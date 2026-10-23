# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## What this is

A WPF desktop app (`WinExe`) that renders a pixel-faithful replica of the Windows 11 **UAC (User Account Control) credential prompt** — the "Do you want to allow this app to make changes to your device?" dialog that asks for an admin username and password. It is not a real installer and does not elevate; it presents the UAC secure-desktop experience (dimmed wallpaper backdrop, UAC sound, keyboard lockdown) and validates typed credentials against a hardcoded demo rule.

The `real uac screen ui behavior/` folder holds reference screenshots of the genuine Windows dialog; changes to the UI are measured against these.

## Commands

```powershell
dotnet build                                    # build (net10.0-windows)
dotnet run --project VSIXInstaller               # build + launch the dialog
dotnet build -c Release                         # release build
```

There is **no test project, linter, or CI** in this repo. Verification is manual and visual: run the app and compare against the reference screenshots. The app must run on Windows — it depends on Win32 APIs, the registry, and the WPF desktop.

## Architecture

The entire app is three source files; almost all logic lives in `MainWindow`.

- **`App.xaml` / `App.xaml.cs`** — startup (`StartupUri="MainWindow.xaml"`). The static ctor sets `Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering = false` (also declared in the `.csproj` as a `RuntimeHostConfigurationOption`). This is **required** so `PasswordBox.SelectionTextBrush` renders white password dots on the accent-colored selection; do not remove either declaration.
- **`MainWindow.xaml`** — the full visual tree plus all styles/brushes as `Window.Resources`. The window is borderless, transparent, topmost, full-screen (`WindowStyle=None`, `AllowsTransparency=True`). Layers: `BackdropLayer` (wallpaper `Image` + `DimOverlay`) behind the centered `UacCard` (header / body / footer).
- **`MainWindow.xaml.cs`** — all behavior.

### Key behaviors to preserve

These are deliberate reproductions of real UAC quirks. Treat them as the spec, not incidental code:

- **Open/close/validate sequence** (`PlayOpenSequenceAsync` / `PlayCloseSequenceAsync` / `RunValidationSequenceAsync`): backdrop + dim + UAC sound appear together, then the card fades in after a random 200–500 ms settle (close is a faster 120–350 ms reverse). `Window_Closing` cancels the real close and drives the animated close instead, only actually closing once `_closeFinished` is set. **Submitting credentials replays that settle**: the card vanishes onto the still-locked/dimmed secure desktop for ~200–500 ms while `CheckCredentials` runs on a background thread (`_validating` guards re-entry), then either the whole prompt exits (valid admin → `PlayCloseSequenceAsync`) or the card returns via `ReappearWithError` carrying the failure message. An empty-field submit skips the flicker and shows the hint inline (it never reaches LSA).
- **Detected-account tile + "Use another account"** (`_manualUserName`, `_detectedAccount`, `SetCredentialEntryMode`, `FocusInitialField`): on open the current interactive user (`DOMAIN\user`) is pre-filled as a non-editable tile (`DetectedAccountPanel`) and focus lands on the password. The `UseAnotherAccountButton` link toggles to manual entry (`ManualUserNamePanel` with the editable `UserNameBox`) and back — its caption flips between "Use another account" and "Use &lt;detected account&gt;". Toggling clears the password (it belonged to the other account). `TryAcceptCredentials` reads the user name from `_detectedAccount` in tile mode, `UserNameBox` in manual mode.
- **Watermark stays until you type**: the "User name"/"Password" placeholders show whenever the field is empty — focused or not — and are replaced **only** when a value is typed (real UAC; focusing an empty field keeps the watermark with the caret blinking over it). Driven by a plain `Text=""` `Trigger` on the `TextBox` and the `empty && PasswordBox.Visibility == Visible` check in `UpdatePasswordPlaceholder` (no focus term).
- **`Domain:` line tracks the typed authority** (`UpdateDomainLabel`, called on every user-name change): bare name → machine domain, `DOMAIN\user` → that domain, `user@suffix` → the UPN suffix. Reads from `_detectedAccount` in tile mode.
- **Custom caret** (`RestartCaretCycle`, `CaretTimer_Tick`, `PlaceTextBoxCaret`, `PlacePasswordBoxCaret`): the system caret is hidden (`CaretBrush=Transparent`); a manually drawn `Rectangle` on a `Canvas` overlay blinks exactly 3 times then stays solid. Password caret X-position is computed by measuring masked text with `FormattedText`.
- **Password reveal** (press-and-hold eye button): swaps the `PasswordBox` for a plaintext `PasswordRevealBox` (`TextBox`) while held. `_allowPasswordRevealIcon` mirrors real UAC — the eye is offered only during the first focused entry pass and is disabled once focus leaves a filled field until the password is fully cleared. `_syncingPassword` guards the two-way value copy so change handlers don't recurse.
- **Password select-all on focus** (`CredentialField_GotKeyboardFocus`): focusing a filled password field selects the whole value; `_passwordRangeSelected` tracks this manually because `PasswordBox.IsSelectionActive` is unreliable for caret decisions.
- **Keyboard lockdown** (`InstallKeyboardLock` / `KeyboardHookCallback` / `ShouldBlockKey`): a low-level `WH_KEYBOARD_LL` hook approximates the secure desktop by swallowing Win, Alt+Tab, Alt+Esc, Ctrl+Esc, and language-switch chords while the prompt is up. Installed on open, removed on close and in `OnClosed`.
- **Theme + accent** (`ApplyTheme`, `GetSystemAccentColor`, `GetSystemAccentLightColor`): brushes are recolored at runtime from the registry (`AppsUseLightTheme`, DWM `AccentColor` in ABGR, `AccentPalette`). Re-applied live via `SystemEvents.UserPreferenceChanged`. The dim overlay stays dark even in light theme.
- **Wallpaper** (`TryLoadWallpaperImage`): loads the desktop background from `%AppData%\Microsoft\Windows\Themes\TranscodedWallpaper`, falling back to the `Control Panel\Desktop\WallPaper` registry value; drawn with `Stretch=Uniform` (Windows "Fit").

