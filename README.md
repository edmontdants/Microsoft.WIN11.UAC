# UAC Prompt Replica

A WPF desktop application that renders a **pixel-faithful replica of the Windows 11 UAC (User Account Control) credential prompt** — the familiar *"Do you want to allow this app to make changes to your device?"* dialog that asks for an administrator username and password.

It reproduces the full secure-desktop experience: the dimmed wallpaper backdrop, the UAC chime, a keyboard lockdown that swallows Win / Alt+Tab / Ctrl+Esc, the detected-account tile with *"Use another account"*, the custom blinking caret, the press-and-hold password reveal, live theme/accent matching, and the exact Windows failure text. Typed credentials are checked against a **real Windows account** via the Win32 `LogonUser` API, and the dialog only dismisses when a valid **administrator** credential is entered.

The `real uac screen ui behavior/` folder holds reference screenshots of the genuine Windows dialog; every UI change is measured against them.

---

> ## ⚠️ Educational and research use only
>
> **This project exists to demonstrate that the Windows UAC prompt can be convincingly spoofed — a security-awareness lesson, not a product.** A dialog that *looks* exactly like UAC is not proof that it *is* UAC. On the real secure desktop, no ordinary application can draw over it; a look-alike running on the normal desktop can.
>
> - Use it **only** on systems you own or are explicitly authorized to test (e.g. your own lab, a sanctioned red-team engagement, a classroom demo, a CTF).
> - **Do not** use it to deceive, phish, or capture credentials from anyone without their informed consent. Presenting a fake credential prompt to trick a person into revealing a password may be a crime (unauthorized access, fraud, wiretapping) in your jurisdiction, regardless of intent.
> - It is a **demonstration artifact**, not a hardened or supported tool, and it ships with no warranty.
>
> The authors and contributors accept no responsibility for misuse. If you are not certain your use is lawful and authorized, **do not run it.**

---

## Why this is interesting

UAC is one of the most-trusted trust signals in Windows: users are trained to type their admin password the moment that dark, centered dialog appears. This project shows, concretely, how much of that trust rests on *appearance* rather than on the genuine secure desktop — and it does so by faithfully reproducing the real dialog's many small behaviors (watermarks that persist until you type, the `Domain:` line tracking the typed authority, the reappear-with-error flicker, the filtered-vs-elevated admin token check). For defenders and educators it is a tangible answer to *"why does Microsoft isolate UAC on a separate secure desktop?"*

## Requirements

- **Windows** (the app depends on Win32 APIs, the registry, and the WPF desktop — it will not run elsewhere)
- **.NET 10 SDK** (targets `net10.0-windows`)
- Windows 11 recommended for the closest visual match

## Build & run

```powershell
dotnet build                                 # build (net10.0-windows)
dotnet run --project VSIXInstaller            # build + launch the dialog
dotnet build -c Release                       # release build
```

There is no test project, linter, or CI. Verification is **manual and visual**: run the app and compare it against the reference screenshots in `real uac screen ui behavior/`.

## How it behaves

- On open, the backdrop, dim overlay, and UAC sound appear together, then the card fades in after a short randomized settle — mirroring the real prompt's timing. Submitting credentials replays that settle while validation runs on a background thread, then either the prompt exits (valid admin) or the card returns carrying the authentic Windows failure message.
- Credentials are authenticated with `LogonUser` (network logon first, with an interactive/cached-credential fallback for offline domain accounts) and gated on **built-in Administrators** membership, resolving a UAC split-token admin's linked elevated token when needed. A valid but non-admin account is refused.
- A low-level keyboard hook approximates the secure desktop by blocking system key chords while the prompt is up.

For a deep, file-by-file description of the architecture and the deliberate UAC quirks the code reproduces, see [`CLAUDE.md`](CLAUDE.md).

## Project layout

| File | Role |
| --- | --- |
| `App.xaml` / `App.xaml.cs` | Startup and a required text-rendering switch |
| `MainWindow.xaml` | The full visual tree, styles, and color brushes |
| `MainWindow.xaml.cs` | All dialog behavior and credential validation |
| `BackdropWindow.xaml` / `.cs` | Full-screen wallpaper + dim layer (secure-desktop look) |
| `VsixPackageLauncher.cs` | Launches Visual Studio's real VSIX installer for a package |
| `OperationLog.cs` | Appends success/failure events to a session log under Temp |

## License

No license file is present. Absent an explicit license, this code is **all rights reserved** by its author; treat it as reference material and do not redistribute without permission.