### Credential validation

`CheckCredentials` authenticates the entered username/password against a **real Windows account** (local or domain) via the Win32 `LogonUser` API and returns a three-state `CredentialCheck` (`Invalid` / `NotAdministrator` / `Valid`); the dialog only dismisses (as if "Yes") on `Valid`.

- **Logon + offline fallback**: tries `LOGON32_LOGON_NETWORK` first (cheapest; no full session; needs a reachable DC and the network-logon right), then falls back to `LOGON32_LOGON_INTERACTIVE` (honors *cached* domain credentials, so it works offline) — but **only** on authority-reachability errors (`ShouldRetryWithNextLogonType`: 1385 no logon-type right, 1311 no DC, 1355 no such domain), never on a wrong password (1326), so a bad guess isn't counted twice toward lockout.
- **Admin gate**: after logon, `IsTokenAdministrator` checks the token's built-in Administrators membership via `WindowsPrincipal.IsInRole`. A UAC split-token admin's interactive logon yields a *filtered* token whose Administrators SID is deny-only (reads as non-member), so it resolves the linked elevated token (`GetTokenInformation` / `TokenLinkedToken`) and re-checks. Network-logon tokens are unfiltered, so the first check suffices. **A valid but non-admin account is refused** with "This account doesn't have administrator rights."
- **`SplitUserName`** parses input into the domain/account pair `LogonUser` expects: `DOMAIN\user` splits, `user@domain` (UPN) passes through with a null domain, and a bare name defaults to `Environment.UserDomainName` (the current logon authority — the AD domain on a domain-joined PC, the machine name/local SAM on a workgroup PC; use `.\user` to force local).
- **`DescribeLogonFailure`** returns the **authentic Windows text** for the failure via `new Win32Exception(win32Error).Message` — the same strings real UAC surfaces (1326 → "The user name or password is incorrect."; 1311, the off-network/undiscoverable-account case → "We can't sign you in with this credential because your domain isn't available…"). Non-admin gets "This account doesn't have administrator rights."; empty fields get the inline gold hint "Please enter a user name and password."

On failure the dialog clears the password and refocuses it, matching real UAC.

## Conventions

- Nullable and implicit usings are enabled. `Segoe UI Variable` / `Segoe Fluent Icons` fonts and `Segoe MDL2 Assets` glyphs are used to match Windows chrome.
- Win32 interop (`user32`, `winmm`, `kernel32`) is declared via `[DllImport]` at the bottom of `MainWindow.xaml.cs`.
- The many `SolidColorBrush` resource keys in `MainWindow.xaml` are the single source of truth for colors and are overwritten in place by `SetBrush` — reference them with `DynamicResource`, never hardcode hex in new UI.
