using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VSIXInstaller;

public partial class MainWindow : Window
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndAlias = 0x00010000;
    private const uint SndFilename = 0x00020000;
    /// <summary>
    /// Play through the same cross-process "System sounds" session real UAC uses
    /// (Sndvol → System sounds), not the quieter per-app session.
    /// </summary>
    private const uint SndSystem = 0x00200000;

    // LogonUser: verify credentials without opening a full interactive session.
    private const int Logon32LogonNetwork = 3;      // LOGON32_LOGON_NETWORK
    private const int Logon32LogonInteractive = 2;  // LOGON32_LOGON_INTERACTIVE (cached/offline fallback)
    private const int Logon32ProviderDefault = 0;   // LOGON32_PROVIDER_DEFAULT
    private const int ErrorLogonTypeNotGranted = 1385; // ERROR_LOGON_TYPE_NOT_GRANTED
    private const int ErrorNoLogonServers = 1311;      // ERROR_NO_LOGON_SERVERS (no reachable DC)
    private const int ErrorNoSuchDomain = 1355;        // ERROR_NO_SUCH_DOMAIN

    /// <summary>Real UAC status for an invalid / non-elevating account attempt.</summary>
    private const string ElevationRequiredMessage = "The requested operation requires elevation.";
    private const int TokenLinkedToken = 19;           // TOKEN_INFORMATION_CLASS.TokenLinkedToken

    private bool _detailsExpanded;
    private bool _isClosing;
    private bool _closeFinished;
    private bool _validating;
    /// <summary>
    /// False = known/cached-account tile (password only); true = editable User name + Password.
    /// Starter screen is always manual (both fields), matching real UAC.
    /// </summary>
    private bool _manualUserName = true;
    /// <summary>SAM form used for LogonUser when in known-account mode, e.g. "STP\moaz.ibrahim".</summary>
    private string _resolvedSam = string.Empty;
    private string _displayName = string.Empty;
    private bool _showSamAccountLine;
    private bool _moreChoicesVisible;
    private bool _moreChoicesExpanded;
    /// <summary>Window height locked when More choices opens (real UAC does not grow the card).</summary>
    private double? _heightBeforeMoreChoices;
    /// <summary>0 = current SAM tile (default active selection), 1 = use a different account.</summary>
    private int _moreChoicesSelectedIndex;
    /// <summary>Which tile has the keyboard focus ring (arrows move this; Tab treats the list as one stop).</summary>
    private int _moreChoicesFocusIndex;
    private bool _syncingPassword;
    private bool _passwordRevealed;
    /// <summary>
    /// Real UAC: reveal eye is only offered during the first focused entry pass until the
    /// password is fully cleared; losing focus disables it until a full clear.
    /// </summary>
    private bool _allowPasswordRevealIcon = true;
    /// <summary>
    /// True only after PasswordBox.SelectAll(); cleared when typing/clicking collapses the range.
    /// (PasswordBox.IsSelectionActive is not reliable for caret visibility.)
    /// </summary>
    private bool _passwordRangeSelected;
    private bool _passwordFieldHadFocusBeforeReveal;
    private bool _suppressPasswordSelectAll;
    /// <summary>
    /// Real UAC auto-moves focus to Password only on the *first* empty-credentials submit.
    /// Later empty Yes/Enter while the user is on User name must leave focus alone.
    /// </summary>
    private bool _autoFocusedPasswordOnEmptyCreds;

    /// <summary>
    /// When true, ReappearWithError / FocusInitialField / eye-button may move focus to Password.
    /// Cleared as soon as User name takes focus so nothing can bounce it back.
    /// </summary>
    private bool _passwordFocusAllowed = true;
    private int _passwordCaretIndex;
    private BackdropWindow? _backdrop;
    private MediaPlayer? _uacSoundPlayer;
    private string? _uacAmplifiedTempPath;
    private Control? _caretTarget;
    private readonly DispatcherTimer _caretTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private int _caretTick;

    // Low-level keyboard hook (secure-desktop approximation while prompt is visible).
    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkTab = 0x09;
    private const int VkEscape = 0x1B;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkShift = 0x10;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12; // Alt
    private const int LlkhfAltdown = 0x20;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private LowLevelKeyboardProc? _keyboardProc;
    private bool _reclaimingFocus;
    private readonly DispatcherTimer _focusWatchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public MainWindow()
    {
        InitializeComponent();
        UacCard.Visibility = Visibility.Hidden;
        SizeDialogForScreen();
        CenterDialogOnScreen();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _caretTimer.Tick += CaretTimer_Tick;
        _focusWatchTimer.Tick += FocusWatchTimer_Tick;
        Deactivated += Window_Deactivated;
        SourceInitialized += (_, _) => RestrictSystemMenuToMoveAndClose();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Re-apply in case DPI/layout settled after first measure.
        SizeDialogForScreen();
        CenterDialogOnScreen();
        ApplyTheme(IsAppsLightTheme());

        var appName = "VSIX Installer";
        AppNameText.Text = appName;
        CredentialHintText.Text = "To continue, enter an admin username and password.";

        var exePath = Environment.ProcessPath ?? @"C:\Program Files\VSIXInstaller\VSIXInstaller.exe";
        ProgramLocationText.Text = $"Program location: \"{exePath}\"";

        OperationLog.Info("SessionStart",
            $"user={Environment.UserDomainName}\\{Environment.UserName}; testVsix={App.TestVsixLaunch}; logDir={OperationLog.LogDirectory}; file={OperationLog.CurrentSessionFile}");
        if (App.TestVsixLaunch)
            OperationLog.Info("TestVsixMode", "Submitting the current logged-in user skips auth and launches the VSIX package.");

        // Real UAC credential prompt opens with both User name + Password; no account tile /
        // "Use another account" until a known/cached account is selected.
        ShowManualEntry(clearUserName: true);

        await PlayOpenSequenceAsync();
        OperationLog.Success("PromptOpened");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsEscapeKey(e))
        {
            Dismiss();
            e.Handled = true;
            return;
        }

        if (_isClosing)
        {
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            if (e.SystemKey == Key.Y)
            {
                TryAcceptCredentials();
                e.Handled = true;
                return;
            }

            if (e.SystemKey == Key.N)
            {
                Dismiss();
                e.Handled = true;
                return;
            }
        }

        // More choices list: Up/Down only moves keyboard focus (white ring), not the selected tile.
        // Tab treats the list as one stop (see SyncMoreChoicesListTabStops).
        if (_moreChoicesExpanded &&
            (CurrentAccountChoiceButton.IsKeyboardFocused || DifferentAccountChoiceButton.IsKeyboardFocused) &&
            e.Key is Key.Up or Key.Down)
        {
            var focusIndex = e.Key == Key.Down ? 1 : 0;
            FocusMoreChoicesTile(focusIndex);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (NoButton.IsKeyboardFocusWithin)
            {
                Dismiss();
                e.Handled = true;
                return;
            }

            if (YesButton.IsKeyboardFocusWithin ||
                UserNameBox.IsKeyboardFocusWithin ||
                PasswordBox.IsKeyboardFocusWithin ||
                PasswordRevealBox.IsKeyboardFocusWithin)
            {
                TryAcceptCredentials();
                e.Handled = true;
            }
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (IsEscapeKey(e))
        {
            Dismiss();
            e.Handled = true;
        }
    }

    private void CloseCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        Dismiss();
        e.Handled = true;
    }

    private static bool IsEscapeKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key == Key.Escape;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeFinished)
            return;

        e.Cancel = true;
        if (!_isClosing)
            _ = PlayCloseSequenceAsync();
    }

    private void SizeDialogForScreen()
    {
        var screenW = SystemParameters.PrimaryScreenWidth;
        var cardW = Math.Clamp(screenW * 0.30, 420, 480);

        // Real UAC: fixed width, height hugs content (no tall empty body).
        SizeToContent = SizeToContent.Height;
        Width = cardW + 48;
        ClearValue(HeightProperty);
        UacCard.Width = cardW;
        UacCard.ClearValue(FrameworkElement.HeightProperty);
        UacCard.ClearValue(FrameworkElement.MaxHeightProperty);
        UacCard.Margin = new Thickness(24);

        // Real UAC credential boxes sit inset (~78% of card width), not edge-to-edge.
        var fieldsW = Math.Clamp(cardW * 0.78, 300, 360);
        CredentialFieldsPanel.MaxWidth = fieldsW;
        CredentialFieldsPanel.Width = fieldsW;
        // Status text spans the dialog body (card content), not the narrow text-box column.
        // Body StackPanel margins are 24px each side.
        CredentialErrorText.ClearValue(FrameworkElement.WidthProperty);
        CredentialErrorText.MaxWidth = Math.Max(0, cardW - 48);
    }

    /// <summary>
    /// Center after Width/Height are known — WindowStartupLocation runs too early (pre-size).
    /// </summary>
    private void CenterDialogOnScreen()
    {
        UpdateLayout();
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = (SystemParameters.PrimaryScreenHeight - ActualHeight) / 2;
    }

    /// <summary>
    /// Re-measure after content changes and lock height to content (no scrollbar).
    /// Only while the More choices list is expanded do we keep a fixed height and scroll.
    /// </summary>
    private void RefitDialogToContent()
    {
        if (_moreChoicesExpanded && _heightBeforeMoreChoices is double locked)
        {
            ApplyLockedHeightWithBodyScroll(locked);
            return;
        }

        ApplyDialogHeightAfterContentChange();
    }

    private void LockDialogHeight()
    {
        UpdateLayout();
        if (ActualHeight <= 0)
            return;

        SizeToContent = SizeToContent.Manual;
        Height = ActualHeight;
    }

    /// <summary>
    /// Grow/shrink window height to content without recentering (avoids a visible jump).
    /// </summary>
    private void FitDialogHeightToContent()
    {
        var top = Top;
        var left = Left;
        if (_moreChoicesExpanded && _heightBeforeMoreChoices is double locked)
        {
            ApplyLockedHeightWithBodyScroll(locked);
        }
        else
        {
            ApplyDialogHeightAfterContentChange(recenter: false);
        }

        Left = left;
        Top = Math.Min(top, SystemParameters.PrimaryScreenHeight - ActualHeight);
    }

    /// <summary>Measure the card to content height (no body scrollbar) and lock it.</summary>
    private void ApplyDialogHeightAfterContentChange(bool recenter = true)
    {
        BodyRow.Height = GridLength.Auto;
        SizeToContent = SizeToContent.Height;
        ClearValue(HeightProperty);
        ClearValue(MaxHeightProperty);
        ClearValue(MinHeightProperty);
        UacCard.ClearValue(FrameworkElement.MaxHeightProperty);
        UacCard.ClearValue(FrameworkElement.HeightProperty);
        UpdateLayout();
        if (recenter)
            CenterDialogOnScreen();
        LockDialogHeight();
    }

    /// <summary>
    /// Real UAC: expanding More choices keeps the same window size; the body scrolls.
    /// </summary>
    private void ApplyLockedHeightWithBodyScroll(double lockedHeight)
    {
        BodyRow.Height = new GridLength(1, GridUnitType.Star);
        SizeToContent = SizeToContent.Manual;
        MaxHeight = lockedHeight;
        Height = lockedHeight;
        UacCard.ClearValue(FrameworkElement.HeightProperty);
        UacCard.MaxHeight = Math.Max(0, lockedHeight - UacCard.Margin.Top - UacCard.Margin.Bottom);
        UpdateLayout();
    }

    private async Task PlayOpenSequenceAsync()
    {
        UacCard.Visibility = Visibility.Hidden;
        IsHitTestVisible = false;

        _backdrop ??= new BackdropWindow();
        if (TryFindResource("DimOverlayBrush") is Brush dim)
            _backdrop.ApplyDimBrush(dim);

        // 1) Wallpaper + dim + UAC sound — keep dimmer on top so the empty dialog never flashes.
        _backdrop.Show();
        EnsureBackdropAboveDialog();
        InstallKeyboardLock();

        // Grab/lock foreground immediately — before the settle delay — so Alt/Ctrl held at
        // launch cannot leave focus in another app while the UAC chrome is already visible.
        _focusWatchTimer.Interval = TimeSpan.FromMilliseconds(50);
        StartFocusWatch();
        ForceSecureDesktopFocus();
        PlayWindowsUacSound();

        // 2) Short settle, then the dialog appears (200–500 ms).
        await Task.Delay(Random.Shared.Next(200, 501));
        if (_isClosing)
            return;

        CenterDialogOnScreen();
        UacCard.Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        LockDialogHeight();
        EnsureDialogAboveBackdrop();

        ForceSecureDesktopFocus();
        FocusInitialField();
        _focusWatchTimer.Interval = TimeSpan.FromMilliseconds(100);
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isClosing || _reclaimingFocus)
            return;

        // Another app stole activation (e.g. Alt/Ctrl held at launch) — pull focus back now.
        Dispatcher.BeginInvoke(ForceSecureDesktopFocus, DispatcherPriority.Send);
    }

    private void StartFocusWatch()
    {
        if (!_focusWatchTimer.IsEnabled)
            _focusWatchTimer.Start();
    }

    private void StopFocusWatch()
    {
        _focusWatchTimer.Stop();
    }

    private void FocusWatchTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || _reclaimingFocus)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        if (GetForegroundWindow() != hwnd)
            ForceSecureDesktopFocus();
    }

    /// <summary>
    /// Real UAC's secure desktop owns input exclusively. Approximate that by forcing this
    /// HWND to the foreground (AttachThreadInput + Alt-bump + SwitchToThisWindow) and locking
    /// foreground ownership for the life of the prompt.
    /// </summary>
    private void ForceSecureDesktopFocus()
    {
        if (_isClosing || _reclaimingFocus)
            return;

        _reclaimingFocus = true;
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            if (!TrySetForeground(hwnd))
            {
                // Windows often denies SetForegroundWindow when Alt/Ctrl were held at launch.
                // A synthetic Alt press temporarily grants foreground permission.
                SimulateAltKeyBump();
                TrySetForeground(hwnd);
            }

            // Last-resort activation used by shell-style switchers.
            SwitchToThisWindow(hwnd, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);

            EnsureDialogAboveBackdrop();
            Activate();

            // Once we own foreground, stop other processes from taking it back.
            LockSetForegroundWindow(LsfwLock);

            if (UacCard.Visibility == Visibility.Visible && !IsKeyboardFocusWithin)
                FocusInitialField();
        }
        finally
        {
            _reclaimingFocus = false;
        }
    }

    private bool TrySetForeground(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hwnd)
            return true;

        if (foreground != IntPtr.Zero)
        {
            var foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
            var thisThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 &&
                           foregroundThread != thisThread &&
                           AttachThreadInput(foregroundThread, thisThread, true);
            try
            {
                BringWindowToTop(hwnd);
                ShowWindow(hwnd, SwShow);
                return SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd;
            }
            finally
            {
                if (attached)
                    AttachThreadInput(foregroundThread, thisThread, false);
            }
        }

        return SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd;
    }

    private static void SimulateAltKeyBump()
    {
        // Empty Alt press is a known way to satisfy the foreground-lock rules when the user
        // still has modifiers down from launching the process.
        keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
        keybd_event(VkMenu, 0, KeyeventfKeyup, UIntPtr.Zero);
    }

    private bool IsDialogForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// During the settle, the dimmer must cover the (still empty) dialog HWND.
    /// </summary>
    private void EnsureBackdropAboveDialog()
    {
        Topmost = true;
        var dialogHwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(
            dialogHwnd,
            HwndTopmost,
            0, 0, 0, 0,
            SwpNomove | SwpNosize | SwpNoactivate);

        if (_backdrop is null)
            return;

        _backdrop.Topmost = true;
        var backdropHwnd = new WindowInteropHelper(_backdrop).Handle;
        if (backdropHwnd == IntPtr.Zero)
            return;

        SetWindowPos(
            backdropHwnd,
            HwndTopmost,
            0, 0, 0, 0,
            SwpNomove | SwpNosize | SwpNoactivate);
    }

    /// <summary>
    /// Both windows are Topmost; the last HWND raised to HWND_TOPMOST wins the stack.
    /// </summary>
    private void EnsureDialogAboveBackdrop()
    {
        if (_backdrop is not null)
        {
            _backdrop.Topmost = true;
            var backdropHwnd = new WindowInteropHelper(_backdrop).Handle;
            if (backdropHwnd != IntPtr.Zero)
            {
                SetWindowPos(
                    backdropHwnd,
                    HwndTopmost,
                    0, 0, 0, 0,
                    SwpNomove | SwpNosize | SwpNoactivate);
            }
        }

        Topmost = true;
        var dialogHwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(
            dialogHwnd,
            HwndTopmost,
            0, 0, 0, 0,
            SwpNomove | SwpNosize | SwpShowWindow);
    }

    /// <summary>
    /// Focus lands on the password in detected mode (username is already known) and on the
    /// user name field in manual mode — matching where real UAC's caret starts in each.
    /// </summary>
    private void FocusInitialField()
    {
        if (_manualUserName)
        {
            _passwordFocusAllowed = false;
            UserNameBox.Focus();
            Keyboard.Focus(UserNameBox);
            UserNameBox.CaretIndex = 0;
            RestartCaretCycle(UserNameBox);
        }
        else
        {
            FocusPasswordField();
        }

        UpdateClearUserNameButtonVisibility();
        UpdatePasswordPlaceholder();
        UpdateRevealPasswordButtonVisibility();
    }

    private void FocusPasswordField()
    {
        _passwordFocusAllowed = true;
        PasswordBox.Focus();
        Keyboard.Focus(PasswordBox);
        RestartCaretCycle(PasswordBox);
        // User may leave immediately; further programmatic password focus requires a new allow.
        _passwordFocusAllowed = false;
    }

    /// <summary>Toggle the More choices account list (keep current SAM vs use a different account).</summary>
    private void MoreChoicesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_moreChoicesExpanded)
            CollapseMoreChoicesList();
        else
            ExpandMoreChoicesList();
    }

    private void ExpandMoreChoicesList()
    {
        // Snapshot the fitted height, then keep it while the list is open (scroll instead of grow).
        UpdateLayout();
        _heightBeforeMoreChoices = ActualHeight > 0 ? ActualHeight : Height;

        // Same caption rule as the known-account label: current user → full name + SAM under;
        // others → DOMAIN\user only.
        ApplyCurrentAccountChoiceCaption();
        MoreChoicesList.Visibility = Visibility.Visible;
        _moreChoicesExpanded = true;
        // Reflect the active credential mode in the list selection chrome.
        _moreChoicesSelectedIndex = _manualUserName ? 1 : 0;
        _moreChoicesFocusIndex = _moreChoicesSelectedIndex;
        SyncMoreChoicesListTabStops();
        UpdateMoreChoicesTileVisuals();
        OperationLog.Info("MoreChoicesExpanded", $"user={CurrentAccountChoiceText.Text}");

        BodyScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        ApplyLockedHeightWithBodyScroll(_heightBeforeMoreChoices.Value);
        // Leave scroll at the top; user scrolls down to the choices (real UAC).
        BodyScrollViewer.ScrollToHome();

        // Land keyboard focus on the list (one Tab stop); Up/Down moves the ring between tiles.
        Dispatcher.BeginInvoke(() =>
        {
            if (!_moreChoicesExpanded)
                return;
            FocusMoreChoicesTile(_moreChoicesFocusIndex);
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// First More choices tile: current interactive user shows full name with DOMAIN\user under it;
    /// any other account shows only DOMAIN\user (no subtitle).
    /// </summary>
    private void ApplyCurrentAccountChoiceCaption()
    {
        if (string.IsNullOrEmpty(_resolvedSam))
        {
            CurrentAccountChoiceText.Text = FormatSamAccount(UserNameBox.Text ?? string.Empty);
            CurrentAccountChoiceSamText.Visibility = Visibility.Collapsed;
            CurrentAccountChoiceSamText.Text = string.Empty;
            return;
        }

        var display = string.IsNullOrEmpty(_displayName) ? _resolvedSam : _displayName;
        var resolved = new ResolvedAccount(_resolvedSam, display, _showSamAccountLine);
        if (IsCurrentInteractiveUser(resolved))
        {
            CurrentAccountChoiceText.Text = display;
            CurrentAccountChoiceSamText.Text = _resolvedSam;
            CurrentAccountChoiceSamText.Visibility = Visibility.Visible;
        }
        else
        {
            CurrentAccountChoiceText.Text = _resolvedSam;
            CurrentAccountChoiceSamText.Text = string.Empty;
            CurrentAccountChoiceSamText.Visibility = Visibility.Collapsed;
        }
    }

    private void CollapseMoreChoicesList(bool log = true)
    {
        if (!_moreChoicesExpanded && MoreChoicesList.Visibility != Visibility.Visible)
            return;

        _moreChoicesExpanded = false;
        MoreChoicesList.Visibility = Visibility.Collapsed;
        _heightBeforeMoreChoices = null;
        SyncMoreChoicesListTabStops();

        BodyScrollViewer.ScrollToHome();
        BodyScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        BodyRow.Height = GridLength.Auto;
        ClearValue(MaxHeightProperty);
        ClearValue(MinHeightProperty);
        ClearValue(HeightProperty);
        UacCard.ClearValue(FrameworkElement.MaxHeightProperty);
        UacCard.ClearValue(FrameworkElement.HeightProperty);

        if (log)
            OperationLog.Info("MoreChoicesCollapsed");

        // Defer one layout pass so Collapsed list is excluded from SizeToContent measure.
        Dispatcher.BeginInvoke(() =>
        {
            if (_moreChoicesExpanded || _isClosing)
                return;

            var top = Top;
            var left = Left;
            SizeToContent = SizeToContent.Height;
            ClearValue(HeightProperty);
            ClearValue(MaxHeightProperty);
            UpdateLayout();
            LockDialogHeight();
            Left = left;
            Top = Math.Min(top, SystemParameters.PrimaryScreenHeight - ActualHeight);
        }, DispatcherPriority.Loaded);
    }

    private void ClearMoreChoicesScrollClamp()
    {
        _heightBeforeMoreChoices = null;
        BodyRow.Height = GridLength.Auto;
        ClearValue(MaxHeightProperty);
        ClearValue(MinHeightProperty);
        ClearValue(HeightProperty);
        UacCard.ClearValue(FrameworkElement.MaxHeightProperty);
        UacCard.ClearValue(FrameworkElement.HeightProperty);
    }

    private void MoreChoicesTile_MouseEnter(object sender, MouseEventArgs e)
    {
        UpdateMoreChoicesTileVisuals();
        if (sender is Button button)
            UpdateMoreChoicesRevealLight(button, e.GetPosition(button), show: true);
    }

    private void MoreChoicesTile_MouseLeave(object sender, MouseEventArgs e)
    {
        UpdateMoreChoicesTileVisuals();
        if (sender is Button button)
            UpdateMoreChoicesRevealLight(button, default, show: false);
    }

    private void MoreChoicesTile_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
            UpdateMoreChoicesRevealLight(button, e.GetPosition(button), show: true);
    }

    /// <summary>
    /// Full-tile soft white spot (ambient wash) with a gentle brighter center at the pointer —
    /// not a condensed/wave falloff. Only the hovered tile is updated.
    /// </summary>
    private static void UpdateMoreChoicesRevealLight(Button button, Point position, bool show)
    {
        if (button.Template?.FindName("RevealLight", button) is not Border reveal)
            return;

        var ambient = button.Template.FindName("RevealAmbient", button) as Border;

        if (!show)
        {
            reveal.Opacity = 0;
            if (ambient is not null)
                ambient.Opacity = 0;
            return;
        }

        if (ambient is not null)
            ambient.Opacity = 1;

        reveal.Opacity = 1;
        if (reveal.Background is not RadialGradientBrush brush)
            return;

        // RadialGradientBrush may be frozen from the template — clone before moving the center.
        if (brush.IsFrozen)
        {
            brush = brush.Clone();
            reveal.Background = brush;
        }

        brush.Center = position;
        brush.GradientOrigin = position;

        // Radius reaches the farthest corner so the soft boost covers the whole tile.
        var width = Math.Max(button.ActualWidth, 1);
        var height = Math.Max(button.ActualHeight, 1);
        var farthest = Math.Max(
            Math.Max(Distance(position, 0, 0), Distance(position, width, 0)),
            Math.Max(Distance(position, 0, height), Distance(position, width, height)));
        var radius = Math.Max(farthest * 1.15, 1);
        brush.RadiusX = radius;
        brush.RadiusY = radius;
    }

    private static double Distance(Point from, double x, double y)
    {
        var dx = from.X - x;
        var dy = from.Y - y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Selected = translucent Windows accent. Hover on the selected row (or dim when a sibling
    /// is hovered) adds a transparent black wash so the accent reads darker. Unselected hover
    /// is only a light black wash to lift the row off the body.
    /// </summary>
    private void UpdateMoreChoicesTileVisuals()
    {
        if (!_moreChoicesExpanded)
            return;

        var selected = TryFindResource("MoreChoicesSelectedBrush") as Brush ?? Brushes.Transparent;
        var selectedDim = TryFindResource("MoreChoicesSelectedDimBrush") as Brush ?? selected;
        var hover = TryFindResource("MoreChoicesHoverBrush") as Brush ?? Brushes.Transparent;
        var selectedHover = TryFindResource("MoreChoicesSelectedHoverBrush") as Brush ?? selected;
        var clear = Brushes.Transparent;

        var currentHover = CurrentAccountChoiceButton.IsMouseOver;
        var differentHover = DifferentAccountChoiceButton.IsMouseOver;
        var otherHovering = currentHover || differentHover;

        CurrentAccountChoiceButton.Background = ResolveMoreChoicesTileBrush(
            isSelected: _moreChoicesSelectedIndex == 0,
            isHover: currentHover,
            siblingHovered: otherHovering && !currentHover,
            selected, selectedDim, hover, selectedHover, clear);

        DifferentAccountChoiceButton.Background = ResolveMoreChoicesTileBrush(
            isSelected: _moreChoicesSelectedIndex == 1,
            isHover: differentHover,
            siblingHovered: otherHovering && !differentHover,
            selected, selectedDim, hover, selectedHover, clear);
    }

    private static Brush ResolveMoreChoicesTileBrush(
        bool isSelected,
        bool isHover,
        bool siblingHovered,
        Brush selected,
        Brush selectedDim,
        Brush hover,
        Brush selectedHover,
        Brush clear)
    {
        if (isSelected && isHover)
            return selectedHover;
        if (isSelected && siblingHovered)
            return selectedDim;
        if (isSelected)
            return selected;
        if (isHover)
            return hover;
        return clear;
    }

    private void CurrentAccountChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        SelectMoreChoicesTile(0, focusCredentialFields: true);
    }

    private void DifferentAccountChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        SelectMoreChoicesTile(1, focusCredentialFields: true);
    }

    /// <summary>
    /// Selects a More choices tile (accent wash + credential layout). Click/Enter only —
    /// Up/Down only moves keyboard focus (white ring) without changing selection.
    /// </summary>
    private void SelectMoreChoicesTile(int index, bool focusCredentialFields)
    {
        if (index == 0)
        {
            var sam = string.IsNullOrEmpty(_resolvedSam)
                ? CurrentAccountChoiceText.Text
                : _resolvedSam;
            if (string.IsNullOrEmpty(_resolvedSam))
                _resolvedSam = sam;
            if (string.IsNullOrEmpty(_displayName))
                _displayName = sam;

            OperationLog.Info("MoreChoicesKeepAccount", $"user={sam}");
            _moreChoicesSelectedIndex = 0;
            // Real UAC: list stays open; only the credential fields swap. No resize / collapse.
            // Status text stays (real UAC keeps the validation error while switching tiles).
            ApplyKnownAccountCredentialLayout();
            ClearPasswordFields();
            _allowPasswordRevealIcon = true;
            _passwordRangeSelected = false;
            UpdatePasswordPlaceholder();
            UpdateRevealPasswordButtonVisibility();
        }
        else
        {
            OperationLog.Info("MoreChoicesDifferentAccount", $"user={_resolvedSam}");
            _moreChoicesSelectedIndex = 1;
            // Real UAC: list stays open; User name appears, label hides. No resize / collapse.
            // Status text stays (real UAC keeps the validation error while switching tiles).
            ApplyDifferentAccountCredentialLayout();
            ClearPasswordFields();
            _allowPasswordRevealIcon = true;
            _passwordRangeSelected = false;
            UpdateDomainLabel();
            UpdateClearUserNameButtonVisibility();
            UpdatePasswordPlaceholder();
            UpdateRevealPasswordButtonVisibility();
        }

        UpdateMoreChoicesTileVisuals();
        KeepMoreChoicesHeightLocked();

        if (focusCredentialFields)
        {
            if (index == 0)
                FocusPasswordField();
            else
                FocusInitialField();
        }
        else
        {
            FocusMoreChoicesTile(index);
        }
    }

    private void FocusMoreChoicesTile(int index)
    {
        _moreChoicesFocusIndex = index is 0 or 1 ? index : 0;
        SyncMoreChoicesListTabStops();
        var button = _moreChoicesFocusIndex == 0
            ? CurrentAccountChoiceButton
            : DifferentAccountChoiceButton;
        button.Focus();
        Keyboard.Focus(button);
    }

    /// <summary>
    /// The account list is one Tab stop: only the focused tile is in the tab order.
    /// Up/Down moves focus between tiles without an extra Tab stop.
    /// </summary>
    private void SyncMoreChoicesListTabStops()
    {
        if (!_moreChoicesExpanded)
        {
            CurrentAccountChoiceButton.IsTabStop = false;
            DifferentAccountChoiceButton.IsTabStop = false;
            return;
        }

        CurrentAccountChoiceButton.IsTabStop = _moreChoicesFocusIndex == 0;
        DifferentAccountChoiceButton.IsTabStop = _moreChoicesFocusIndex == 1;
    }

    /// <summary>Re-apply the locked height while the More choices list remains open.</summary>
    private void KeepMoreChoicesHeightLocked()
    {
        if (!_moreChoicesExpanded)
            return;

        var locked = _heightBeforeMoreChoices
            ?? (ActualHeight > 0 ? ActualHeight : Height);
        if (locked <= 0)
            return;

        _heightBeforeMoreChoices = locked;
        ApplyLockedHeightWithBodyScroll(locked);
    }

    /// <summary>
    /// Password-only known-account fields (label, no User name box). Keeps More choices visible.
    /// </summary>
    private void ApplyKnownAccountCredentialLayout()
    {
        _manualUserName = false;
        var resolved = new ResolvedAccount(
            _resolvedSam,
            string.IsNullOrEmpty(_displayName) ? _resolvedSam : _displayName,
            _showSamAccountLine);
        var isCurrent = IsCurrentInteractiveUser(resolved);

        DetectedAccountText.Text = isCurrent ? resolved.DisplayName : resolved.Sam;
        DetectedAccountText.Visibility = Visibility.Visible;
        DetectedSamAccountText.Visibility = Visibility.Collapsed;
        ManualUserNamePanel.Visibility = Visibility.Collapsed;

        if (isCurrent)
        {
            DomainText.Visibility = Visibility.Visible;
            UpdateDomainLabel();
        }
        else
        {
            DomainText.Visibility = Visibility.Collapsed;
        }

        RevealMoreChoices();
    }

    /// <summary>
    /// Dual User name + Password fields after "Use a different account" — no HideMoreChoices /
    /// cleared-SAM starter cycle.
    /// </summary>
    private void ApplyDifferentAccountCredentialLayout()
    {
        _manualUserName = true;
        DetectedAccountText.Visibility = Visibility.Collapsed;
        DetectedSamAccountText.Visibility = Visibility.Collapsed;
        ManualUserNamePanel.Visibility = Visibility.Visible;
        DomainText.Visibility = Visibility.Visible;
        UserNameBox.Clear();
        // Keep _resolvedSam / _displayName so the key tile can restore the prior account.
        RevealMoreChoices();
    }

    /// <summary>
    /// Real UAC shows "More choices" only once a user account is selected (password-only tile),
    /// not on the starter dual-field screen or empty-credentials hint.
    /// </summary>
    private void RevealMoreChoices()
    {
        if (_moreChoicesVisible && MoreChoicesButton.Visibility == Visibility.Visible)
            return;

        _moreChoicesVisible = true;
        MoreChoicesButton.Visibility = Visibility.Visible;
    }

    private void HideMoreChoices()
    {
        CollapseMoreChoicesList(log: false);

        if (!_moreChoicesVisible && MoreChoicesButton.Visibility == Visibility.Collapsed)
            return;

        _moreChoicesVisible = false;
        MoreChoicesButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>Starter / unknown-account layout: both User name + Password, no display-name label.</summary>
    private void ShowManualEntry(bool clearUserName)
    {
        _manualUserName = true;
        _resolvedSam = string.Empty;
        _displayName = string.Empty;
        _showSamAccountLine = false;

        DetectedAccountText.Visibility = Visibility.Collapsed;
        ManualUserNamePanel.Visibility = Visibility.Visible;
        DetectedSamAccountText.Visibility = Visibility.Collapsed;
        DomainText.Visibility = Visibility.Visible;
        HideMoreChoices();
        ClearMoreChoicesScrollClamp();

        HideCredentialError();
        if (clearUserName)
            UserNameBox.Clear();

        ClearPasswordFields();
        _allowPasswordRevealIcon = true;
        _passwordRangeSelected = false;

        UpdateDomainLabel();
        FocusInitialField();
        RefitDialogToContent();
        OperationLog.Info("ShowManualEntry", $"clearUserName={clearUserName}");
    }

    /// <summary>
    /// Known/cached account after a failed try: user label + password (+ error + More choices).
    /// Current user → full name + Domain: under password; other users → DOMAIN\user, no Domain: line.
    /// </summary>
    private void ShowKnownAccount(ResolvedAccount resolved, bool clearPassword = true)
    {
        CollapseMoreChoicesList(log: false);

        _resolvedSam = resolved.Sam;
        _displayName = resolved.DisplayName;
        _showSamAccountLine = resolved.ShowSamLine;
        ApplyKnownAccountCredentialLayout();

        HideCredentialError();
        if (clearPassword)
            ClearPasswordFields();

        _allowPasswordRevealIcon = true;
        _passwordRangeSelected = false;

        FocusInitialField();
        RefitDialogToContent();
        OperationLog.Info("ShowKnownAccount",
            $"sam={resolved.Sam}; display={resolved.DisplayName}; currentUser={IsCurrentInteractiveUser(resolved)}");
    }

    private static bool IsCurrentInteractiveUser(ResolvedAccount resolved)
    {
        var current = $@"{Environment.UserDomainName}\{Environment.UserName}";
        return resolved.Sam.Equals(current, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="userName"/> is the interactive session account
    /// (bare name, DOMAIN\user, or .\user for local).
    /// </summary>
    private static bool IsCurrentInteractiveUserName(string userName)
    {
        var typed = FormatSamAccount(userName);
        var current = $@"{Environment.UserDomainName}\{Environment.UserName}";
        if (typed.Equals(current, StringComparison.OrdinalIgnoreCase))
            return true;

        // Local machine SAM vs UserDomainName when they differ (.\user / MACHINE\user).
        var local = $@"{Environment.MachineName}\{Environment.UserName}";
        return typed.Equals(local, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearPasswordFields()
    {
        _syncingPassword = true;
        try
        {
            PasswordBox.Clear();
            PasswordRevealBox.Clear();
            HidePasswordReveal();
            PasswordRevealBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private async Task PlayCloseSequenceAsync()
    {
        _isClosing = true;
        StopFocusWatch();
        LockSetForegroundWindow(LsfwUnlock);
        OperationLog.Info("PromptClosing");
        UninstallKeyboardLock();
        IsHitTestVisible = false;
        StopCaretCycle();
        PasswordBox.Clear();
        PasswordRevealBox.Clear();
        UserNameBox.Clear();
        RevealPasswordButton.ReleaseMouseCapture();
        ClearUserNameButton.ReleaseMouseCapture();
        ClearUserNameButton.Tag = null;
        HidePasswordReveal();
        HideCredentialError();
        CollapseMoreChoicesList(log: false);
        ClearMoreChoicesScrollClamp();
        _allowPasswordRevealIcon = true;
        _passwordRangeSelected = false;
        UpdateClearUserNameButtonVisibility();
        UpdatePasswordPlaceholder();
        UpdateRevealPasswordButtonVisibility();

        // 1) Card disappears first; dimmed desktop stays.
        UacCard.Visibility = Visibility.Hidden;
        IsHitTestVisible = false;

        // 2) Slightly faster settle than open (120–350 ms), then wallpaper + dim drop.
        await Task.Delay(Random.Shared.Next(120, 351));

        if (_backdrop is not null)
        {
            _backdrop.Hide();
            _backdrop.Close();
            _backdrop = null;
        }

        _closeFinished = true;
        OperationLog.Success("PromptClosed");
        Close();
    }

    /// <summary>
    /// Real UAC on the secure desktop reads subjectively louder than app-session playback
    /// at Volume=1.0. Boost PCM ~+6 dB (with clamp) so MediaPlayer can match that level.
    /// </summary>
    private const float UacSoundGain = 2.0f;

    private void PlayWindowsUacSound()
    {
        var path = ResolveWindowsUacWavPath();
        if (path is null)
        {
            PlaySound("WindowsUAC", IntPtr.Zero, SndAsync | SndAlias | SndNoDefault | SndSystem);
            return;
        }

        byte[] wav;
        try
        {
            wav = File.ReadAllBytes(path);
            AmplifyPcmWav(wav, UacSoundGain);
        }
        catch
        {
            PlaySound(path, IntPtr.Zero, SndAsync | SndFilename | SndNoDefault | SndSystem);
            return;
        }

        // Prefer Media Foundation at native 48 kHz, using the boosted copy.
        try
        {
            _uacAmplifiedTempPath ??= Path.Combine(Path.GetTempPath(), "VSIXInstaller.UAC.wav");
            File.WriteAllBytes(_uacAmplifiedTempPath, wav);

            _uacSoundPlayer ??= new MediaPlayer();
            _uacSoundPlayer.Stop();
            _uacSoundPlayer.Volume = 1.0;
            _uacSoundPlayer.Open(new Uri(_uacAmplifiedTempPath, UriKind.Absolute));
            _uacSoundPlayer.Play();
            return;
        }
        catch
        {
            // Fall through to PlaySound.
        }

        PlaySound(path, IntPtr.Zero, SndAsync | SndFilename | SndNoDefault | SndSystem);
    }

    /// <summary>Scales 16-bit PCM samples in a WAV's data chunk; clamps to avoid wrap distortion.</summary>
    private static void AmplifyPcmWav(byte[] wav, float gain)
    {
        if (wav.Length < 44 || gain <= 0)
            return;

        // Walk RIFF chunks to find "data" (header size is not always 44).
        var pos = 12;
        var dataOffset = -1;
        var dataSize = 0;
        while (pos + 8 <= wav.Length)
        {
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (wav[pos] == (byte)'d' && wav[pos + 1] == (byte)'a' &&
                wav[pos + 2] == (byte)'t' && wav[pos + 3] == (byte)'a')
            {
                dataOffset = pos + 8;
                dataSize = size;
                break;
            }

            pos += 8 + size + (size & 1);
            if (size < 0 || pos < 0)
                return;
        }

        if (dataOffset < 0 || dataSize <= 0)
            return;

        var end = Math.Min(wav.Length, dataOffset + dataSize);
        for (var i = dataOffset; i + 1 < end; i += 2)
        {
            var sample = (short)(wav[i] | (wav[i + 1] << 8));
            var scaled = (int)Math.Round(sample * gain);
            scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
            wav[i] = (byte)(scaled & 0xFF);
            wav[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }

    private static string? ResolveWindowsUacWavPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"AppEvents\Schemes\Apps\.Default\WindowsUAC\.Current");
            if (key?.GetValue(null) is string configuredPath && File.Exists(configuredPath))
                return configuredPath;
        }
        catch
        {
            // ignore
        }

        var defaultWav = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Media",
            "Windows User Account Control.wav");

        return File.Exists(defaultWav) ? defaultWav : null;
    }

    private void DetailsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _detailsExpanded = !_detailsExpanded;
        ProgramLocationText.Visibility = _detailsExpanded ? Visibility.Visible : Visibility.Collapsed;
        DetailsToggleButton.Content = _detailsExpanded ? "Hide details" : "Show more details";
        OperationLog.Info(_detailsExpanded ? "ShowMoreDetails" : "HideDetails");
        RefitDialogToContent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        OperationLog.Info("CloseClicked");
        Dismiss();
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        var userName = (_manualUserName ? UserNameBox.Text : _resolvedSam) ?? string.Empty;
        var password = _passwordRevealed ? PasswordRevealBox.Text : PasswordBox.Password;
        OperationLog.Info("YesClicked",
            $"user={(_manualUserName ? FormatSamAccount(userName) : userName)}; password={password}");
        TryAcceptCredentials();
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        OperationLog.Info("NoClicked");
        Dismiss();
    }

    private async void TryAcceptCredentials()
    {
        if (_isClosing || _validating)
            return;

        // Real UAC: do not Trim — spaces-only still counts as a typed user name.
        var userName = (_manualUserName ? UserNameBox.Text : _resolvedSam) ?? string.Empty;
        var password = _passwordRevealed
            ? PasswordRevealBox.Text
            : PasswordBox.Password;

        var userEmpty = string.IsNullOrEmpty(userName);
        var passwordEmpty = string.IsNullOrEmpty(password);

        // Optional --test-vsix: current logged-in user skips LogonUser and exercises VSIX launch.
        if (App.TestVsixLaunch && !userEmpty && IsCurrentInteractiveUserName(userName))
        {
            await RunTestVsixSequenceAsync(userName, password);
            return;
        }

        if (userEmpty && passwordEmpty)
        {
            // Dual-field empty submit: status only — More choices waits until a user is selected.
            // First time: move to Password (real UAC). Later: keep whatever field the user is on.
            var focus = _autoFocusedPasswordOnEmptyCreds
                ? CredentialErrorFocus.Preserve
                : CredentialErrorFocus.Password;
            _autoFocusedPasswordOnEmptyCreds = true;
            OperationLog.Fail("ValidateCredentials",
                $"user=; password=; reason=empty_username_and_password");
            ReappearWithError("Please enter a user name and password.", focus);
            return;
        }

        // Username only (Enter/Yes with empty password): same blink cycle, then tile or empty hint.
        if (!userEmpty && passwordEmpty)
        {
            await RunUsernameOnlySequenceAsync(userName);
            return;
        }

        if (userEmpty)
        {
            var focus = _autoFocusedPasswordOnEmptyCreds
                ? CredentialErrorFocus.Preserve
                : CredentialErrorFocus.Password;
            _autoFocusedPasswordOnEmptyCreds = true;
            OperationLog.Fail("ValidateCredentials",
                $"user=; password={password}; reason=empty_username");
            ReappearWithError("Please enter a user name and password.", focus);
            return;
        }

        await RunValidationSequenceAsync(userName, password);
    }

    /// <summary>
    /// Test-only path (<c>--test-vsix</c>): same blink as real validation, then launch VSIX + close.
    /// </summary>
    private async Task RunTestVsixSequenceAsync(string userName, string password)
    {
        _validating = true;
        try
        {
            HideCredentialError();
            StopCaretCycle();
            UacCard.IsHitTestVisible = false;
            UacCard.Visibility = Visibility.Hidden;

            await Task.Delay(333);
            if (_isClosing)
                return;

            OperationLog.Success("ValidateCredentials",
                $"user={FormatSamAccount(userName)}; password={password}; status=Valid; mode=test-vsix");
            LaunchResXManagerVsix();
            await PlayCloseSequenceAsync();
        }
        finally
        {
            _validating = false;
        }
    }

    /// <summary>
    /// Username-only submit: card blinks like a full validation, then returns as known-account
    /// tile (resolvable/cached) or stays on dual fields with the authentic Win32 status for
    /// unknown names (e.g. no logon servers when off-network).
    /// </summary>
    private async Task RunUsernameOnlySequenceAsync(string userName)
    {
        _validating = true;
        try
        {
            HideCredentialError();
            StopCaretCycle();
            UacCard.IsHitTestVisible = false;
            UacCard.Visibility = Visibility.Hidden;

            var resolveTask = Task.Run(() =>
                TryResolveAccount(userName, out var resolved) ? resolved : (ResolvedAccount?)null);
            // Even with an empty password, probe LSA so unknown off-network names get the
            // real Win32 message instead of the generic empty-fields hint.
            var authTask = Task.Run(() => CheckCredentials(userName, string.Empty));
            await Task.Delay(333);
            var resolved = await resolveTask;
            var result = await authTask;
            if (_isClosing)
                return;

            if (resolved is { } known)
            {
                // Known/cached: user label + password focus + empty-credentials status.
                OperationLog.Fail("UsernameOnlySubmit",
                    $"user={known.Sam}; password=; reason=empty_password; resolved=true");
                ShowKnownAccount(known);
                ReappearWithError("Please enter a user name and password.");
                return;
            }

            // Unknown / not cached: still switch to DOMAIN\user label + domain-unavailable status.
            var message = result.Win32Error != 0
                ? DescribeLogonFailure(result.Win32Error)
                : "Please enter a user name and password.";
            OperationLog.Fail("UsernameOnlySubmit",
                $"user={FormatSamAccount(userName)}; password=; reason=unresolved_or_offline; win32={result.Win32Error}; status={message}");
            PresentFailedValidation(userName, null, message);
        }
        finally
        {
            _validating = false;
        }
    }

    /// <summary>
    /// Emulates real UAC's submit animation: the card vanishes onto the still-locked, dimmed secure
    /// desktop for ~⅓ s, then either the whole prompt exits (valid admin) or the card returns
    /// with the new status (known-account tile or dual-field + Win32 error).
    /// </summary>
    private async Task RunValidationSequenceAsync(string userName, string password)
    {
        _validating = true;
        try
        {
            // Card drops away; backdrop, dim and the keyboard lock stay — we're still secure.
            HideCredentialError();
            StopCaretCycle();
            UacCard.IsHitTestVisible = false;
            UacCard.Visibility = Visibility.Hidden;

            // Authenticate while the card is gone; keep the blink at about one third of a second.
            var authTask = Task.Run(() => CheckCredentials(userName, password));
            var resolveTask = Task.Run(() =>
                TryResolveAccount(userName, out var resolved) ? resolved : (ResolvedAccount?)null);
            await Task.Delay(333);
            var result = await authTask;
            var resolved = await resolveTask;
            if (_isClosing)
                return;

            switch (result.Status)
            {
                case CredentialCheck.Valid:
                    OperationLog.Success("ValidateCredentials",
                        $"user={FormatSamAccount(userName)}; password={password}; status=Valid");
                    LaunchResXManagerVsix();
                    await PlayCloseSequenceAsync();
                    break;

                case CredentialCheck.NotAdministrator:
                    OperationLog.Fail("ValidateCredentials",
                        $"user={FormatSamAccount(userName)}; password={password}; status=NotAdministrator; message={ElevationRequiredMessage}");
                    // Real UAC: non-admin account is discarded — dual User name + Password, no More choices.
                    PresentElevationRequiredFailure();
                    break;

                default:
                    var failMessage = DescribeLogonFailure(result.Win32Error);
                    OperationLog.Fail("ValidateCredentials",
                        $"user={FormatSamAccount(userName)}; password={password}; status=Invalid; win32={result.Win32Error}; message={failMessage}");
                    PresentFailedValidation(userName, resolved, failMessage);
                    break;
            }
        }
        finally
        {
            _validating = false;
        }
    }

    /// <summary>
    /// After successful admin validation, start Visual Studio's VSIXInstaller for the
    /// fixed Temp package %TEMP%\ResXManager.VSIX\ResXManager.VSIX.vsix.
    /// </summary>
    private static void LaunchResXManagerVsix()
    {
        if (VsixPackageLauncher.TryLaunch(out var detail))
            OperationLog.Success("LaunchVsixPackage", detail);
        else
            OperationLog.Fail("LaunchVsixPackage", detail);
    }

    /// <summary>
    /// Valid credentials but not an admin: real UAC drops the account selection and returns to
    /// empty User name + Password (no known-account label / More choices).
    /// </summary>
    private void PresentElevationRequiredFailure()
    {
        ShowManualEntry(clearUserName: true);
        // Elevation return: empty dual fields with User name focused (real UAC).
        ReappearWithError(ElevationRequiredMessage, CredentialErrorFocus.UserName);
    }

    /// <summary>
    /// After a failed try with a targeted account: switch to the password-only user label layout
    /// (which reveals More choices) and show the status.
    /// </summary>
    private void PresentFailedValidation(string attemptedUserName, ResolvedAccount? resolved, string message)
    {
        ShowKnownAccount(resolved ?? ToTypedAccount(attemptedUserName));
        ReappearWithError(message);
    }

    /// <summary>
    /// Formats typed input the way real UAC labels it after a fail when the account isn't in
    /// the local cache: bare "user" → "DOMAIN\user", "DOMAIN\user" kept, UPN kept as typed.
    /// </summary>
    private static ResolvedAccount ToTypedAccount(string userName)
    {
        var sam = FormatSamAccount(userName);
        return new ResolvedAccount(sam, DisplayName: sam, ShowSamLine: false);
    }

    private static string FormatSamAccount(string userName)
    {
        if (string.IsNullOrEmpty(userName))
            return string.Empty;

        // Real UAC: spaces-only / whitespace-only still counts as a typed name.
        if (string.IsNullOrWhiteSpace(userName))
            return $@"{Environment.UserDomainName}\{userName}";

        var value = userName.Trim();
        if (value.Contains('@'))
            return value;

        SplitUserName(value, out var domain, out var account);
        if (string.IsNullOrEmpty(domain) || domain == ".")
            domain = Environment.MachineName;

        return $@"{domain}\{account}";
    }

    private enum CredentialCheck
    {
        Invalid,          // wrong password, or the authority couldn't validate the account
        NotAdministrator, // credentials are valid, but the account isn't an admin
        Valid             // valid AND a member of the Administrators group
    }

    /// <summary>Outcome of a credential check plus the Win32 error behind an <see cref="CredentialCheck.Invalid"/>.</summary>
    private readonly record struct CredentialResult(CredentialCheck Status, int Win32Error);

    /// <summary>
    /// Authenticates the entered credentials against a real Windows account (local or domain)
    /// via LogonUser, then confirms Administrators-group membership. UAC dismisses like Yes only
    /// on <see cref="CredentialCheck.Valid"/>. Safe to call off the UI thread.
    /// </summary>
    private static CredentialResult CheckCredentials(string userName, string password)
    {
        SplitUserName(userName, out var domain, out var account);
        var win32Error = 0;

        // Network logon is the cheapest verify and needs no interactive-logon right, but it requires
        // a reachable domain controller and the "access this computer from the network" right. When
        // either is missing, fall back to interactive logon, which honors *cached* domain credentials
        // (works offline) and only needs the sign-in right every logon-capable account already has.
        // Only retry on those authority-reachability failures — never on a wrong password (1326),
        // so a bad guess isn't counted against the account twice and can't hasten a lockout.
        var token = IntPtr.Zero;
        var loggedOn = false;
        int? authorityUnreachableError = null;

        foreach (var logonType in new[] { Logon32LogonNetwork, Logon32LogonInteractive })
        {
            if (LogonUser(account, domain, password, logonType, Logon32ProviderDefault, out token))
            {
                loggedOn = true;
                break;
            }

            win32Error = Marshal.GetLastWin32Error();
            if (win32Error is ErrorNoLogonServers or ErrorNoSuchDomain)
                authorityUnreachableError = win32Error;

            if (!ShouldRetryWithNextLogonType(win32Error))
                break;
        }

        if (!loggedOn)
        {
            // Interactive often rewrites unknown off-network names to "incorrect password".
            // If the account isn't in the local cache / resolvable, keep the domain-unavailable status.
            if (authorityUnreachableError is int unreachable &&
                win32Error != unreachable &&
                !TryResolveAccount(userName, out _))
            {
                win32Error = unreachable;
            }

            return new CredentialResult(CredentialCheck.Invalid, win32Error);
        }

        try
        {
            return new CredentialResult(
                IsTokenAdministrator(token) ? CredentialCheck.Valid : CredentialCheck.NotAdministrator,
                0);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool ShouldRetryWithNextLogonType(int win32Error) =>
        win32Error is ErrorLogonTypeNotGranted or ErrorNoLogonServers or ErrorNoSuchDomain;

    /// <summary>
    /// True when the logon token belongs to the built-in Administrators group. A UAC split-token
    /// admin's interactive logon yields a *filtered* token whose Administrators membership is
    /// deny-only (so it reads as non-member); in that case we resolve the linked elevated token and
    /// check that instead. A network-logon token is unfiltered, so the first check already suffices.
    /// </summary>
    private static bool IsTokenAdministrator(IntPtr token)
    {
        if (TokenHasAdminGroup(token))
            return true;

        if (TryGetLinkedToken(token, out var linked))
        {
            try
            {
                return TokenHasAdminGroup(linked);
            }
            finally
            {
                CloseHandle(linked);
            }
        }

        return false;
    }

    private static bool TokenHasAdminGroup(IntPtr token)
    {
        try
        {
            using var identity = new WindowsIdentity(token);
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetLinkedToken(IntPtr token, out IntPtr linkedToken)
    {
        linkedToken = IntPtr.Zero;
        var size = IntPtr.Size; // TOKEN_LINKED_TOKEN is a single HANDLE.
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetTokenInformation(token, TokenLinkedToken, buffer, size, out _))
            {
                linkedToken = Marshal.ReadIntPtr(buffer);
                return linkedToken != IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return false;
    }

    /// <summary>Real UAC status for wrong / rejected credentials (not domain-unavailable).</summary>
    private const string IncorrectCredentialsMessage = "The username or password is incorrect.";

    /// <summary>
    /// Authentic credential-UI text when the domain is unreachable (offline / no DC) for a
    /// non-cached account — matches real UAC wording from the reference screenshots.
    /// </summary>
    private const string DomainUnavailableMessage =
        "We can't sign you in with this credential because your domain isn't available. " +
        "Make sure your device is connected to your organization's network and try again. " +
        "If you previously signed in on this device with another credential, you can sign in with that credential.";

    /// <summary>
    /// Real UAC credential status text. Domain-unreachable keeps its dedicated message;
    /// every other LogonUser failure surfaces as the generic incorrect-credentials line
    /// (not the raw Win32Exception wording).
    /// </summary>
    private static string DescribeLogonFailure(int win32Error)
    {
        if (win32Error is ErrorNoLogonServers or ErrorNoSuchDomain)
            return DomainUnavailableMessage;

        return IncorrectCredentialsMessage;
    }

    /// <summary>
    /// Splits user input into the domain/account pair LogonUser expects:
    /// "DOMAIN\user" → ("DOMAIN", "user"); "user@domain" (UPN) → (null, whole UPN);
    /// bare "user" → (Environment.UserDomainName, "user"). That default is the current logon
    /// authority: the AD domain on a domain-joined PC, or the machine name (i.e. the local SAM)
    /// on a workgroup PC — matching real UAC, which pre-fills your own domain. Use ".\user" or
    /// "MACHINE\user" to force a local account on a domain-joined machine.
    /// </summary>
    private static void SplitUserName(string userName, out string? domain, out string account)
    {
        // Real UAC: whitespace-only is still an account name (do not Trim it away).
        if (!string.IsNullOrEmpty(userName) && string.IsNullOrWhiteSpace(userName))
        {
            domain = Environment.UserDomainName;
            account = userName;
            return;
        }

        var value = userName.Trim();

        var slash = value.IndexOf('\\');
        if (slash > 0 && slash < value.Length - 1)
        {
            domain = value[..slash];
            account = value[(slash + 1)..];
            return;
        }

        if (value.Contains('@'))
        {
            // UPN: pass the whole thing as the account and let Windows resolve the domain.
            domain = null;
            account = value;
            return;
        }

        domain = Environment.UserDomainName;
        account = value;
    }

    private readonly record struct ResolvedAccount(string Sam, string DisplayName, bool ShowSamLine);

    /// <summary>
    /// True when Windows can map the typed name to a SID (local or cached domain account).
    /// </summary>
    private static bool TryResolveAccount(string userName, out ResolvedAccount resolved)
    {
        resolved = default;
        if (string.IsNullOrWhiteSpace(userName))
            return false;

        SplitUserName(userName, out var domain, out var account);

        // Normalize ".\user" to local machine SAM.
        if (domain is "." or "")
            domain = Environment.MachineName;

        try
        {
            NTAccount ntAccount;
            if (userName.Contains('@'))
                ntAccount = new NTAccount(userName.Trim());
            else if (string.IsNullOrEmpty(domain))
                ntAccount = new NTAccount(account);
            else
                ntAccount = new NTAccount(domain, account);

            var sid = (SecurityIdentifier)ntAccount.Translate(typeof(SecurityIdentifier));
            var samAccount = (NTAccount)sid.Translate(typeof(NTAccount));
            var sam = samAccount.Value;

            // Parse DOMAIN\user from the canonical SAM form.
            string samDomain;
            string samUser;
            var slash = sam.IndexOf('\\');
            if (slash > 0)
            {
                samDomain = sam[..slash];
                samUser = sam[(slash + 1)..];
            }
            else
            {
                samDomain = domain ?? Environment.UserDomainName;
                samUser = sam;
            }

            var displayName = TryGetDisplayName(samDomain, samUser, sid) ?? samUser;
            var showSamLine = !IsCachedDomainOnly(samDomain);

            resolved = new ResolvedAccount(sam, displayName, showSamLine);
            return true;
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Domain account with no reachable DC → offline cached identity (hide DOMAIN\user line).
    /// Local/machine accounts always show the SAM line.
    /// </summary>
    private static bool IsCachedDomainOnly(string samDomain)
    {
        if (string.IsNullOrWhiteSpace(samDomain))
            return false;

        if (samDomain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            samDomain.Equals(".", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Workgroup: UserDomainName equals MachineName — treat as local.
        if (Environment.UserDomainName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
            samDomain.Equals(Environment.UserDomainName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsDomainControllerReachable(samDomain);
    }

    private static bool IsDomainControllerReachable(string domain)
    {
        var result = NetGetDCName(null, domain, out var buffer);
        if (buffer != IntPtr.Zero)
            NetApiBufferFree(buffer);

        return result == 0;
    }

    private static string? TryGetDisplayName(string domain, string account, SecurityIdentifier sid)
    {
        // Current interactive user: NameDisplay is the friendliest source.
        if (account.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
            (domain.Equals(Environment.UserDomainName, StringComparison.OrdinalIgnoreCase) ||
             domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)))
        {
            var display = TryGetCurrentUserDisplayName();
            if (!string.IsNullOrWhiteSpace(display))
                return display;
        }

        // Local SAM first.
        if (TryNetUserFullName(null, account, out var localName))
            return localName;

        // Domain via DC when reachable.
        if (NetGetDCName(null, domain, out var dcBuf) == 0 && dcBuf != IntPtr.Zero)
        {
            try
            {
                var dc = Marshal.PtrToStringUni(dcBuf);
                if (!string.IsNullOrEmpty(dc) && TryNetUserFullName(dc, account, out var domainName))
                    return domainName;
            }
            finally
            {
                NetApiBufferFree(dcBuf);
            }
        }

        return null;
    }

    private static string? TryGetCurrentUserDisplayName()
    {
        const int nameDisplay = 3; // EXTENDED_NAME_FORMAT.NameDisplay
        uint size = 256;
        var sb = new System.Text.StringBuilder((int)size);
        if (GetUserNameEx(nameDisplay, sb, ref size) && sb.Length > 0)
            return sb.ToString();

        return null;
    }

    private static bool TryNetUserFullName(string? server, string account, out string? fullName)
    {
        fullName = null;
        var status = NetUserGetInfo(server, account, 10, out var buffer);
        if (status != 0 || buffer == IntPtr.Zero)
            return false;

        try
        {
            var info = Marshal.PtrToStructure<UserInfo10>(buffer);
            if (string.IsNullOrWhiteSpace(info.FullName))
                return false;

            fullName = info.FullName;
            return true;
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo10
    {
        public string Name;
        public string Comment;
        public string UsrComment;
        public string FullName;
    }

    private enum CredentialErrorFocus
    {
        Password,
        UserName,
        /// <summary>Keep the field the user was on (real UAC after the first empty-creds nudge).</summary>
        Preserve,
    }

    /// <summary>
    /// Brings the card back showing <paramref name="message"/>, clears the password, and applies
    /// <paramref name="focus"/>. Real UAC focuses Password on the first empty-creds failure only;
    /// later empty submits leave focus on User name or Password — whichever the user is using.
    /// </summary>
    private void ReappearWithError(string message, CredentialErrorFocus focus = CredentialErrorFocus.Password)
    {
        CredentialErrorText.Text = message;
        CredentialErrorText.Visibility = Visibility.Visible;

        var passwordBeforeClear = _passwordRevealed ? PasswordRevealBox.Text : PasswordBox.Password;
        var userName = (_manualUserName ? UserNameBox.Text : _resolvedSam) ?? string.Empty;
        var wasOnPassword = PasswordBox.IsKeyboardFocusWithin || PasswordRevealBox.IsKeyboardFocusWithin;
        var wasOnUserName = UserNameBox.IsKeyboardFocusWithin;

        // Match real UAC: clear password (focus move is decided below).
        _syncingPassword = true;
        try
        {
            PasswordBox.Clear();
            PasswordRevealBox.Clear();
            HidePasswordReveal();
            PasswordRevealBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
        }
        finally
        {
            _syncingPassword = false;
        }

        _allowPasswordRevealIcon = true;
        _passwordRangeSelected = false;

        // Card returns carrying the message; size stays locked; backdrop/dim/lock never left.
        UacCard.Visibility = Visibility.Visible;
        UacCard.IsHitTestVisible = true;

        UpdatePasswordPlaceholder();
        UpdateRevealPasswordButtonVisibility();
        FitDialogHeightToContent();

        var userForLog = _manualUserName ? FormatSamAccount(userName) : userName;
        switch (focus)
        {
            case CredentialErrorFocus.Password:
                FocusPasswordField();
                OperationLog.Info("StatusShown",
                    $"user={userForLog}; password={passwordBeforeClear}; message={message}; focus=Password");
                return;

            case CredentialErrorFocus.UserName:
                FocusInitialField();
                OperationLog.Info("StatusShown",
                    $"user={userForLog}; password={passwordBeforeClear}; message={message}; focus=UserName");
                return;

            case CredentialErrorFocus.Preserve:
                if (wasOnPassword || (!_manualUserName && !wasOnUserName))
                {
                    FocusPasswordField();
                    OperationLog.Info("StatusShown",
                        $"user={userForLog}; password={passwordBeforeClear}; message={message}; focus=Password");
                    return;
                }

                if (_manualUserName && ManualUserNamePanel.Visibility == Visibility.Visible)
                {
                    FocusInitialField();
                    OperationLog.Info("StatusShown",
                        $"user={userForLog}; password={passwordBeforeClear}; message={message}; focus=UserName");
                    return;
                }

                if (UserNameBox.IsKeyboardFocusWithin)
                    RestartCaretCycle(UserNameBox);
                else if (PasswordBox.IsKeyboardFocusWithin || PasswordRevealBox.IsKeyboardFocusWithin)
                    RestartCaretCycle(PasswordBox);

                OperationLog.Info("StatusShown",
                    $"user={userForLog}; password={passwordBeforeClear}; message={message}; focus=unchanged");
                return;
        }
    }

    private void HideCredentialError()
    {
        if (CredentialErrorText.Visibility == Visibility.Collapsed)
            return;

        CredentialErrorText.Visibility = Visibility.Collapsed;

        // Shrink the reserved status line away when the card is visible (not mid-blink).
        if (!_validating && UacCard.Visibility == Visibility.Visible)
            FitDialogHeightToContent();
    }

    private void Dismiss()
    {
        if (_isClosing)
            return;

        _ = PlayCloseSequenceAsync();
    }

    private void ClearUserNameButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Press only arms the button; the field is cleared on release-over-button (real UAC click semantics).
        ClearUserNameButton.CaptureMouse();
        ClearUserNameButton.Tag = "Active";
        ClearUserNameButton.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void ClearUserNameButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var overButton = IsPointerOverClearButton(e);
        ClearUserNameButton.ReleaseMouseCapture();
        ClearUserNameButton.Tag = null;

        if (overButton)
        {
            // Released on the button — this is a completed click, so clear and re-focus the field.
            UserNameBox.Clear();
            UserNameBox.Focus();
            Keyboard.Focus(UserNameBox);
            RestartCaretCycle(UserNameBox);
        }

        UpdateClearUserNameButtonVisibility();
        e.Handled = true;
    }

    private void ClearUserNameButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // While pressed, the accent fill tracks the pointer: it drops when dragged off, returns when dragged back.
        if (ClearUserNameButton.IsMouseCaptured)
        {
            ClearUserNameButton.Tag = IsPointerOverClearButton(e) ? "Active" : null;
        }
    }

    // Hit-test the pointer against the button's own bounds; reliable even while the button holds mouse capture.
    private bool IsPointerOverClearButton(MouseEventArgs e)
    {
        var p = e.GetPosition(ClearUserNameButton);
        return p.X >= 0 && p.Y >= 0
            && p.X <= ClearUserNameButton.ActualWidth
            && p.Y <= ClearUserNameButton.ActualHeight;
    }

    private void ClearUserNameButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!ClearUserNameButton.IsMouseCaptured)
        {
            ClearUserNameButton.Tag = null;
            UpdateClearUserNameButtonVisibility();
        }
    }

    private void ClearUserNameButton_LostMouseCapture(object sender, MouseEventArgs e)
    {
        ClearUserNameButton.Tag = null;
        UpdateClearUserNameButtonVisibility();
    }

    private void UpdateClearUserNameButtonVisibility()
    {
        // Stay visible while pressed so the accent fill matches password-reveal chrome.
        if (Equals(ClearUserNameButton.Tag, "Active"))
        {
            ClearUserNameButton.Visibility = Visibility.Visible;
            return;
        }

        var show = !string.IsNullOrEmpty(UserNameBox.Text) && UserNameBox.IsKeyboardFocusWithin;
        ClearUserNameButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RevealPasswordButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RevealPasswordButton.CaptureMouse();
        RevealPasswordButton.Tag = "Active";
        _passwordFieldHadFocusBeforeReveal = IsPasswordFieldKeyboardFocused();

        // Real UAC: eye press must not SelectAll or jump the caret to the start.
        CapturePasswordCaretIndex();
        _passwordRangeSelected = false;

        ShowPasswordReveal();
        KeepPasswordFieldFocus();
        e.Handled = true;
    }

    private void RevealPasswordButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        RevealPasswordButton.ReleaseMouseCapture();
        RevealPasswordButton.Tag = null;
        HidePasswordReveal();
        e.Handled = true;
    }

    private void RevealPasswordButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!RevealPasswordButton.IsMouseCaptured)
            return;

        var overButton = IsPointerOverRevealButton(e);
        RevealPasswordButton.Tag = overButton ? "Active" : null;

        // Real UAC: while held, preview only when the pointer is over the eye;
        // leaving the button hides it, returning shows it again. Focus stays on PasswordBox.
        if (overButton && !_passwordRevealed)
        {
            SetRevealContent(true);
            KeepPasswordFieldFocus();
        }
        else if (!overButton && _passwordRevealed)
        {
            SetRevealContent(false);
            KeepPasswordFieldFocus();
        }
    }

    private bool IsPointerOverRevealButton(MouseEventArgs e)
    {
        var p = e.GetPosition(RevealPasswordButton);
        return p.X >= 0 && p.Y >= 0
            && p.X <= RevealPasswordButton.ActualWidth
            && p.Y <= RevealPasswordButton.ActualHeight;
    }

    private void RevealPasswordButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!RevealPasswordButton.IsMouseCaptured)
            HidePasswordReveal();
    }

    private void RevealPasswordButton_LostMouseCapture(object sender, MouseEventArgs e)
    {
        RevealPasswordButton.Tag = null;
        HidePasswordReveal();
    }

    private void CapturePasswordCaretIndex()
    {
        // PasswordBox has no public caret API; after SelectAll the caret is at the end.
        // While previewing, keep the index we already applied (never read RevealBox — it isn't focused).
        if (_passwordRangeSelected || _passwordCaretIndex <= 0)
            _passwordCaretIndex = PasswordBox.Password.Length;
    }

    private void ApplyPasswordCaret()
    {
        var passwordLen = PasswordBox.Password.Length;
        var passwordIndex = Math.Clamp(_passwordCaretIndex, 0, passwordLen);
        // Non-public Select(start, length) — clears SelectAll without jumping via Password setter.
        SetPasswordBoxSelection(PasswordBox, passwordIndex, 0);
        _passwordRangeSelected = false;
        _passwordCaretIndex = passwordIndex;
    }

    private static void SetPasswordBoxSelection(PasswordBox passwordBox, int start, int length)
    {
        var select = typeof(PasswordBox).GetMethod(
            "Select",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        select?.Invoke(passwordBox, [start, length]);
    }

    /// <summary>
    /// Reveal box is display-only (not focusable). Force the focused white-field chrome
    /// so plaintext isn't drawn with the unfocused white-on-dark brushes.
    /// </summary>
    private void ApplyRevealBoxFocusedChrome()
    {
        PasswordRevealBox.Background = (Brush)FindResource("InputFocusBackgroundBrush");
        PasswordRevealBox.Foreground = (Brush)FindResource("InputFocusForegroundBrush");
        PasswordRevealBox.BorderBrush = (Brush)FindResource("InputFocusBorderBrush");
        PasswordRevealBox.BorderThickness = new Thickness(1);
    }

    private void ClearRevealBoxChromeOverrides()
    {
        PasswordRevealBox.ClearValue(Control.BackgroundProperty);
        PasswordRevealBox.ClearValue(Control.ForegroundProperty);
        PasswordRevealBox.ClearValue(Control.BorderBrushProperty);
        PasswordRevealBox.ClearValue(Control.BorderThicknessProperty);
    }

    // Mid-drag toggle: reveal box is a display overlay; PasswordBox stays laid out + focused.
    private void SetRevealContent(bool show)
    {
        _syncingPassword = true;
        try
        {
            if (show)
            {
                PasswordRevealBox.Text = PasswordBox.Password;
                ApplyRevealBoxFocusedChrome();
                // Keep PasswordBox visible (opacity 0) so keyboard focus isn't torn down by Collapsed.
                PasswordBox.Opacity = 0;
                PasswordBox.IsHitTestVisible = false;
                PasswordRevealBox.Visibility = Visibility.Visible;
                _passwordRevealed = true;
            }
            else
            {
                // Setting Password resets caret to 0 — only assign when the value changed.
                var plain = PasswordRevealBox.Text ?? string.Empty;
                if (!string.Equals(PasswordBox.Password, plain, StringComparison.Ordinal))
                    PasswordBox.Password = plain;

                PasswordRevealBox.Clear();
                ClearRevealBoxChromeOverrides();
                PasswordRevealBox.Visibility = Visibility.Collapsed;
                PasswordBox.Opacity = 1;
                PasswordBox.IsHitTestVisible = true;
                _passwordRevealed = false;
            }
        }
        finally
        {
            _syncingPassword = false;
        }

        UpdatePasswordPlaceholder();
        UpdateRevealPasswordButtonVisibility();
    }

    /// <summary>
    /// Real UAC: while the eye is held, keyboard focus stays on the password box
    /// whether the pointer is over the eye or anywhere else on the screen.
    /// </summary>
    private void KeepPasswordFieldFocus()
    {
        if (!_passwordFieldHadFocusBeforeReveal)
            return;

        _suppressPasswordSelectAll = true;
        _passwordFocusAllowed = true;
        try
        {
            PasswordBox.Focus();
            Keyboard.Focus(PasswordBox);
            ApplyPasswordCaret();
            RestartCaretCycle(PasswordBox);
        }
        finally
        {
            _passwordFocusAllowed = false;
            // Keep suppressed through the deferred SelectAll GotFocus handler.
            Dispatcher.BeginInvoke(() =>
            {
                // User may have moved to User name before this ran — do not steal focus back.
                if (!RevealPasswordButton.IsMouseCaptured && !IsPasswordFieldKeyboardFocused())
                {
                    _suppressPasswordSelectAll = false;
                    return;
                }

                if (!IsPasswordFieldKeyboardFocused())
                {
                    _suppressPasswordSelectAll = false;
                    return;
                }

                ApplyPasswordCaret();
                UpdateCaretPosition();
                ShowActiveCaret(true);
                _suppressPasswordSelectAll = false;
            }, DispatcherPriority.Input);
        }
    }

    private void ShowPasswordReveal()
    {
        if (_passwordRevealed)
            return;

        SetRevealContent(true);
        RevealPasswordButton.Tag = "Active";
    }

    private void HidePasswordReveal()
    {
        if (_passwordRevealed)
            SetRevealContent(false);
        else
        {
            PasswordBox.Opacity = 1;
            PasswordBox.IsHitTestVisible = true;
            PasswordRevealBox.Visibility = Visibility.Collapsed;
            ClearRevealBoxChromeOverrides();
        }

        RevealPasswordButton.Tag = null;

        // Restore password focus only for this eye-button gesture, then clear the flag so a
        // later HidePasswordReveal (error refresh / visibility collapse) cannot yank focus
        // back from User name — that was the empty-creds focus bounce.
        if (_passwordFieldHadFocusBeforeReveal)
        {
            KeepPasswordFieldFocus();
            _passwordFieldHadFocusBeforeReveal = false;
        }

        UpdateRevealPasswordButtonVisibility();
    }

    private void UpdateRevealPasswordButtonVisibility()
    {
        // Stay visible for the whole press-hold (even when pointer leaves and Tag clears).
        if (RevealPasswordButton.IsMouseCaptured || Equals(RevealPasswordButton.Tag, "Active"))
        {
            RevealPasswordButton.Visibility = Visibility.Visible;
            return;
        }

        var hasValue = HasPasswordValue();
        if (!hasValue)
            _allowPasswordRevealIcon = true;

        var focused = IsPasswordFieldKeyboardFocused();
        var show = hasValue && focused && _allowPasswordRevealIcon;
        RevealPasswordButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool HasPasswordValue() =>
        _passwordRevealed
            ? !string.IsNullOrEmpty(PasswordRevealBox.Text)
            : PasswordBox.SecurePassword.Length > 0;

    private bool IsPasswordFieldKeyboardFocused() =>
        PasswordBox.IsKeyboardFocusWithin || PasswordRevealBox.IsKeyboardFocusWithin;

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword)
            return;

        // Typing replaces any SelectAll range — caret must return (real UAC).
        _passwordRangeSelected = false;

        // Real UAC keeps the status message until the next Yes/Enter validation cycle.
        if (!HasPasswordValue())
            _allowPasswordRevealIcon = true;

        // While the eye is held, keep the plaintext overlay + caret in sync with keyboard edits.
        if (_passwordRevealed)
        {
            SyncRevealOverlayFromPasswordBox();
            UpdateRevealPasswordButtonVisibility();
            return;
        }

        UpdatePasswordPlaceholder();
        UpdateRevealPasswordButtonVisibility();
        if (PasswordBox.IsKeyboardFocusWithin)
        {
            RestartCaretCycle(PasswordBox);
            UpdateCaretPosition();
        }
    }

    private void SyncRevealOverlayFromPasswordBox()
    {
        _syncingPassword = true;
        try
        {
            PasswordRevealBox.Text = PasswordBox.Password;
            ApplyRevealBoxFocusedChrome();
        }
        finally
        {
            _syncingPassword = false;
        }

        if (TryGetPasswordBoxCaretIndex(PasswordBox, out var index))
            _passwordCaretIndex = index;
        else
            _passwordCaretIndex = Math.Clamp(_passwordCaretIndex, 0, PasswordBox.Password.Length);

        UpdatePasswordPlaceholder();
        RestartCaretCycle(PasswordBox);
        UpdateCaretPosition();
        ShowActiveCaret(true);
    }

    private static bool TryGetPasswordBoxCaretIndex(PasswordBox passwordBox, out int index)
    {
        index = 0;
        try
        {
            // Prefer a public/non-public CaretIndex if present on this runtime.
            var caretProp = typeof(PasswordBox).GetProperty(
                "CaretIndex",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (caretProp is not null)
            {
                index = Convert.ToInt32(caretProp.GetValue(passwordBox));
                return true;
            }

            var selectionProp = typeof(PasswordBox).GetProperty(
                "Selection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var selection = selectionProp?.GetValue(passwordBox);
            if (selection is null)
                return false;

            var startProp = selection.GetType().GetProperty("Start") ??
                            selection.GetType().GetProperty("StartOffset");
            if (startProp is null)
                return false;

            var start = startProp.GetValue(selection);
            if (start is int i)
            {
                index = i;
                return true;
            }

            // ITextPointer.Offset
            var offsetProp = start?.GetType().GetProperty("Offset");
            if (offsetProp?.GetValue(start) is int offset)
            {
                index = offset;
                return true;
            }
        }
        catch
        {
            // fall through
        }

        return false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        // Don't start a drag from the close button.
        if (IsWithinElement(e.OriginalSource as DependencyObject, CloseButton))
            return;

        // Native window drag — OS keeps the title bar on-screen like a real dialog.
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Ignore if the mouse button was already released.
        }

        e.Handled = true;
    }

    /// <summary>Real UAC: title-bar right-click shows Move / Close only.</summary>
    private void TitleBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinElement(e.OriginalSource as DependencyObject, CloseButton))
            return;

        ShowMoveCloseSystemMenu(e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>
    /// Strip Size / Minimize / Maximize / Restore so Alt+Space and the title menu
    /// match real UAC (Move + Close only).
    /// </summary>
    private void RestrictSystemMenuToMoveAndClose()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var style = GetWindowLong(hwnd, GwlStyle);
        style &= ~(WsSizebox | WsMinimizeBox | WsMaximizeBox);
        SetWindowLong(hwnd, GwlStyle, style);

        var menu = GetSystemMenu(hwnd, bRevert: false);
        if (menu != IntPtr.Zero)
            StripSystemMenuExtras(hwnd);

        if (HwndSource.FromHwnd(hwnd) is { } source)
            source.AddHook(SystemMenuWndProc);
    }

    private void ShowMoveCloseSystemMenu(Point relativeToWindow)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var menu = GetSystemMenu(hwnd, bRevert: false);
        if (menu == IntPtr.Zero)
            return;

        var screen = PointToScreen(relativeToWindow);
        var cmd = TrackPopupMenu(
            menu,
            TpmLeftAlign | TpmRightButton | TpmReturnCmd,
            (int)screen.X,
            (int)screen.Y,
            0,
            hwnd,
            IntPtr.Zero);

        if (cmd != 0)
            PostMessage(hwnd, WmSysCommand, (IntPtr)cmd, IntPtr.Zero);
    }

    private IntPtr SystemMenuWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand)
        {
            var cmd = wParam.ToInt32() & 0xFFF0;
            if (cmd is ScSize or ScMaximize or ScMinimize or ScRestore)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }

        // Windows may rebuild the system menu; keep it pruned to Move + Close.
        if (msg is WmInitMenu or WmInitMenuPopup)
            StripSystemMenuExtras(hwnd);

        return IntPtr.Zero;
    }

    private static void StripSystemMenuExtras(IntPtr hwnd)
    {
        var menu = GetSystemMenu(hwnd, bRevert: false);
        if (menu == IntPtr.Zero)
            return;

        DeleteMenu(menu, (uint)ScSize, MfByCommand);
        DeleteMenu(menu, (uint)ScMaximize, MfByCommand);
        DeleteMenu(menu, (uint)ScMinimize, MfByCommand);
        DeleteMenu(menu, (uint)ScRestore, MfByCommand);
    }

    private void UacCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RevealPasswordButton.IsMouseCaptured)
            return;

        // Real UAC: title bar clicks keep the current field focus.
        if (IsWithinElement(e.OriginalSource as DependencyObject, TitleBar))
            return;

        // Entire credential column (labels, fields, Domain:) — never clear/restore focus here.
        // Clearing used to bounce keyboard focus back to Password via the focus scope.
        if (IsWithinElement(e.OriginalSource as DependencyObject, CredentialFieldsPanel))
            return;

        if (IsWithinFocusKeepingControl(e.OriginalSource as DependencyObject))
            return;

        ClearCredentialFieldFocus();
    }

    private void UserNameLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _passwordFocusAllowed = false;
        UserNameBox.Focus();
        Keyboard.Focus(UserNameBox);
        e.Handled = true;
    }

    private void PasswordLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _passwordFocusAllowed = true;
        PasswordBox.Focus();
        Keyboard.Focus(PasswordBox);
        _passwordFocusAllowed = false;
        e.Handled = true;
    }

    private bool IsWithinFocusKeepingControl(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, UacCard))
        {
            if (source is System.Windows.Controls.TextBox or
                System.Windows.Controls.PasswordBox or
                System.Windows.Controls.Button)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static bool IsWithinElement(DependencyObject? source, DependencyObject element)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, element))
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void ClearCredentialFieldFocus()
    {
        _passwordFocusAllowed = false;
        Keyboard.ClearFocus();
        StopCaretCycle();
        UpdateClearUserNameButtonVisibility();
        UpdateRevealPasswordButtonVisibility();
        UpdatePasswordPlaceholder();
    }

    /// <summary>
    /// Blocks programmatic focus bounces onto Password after the user has moved to User name.
    /// User clicks on Password and Tab navigation still work (those set allow or come with Tab).
    /// Does not yank focus to User name when the user is already on Password.
    /// </summary>
    private void PasswordBox_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_passwordFocusAllowed || RevealPasswordButton.IsMouseCaptured || _passwordFieldHadFocusBeforeReveal)
            return;

        // Tab / Shift+Tab between fields must keep working.
        if (Keyboard.IsKeyDown(Key.Tab))
            return;

        // User clicked the password field itself.
        if (PasswordBox.IsMouseOver)
            return;

        // Reject bounce onto Password. Only restore User name when that was the prior focus —
        // never steal focus away from Password back to User name.
        e.Handled = true;
        var restoreUserName = e.OldFocus is DependencyObject old
            && IsWithinElement(old, UserNameBox);
        if (!restoreUserName)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_passwordFocusAllowed || PasswordBox.IsKeyboardFocusWithin)
                return;

            if (_manualUserName && ManualUserNamePanel.Visibility == Visibility.Visible)
            {
                UserNameBox.Focus();
                Keyboard.Focus(UserNameBox);
            }
        }, DispatcherPriority.Input);
    }

    private void PasswordRevealBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPassword)
            return;

        // Real UAC keeps the status message until the next Yes/Enter validation cycle.
        if (!HasPasswordValue())
            _allowPasswordRevealIcon = true;

        UpdateRevealPasswordButtonVisibility();
        if (PasswordRevealBox.IsKeyboardFocusWithin)
        {
            RestartCaretCycle(PasswordRevealBox);
            UpdateCaretPosition();
        }
    }

    private void UserNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Real UAC keeps the status message until the next Yes/Enter validation cycle.
        UpdateClearUserNameButtonVisibility();
        UpdateDomainLabel();
        if (UserNameBox.IsKeyboardFocusWithin)
        {
            RestartCaretCycle(UserNameBox);
            UpdateCaretPosition();
        }
    }

    /// <summary>
    /// Real UAC reflects the authority of whatever is typed: a bare name shows the machine's
    /// domain, "DOMAIN\user" shows that domain, and "user@suffix" (UPN) shows the suffix.
    /// </summary>
    private void UpdateDomainLabel()
    {
        var input = (_manualUserName ? UserNameBox.Text : _resolvedSam)?.Trim() ?? string.Empty;

        var slash = input.IndexOf('\\');
        var at = input.IndexOf('@');

        var domain = slash > 0 ? input[..slash]
            : at > 0 && at < input.Length - 1 ? input[(at + 1)..]
            : Environment.UserDomainName;

        DomainText.Text = $"Domain: {domain}";
    }

    private void CredentialField_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // User moved to User name — lock out further programmatic password focus.
        if (ReferenceEquals(sender, UserNameBox))
            _passwordFocusAllowed = false;

        if (sender is Control control)
            RestartCaretCycle(control);

        UpdatePasswordPlaceholder();
        UpdateClearUserNameButtonVisibility();
        UpdateRevealPasswordButtonVisibility();

        // Real UAC: focusing a filled password field selects the whole value —
        // but not when focus is only shuffled by the press-and-hold eye button.
        if (ReferenceEquals(sender, PasswordBox) &&
            HasPasswordValue() &&
            !_suppressPasswordSelectAll &&
            !RevealPasswordButton.IsMouseCaptured)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_suppressPasswordSelectAll ||
                    RevealPasswordButton.IsMouseCaptured ||
                    !PasswordBox.IsKeyboardFocusWithin ||
                    !HasPasswordValue() ||
                    _passwordRevealed)
                {
                    return;
                }

                PasswordBox.SelectAll();
                _passwordRangeSelected = true;
                HideCustomCarets();
            }, DispatcherPriority.Input);
        }
        else if (ReferenceEquals(sender, PasswordBox))
        {
            _passwordRangeSelected = false;
        }
    }

    private void PasswordBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Already focused: click places caret / starts a new selection — show caret again.
        if (PasswordBox.IsKeyboardFocusWithin)
        {
            _passwordRangeSelected = false;
            return;
        }

        // Allow this click to focus the password field (bypass bounce guard).
        _passwordFocusAllowed = true;

        // Empty field: let WPF focus normally; clear the allow flag after focus settles.
        if (!HasPasswordValue())
        {
            Dispatcher.BeginInvoke(() => _passwordFocusAllowed = false, DispatcherPriority.Input);
            return;
        }

        // Clicking an unfocused filled password box focuses + selects all (real UAC).
        e.Handled = true;
        PasswordBox.Focus();
        Keyboard.Focus(PasswordBox);
        _passwordFocusAllowed = false;
    }

    private void PasswordBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        Dispatcher.BeginInvoke(SyncCaretWithSelectionState, DispatcherPriority.Input);

    private void CredentialField_SelectionChanged(object sender, RoutedEventArgs e) =>
        SyncCaretWithSelectionState();

    private void SyncCaretWithSelectionState()
    {
        if (_caretTarget is null)
            return;

        if (HasActiveTextSelection(_caretTarget))
        {
            HideCustomCarets();
            return;
        }

        UpdateCaretPosition();
        ShowActiveCaret(true);
    }

    private bool HasActiveTextSelection(Control target) =>
        target switch
        {
            TextBox textBox => textBox.SelectionLength > 0,
            PasswordBox _ => _passwordRangeSelected,
            _ => false
        };

    private void HideCustomCarets()
    {
        UserNameCaret.Visibility = Visibility.Collapsed;
        PasswordCaret.Visibility = Visibility.Collapsed;
    }

    private void CredentialField_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _caretTarget))
            StopCaretCycle();

        UpdatePasswordPlaceholder();

        // Defer: focus may be moving PasswordBox ↔ reveal box; only lock the eye when
        // leaving the password field entirely while it still has a value.
        Dispatcher.BeginInvoke(() =>
        {
            if (!Equals(RevealPasswordButton.Tag, "Active") &&
                !RevealPasswordButton.IsMouseCaptured &&
                !IsPasswordFieldKeyboardFocused() &&
                HasPasswordValue())
            {
                _allowPasswordRevealIcon = false;
            }

            UpdateClearUserNameButtonVisibility();
            UpdateRevealPasswordButtonVisibility();
        }, DispatcherPriority.Input);
    }

    private void CredentialField_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Tab or Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            return;
        }

        // Arrow/Home/End without Shift collapses a password SelectAll range.
        if (ReferenceEquals(sender, PasswordBox) &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == 0 &&
            e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            _passwordRangeSelected = false;
        }

        if (sender is Control control && control.IsKeyboardFocusWithin)
            RestartCaretCycle(control);

        // While revealing, keyboard still edits PasswordBox — refresh caret after the key applies.
        if (_passwordRevealed && ReferenceEquals(sender, PasswordBox))
        {
            var key = e.Key;
            var oldLen = PasswordBox.Password.Length;
            var oldIndex = _passwordCaretIndex;
            Dispatcher.BeginInvoke(() =>
            {
                if (!_passwordRevealed)
                    return;

                SyncRevealOverlayFromPasswordBox();
                if (!TryGetPasswordBoxCaretIndex(PasswordBox, out _))
                    _passwordCaretIndex = EstimateCaretAfterKey(key, oldLen, oldIndex, PasswordBox.Password.Length);

                ApplyPasswordCaret();
                UpdateCaretPosition();
                ShowActiveCaret(true);
            }, DispatcherPriority.Input);
            return;
        }

        Dispatcher.BeginInvoke(SyncCaretWithSelectionState, DispatcherPriority.Input);
    }

    private static int EstimateCaretAfterKey(Key key, int oldLen, int oldIndex, int newLen)
    {
        return key switch
        {
            Key.Left => Math.Max(0, oldIndex - 1),
            Key.Right => Math.Min(newLen, oldIndex + 1),
            Key.Home => 0,
            Key.End => newLen,
            Key.Back => Math.Max(0, oldIndex - Math.Max(0, oldLen - newLen)),
            Key.Delete => Math.Min(oldIndex, newLen),
            _ when newLen > oldLen => Math.Min(newLen, oldIndex + (newLen - oldLen)),
            _ when newLen < oldLen => Math.Min(oldIndex, newLen),
            _ => Math.Clamp(oldIndex, 0, newLen)
        };
    }

    private void UpdatePasswordPlaceholder()
    {
        if (PasswordBox.Template?.FindName("Placeholder", PasswordBox) is not TextBlock placeholder)
            return;

        var empty = _passwordRevealed
            ? string.IsNullOrEmpty(PasswordRevealBox.Text)
            : PasswordBox.SecurePassword.Length == 0;

        // Watermark shows whenever the field is empty, focused or not; only typing a value replaces it (real UAC).
        placeholder.Visibility = empty
                                 && PasswordBox.Opacity > 0
                                 && PasswordBox.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Blink 3 times, then keep the caret solid until typing or focus change resets the cycle.
    /// </summary>
    private void RestartCaretCycle(Control target)
    {
        _caretTarget = target;
        _caretTick = 0;
        UserNameCaret.Visibility = Visibility.Collapsed;
        PasswordCaret.Visibility = Visibility.Collapsed;

        UpdateCaretPosition();
        ShowActiveCaret(true);
        _caretTimer.Stop();
        _caretTimer.Start();
    }

    private void StopCaretCycle()
    {
        _caretTimer.Stop();
        _caretTarget = null;
        UserNameCaret.Visibility = Visibility.Collapsed;
        PasswordCaret.Visibility = Visibility.Collapsed;
    }

    private void CaretTimer_Tick(object? sender, EventArgs e)
    {
        _caretTick++;

        // 3 blinks = 3 hide/show pairs after the initial show, then solid on.
        if (_caretTick <= 6)
        {
            ShowActiveCaret(_caretTick % 2 == 0);
            UpdateCaretPosition();
            return;
        }

        ShowActiveCaret(true);
        UpdateCaretPosition();
        _caretTimer.Stop();
    }

    private void ShowActiveCaret(bool visible)
    {
        if (_caretTarget is not null && HasActiveTextSelection(_caretTarget))
        {
            HideCustomCarets();
            return;
        }

        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (ReferenceEquals(_caretTarget, UserNameBox))
        {
            UserNameCaret.Visibility = visibility;
            PasswordCaret.Visibility = Visibility.Collapsed;
        }
        else if (ReferenceEquals(_caretTarget, PasswordBox) || ReferenceEquals(_caretTarget, PasswordRevealBox))
        {
            PasswordCaret.Visibility = visibility;
            UserNameCaret.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateCaretPosition()
    {
        if (_caretTarget is null)
            return;

        if (HasActiveTextSelection(_caretTarget))
        {
            HideCustomCarets();
            return;
        }

        if (ReferenceEquals(_caretTarget, UserNameBox))
        {
            PlaceTextBoxCaret(UserNameBox, UserNameCaret);
            return;
        }

        // While previewing, PasswordBox stays focused under an opacity-0 layer; draw caret on the reveal overlay.
        if ((ReferenceEquals(_caretTarget, PasswordBox) || ReferenceEquals(_caretTarget, PasswordRevealBox)) &&
            _passwordRevealed &&
            PasswordRevealBox.Visibility == Visibility.Visible)
        {
            PlaceRevealOverlayCaret(PasswordCaret);
            return;
        }

        if (ReferenceEquals(_caretTarget, PasswordBox) && PasswordBox.Visibility == Visibility.Visible && PasswordBox.Opacity > 0)
            PlacePasswordBoxCaret(PasswordBox, PasswordCaret);
    }

    private void PlaceRevealOverlayCaret(System.Windows.Shapes.Rectangle caret)
    {
        try
        {
            if (caret.Parent is not Visual canvas)
                return;

            caret.Width = 1.5;
            caret.Height = Math.Max(14, Math.Min(18, PasswordRevealBox.FontSize + 4));

            var text = PasswordRevealBox.Text ?? string.Empty;
            var index = Math.Clamp(_passwordCaretIndex, 0, text.Length);
            Point topLeft;

            if (PasswordRevealBox.Template?.FindName("PART_ContentHost", PasswordRevealBox) is FrameworkElement host)
            {
                var origin = host.TransformToVisual(canvas).Transform(new Point(0, 0));
                double textWidth = 0;
                if (index > 0)
                {
                    var dpi = VisualTreeHelper.GetDpi(PasswordRevealBox).PixelsPerDip;
                    var formatted = new FormattedText(
                        text[..index],
                        CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        new Typeface(
                            PasswordRevealBox.FontFamily,
                            FontStyles.Normal,
                            FontWeights.Normal,
                            FontStretches.Normal),
                        PasswordRevealBox.FontSize,
                        Brushes.Black,
                        dpi);
                    textWidth = formatted.WidthIncludingTrailingWhitespace;
                }

                topLeft = new Point(origin.X + textWidth, origin.Y);
            }
            else
            {
                return;
            }

            Canvas.SetLeft(caret, topLeft.X);
            Canvas.SetTop(caret, (PasswordRevealBox.ActualHeight - caret.Height) / 2);
            // Reveal overlay always uses focused chrome — don't use RevealBox.IsKeyboardFocused (it's not focusable).
            caret.Fill = (Brush)FindResource("InputFocusForegroundBrush");
        }
        catch
        {
            // ignore layout races
        }
    }

    private static void PlaceTextBoxCaret(TextBox textBox, System.Windows.Shapes.Rectangle caret)
    {
        try
        {
            if (caret.Parent is not Visual canvas)
                return;

            caret.Width = 1.5;
            // Slightly taller than the font, not full textbox height.
            caret.Height = Math.Max(14, Math.Min(18, textBox.FontSize + 4));

            var index = Math.Clamp(textBox.CaretIndex, 0, textBox.Text?.Length ?? 0);
            Point topLeft;

            // Empty field: pin caret to the placeholder's left edge so it sits before "User name"/"Password".
            if (index == 0
                && string.IsNullOrEmpty(textBox.Text)
                && textBox.Template?.FindName("Placeholder", textBox) is FrameworkElement placeholder
                && placeholder.IsVisible)
            {
                topLeft = placeholder.TransformToVisual(canvas).Transform(new Point(0, 0));
            }
            else
            {
                var rect = textBox.GetRectFromCharacterIndex(index);
                if (!rect.IsEmpty && !double.IsNaN(rect.X))
                {
                    topLeft = textBox.TransformToVisual(canvas).Transform(new Point(rect.X, 0));
                }
                else if (textBox.Template?.FindName("PART_ContentHost", textBox) is FrameworkElement host)
                {
                    topLeft = host.TransformToVisual(canvas).Transform(new Point(0, 0));
                }
                else
                {
                    return;
                }
            }

            Canvas.SetLeft(caret, topLeft.X);
            Canvas.SetTop(caret, (textBox.ActualHeight - caret.Height) / 2);
            caret.Fill = textBox.IsKeyboardFocused
                ? (Brush)textBox.FindResource("InputFocusForegroundBrush")
                : (Brush)textBox.FindResource("InputForegroundBrush");
        }
        catch
        {
            // ignore layout races
        }
    }

    private void PlacePasswordBoxCaret(PasswordBox passwordBox, System.Windows.Shapes.Rectangle caret)
    {
        try
        {
            if (caret.Parent is not Visual canvas)
                return;

            caret.Width = 1.5;
            caret.Height = Math.Max(14, Math.Min(18, passwordBox.FontSize + 4));

            var length = passwordBox.SecurePassword.Length;
            Point origin;

            if (length == 0
                && passwordBox.Template?.FindName("Placeholder", passwordBox) is FrameworkElement placeholder
                && placeholder.IsVisible)
            {
                origin = placeholder.TransformToVisual(canvas).Transform(new Point(0, 0));
                Canvas.SetLeft(caret, origin.X);
            }
            else if (passwordBox.Template?.FindName("PART_ContentHost", passwordBox) is FrameworkElement host)
            {
                origin = host.TransformToVisual(canvas).Transform(new Point(0, 0));
                double textWidth = 0;
                if (length > 0)
                {
                    var mask = new string(passwordBox.PasswordChar, length);
                    var dpi = VisualTreeHelper.GetDpi(passwordBox).PixelsPerDip;
                    var formatted = new FormattedText(
                        mask,
                        CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        new Typeface(passwordBox.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                        passwordBox.FontSize,
                        Brushes.Black,
                        dpi);
                    textWidth = formatted.WidthIncludingTrailingWhitespace;
                }

                Canvas.SetLeft(caret, origin.X + textWidth);
            }
            else
            {
                return;
            }

            Canvas.SetTop(caret, (passwordBox.ActualHeight - caret.Height) / 2);
            caret.Fill = passwordBox.IsKeyboardFocused
                ? (Brush)FindResource("InputFocusForegroundBrush")
                : (Brush)FindResource("InputForegroundBrush");
        }
        catch
        {
            // ignore layout races
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Desktop)
        {
            Dispatcher.Invoke(() =>
            {
                ApplyTheme(IsAppsLightTheme());
                if (_backdrop is not null)
                {
                    if (TryFindResource("DimOverlayBrush") is Brush dim)
                        _backdrop.ApplyDimBrush(dim);
                    _backdrop.RefreshDesktopBackground();
                }
            });
        }
    }

    private void ApplyTheme(bool light)
    {
        var accent = GetSystemAccentColor();
        var accentLight = GetSystemAccentLightColor(accent);
        var accentDark = Darken(accent, 0.22);

        SetBrush("AccentBrush", accent);
        SetBrush("AccentLightBrush", accentLight);
        SetBrush("AccentDarkBrush", accentDark);
        SetBrush("InputFocusBorderBrush", accentLight);
        SetBrush("FieldActionGlyphHoverBrush", accentLight);

        // More choices tiles: translucent accent selection; black washes darken / lift rows.
        SetBrush("MoreChoicesSelectedBrush", Color.FromArgb(0x55, accent.R, accent.G, accent.B));
        SetBrush("MoreChoicesSelectedDimBrush", AccentWithBlackWash(accent, accentAlpha: 0x55, blackAlpha: 0x28));
        SetBrush("MoreChoicesSelectedHoverBrush", AccentWithBlackWash(accent, accentAlpha: 0x55, blackAlpha: 0x3A));
        SetBrush("MoreChoicesHoverBrush", Color.FromArgb(0x2A, 0x00, 0x00, 0x00));

        // No + "Show more details" keep the classic Windows blue degrees (not system accent).
        SetBrush("NoRestBrush", Color.FromRgb(0x00, 0x78, 0xD4));
        SetBrush("NoHoverBrush", Color.FromRgb(0x60, 0xCD, 0xFF));
        SetBrush("NoPressedBrush", Color.FromRgb(0x00, 0x5A, 0x9E));

        // Secure-desktop dim is always dark, even when apps use light theme.
        SetBrush("DimOverlayBrush", Color.FromArgb(0xCC, 0x00, 0x00, 0x00));

        if (light)
        {
            SetBrush("HeaderBrush", Color.FromRgb(0xE8, 0xE8, 0xE8));
            SetBrush("FooterBrush", Color.FromRgb(0xF3, 0xF3, 0xF3));
            SetBrush("BodyBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush("CardBorderBrush", Color.FromRgb(0xD0, 0xD0, 0xD0));
            SetBrush("PrimaryTextBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            SetBrush("SecondaryTextBrush", Color.FromRgb(0x5C, 0x5C, 0x5C));
            SetBrush("LinkBrush", Color.FromRgb(0x00, 0x63, 0xB1));
            SetBrush("KeyboardFocusBorderBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            SetBrush("InputBackgroundBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush("InputBorderBrush", Color.FromRgb(0x6B, 0x6B, 0x6B));
            SetBrush("InputFocusBackgroundBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush("InputFocusForegroundBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            SetBrush("InputForegroundBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            SetBrush("PlaceholderBrush", Color.FromRgb(0x5A, 0x5A, 0x5A));
            SetBrush("PlaceholderFocusBrush", Color.FromRgb(0x8A, 0x8A, 0x8A));
            SetBrush("FieldActionGlyphBrush", Color.FromRgb(0x5C, 0x5C, 0x5C));
            SetBrush("CredentialErrorBrush", Color.FromRgb(0xC4, 0x2B, 0x1C));

            SetBrush("YesRestBrush", Color.FromRgb(0xE8, 0xE8, 0xE8));
            SetBrush("YesHoverBrush", Color.FromRgb(0xF2, 0xF2, 0xF2));
            SetBrush("YesPressedBrush", Color.FromRgb(0xD0, 0xD0, 0xD0));
            SetBrush("YesBorderBrush", Color.FromRgb(0xC0, 0xC0, 0xC0));
            SetBrush("YesForegroundBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            // Light UAC: Yes black on gray, No white on accent blue.
            SetBrush("NoForegroundBrush", Colors.White);
            SetBrush("NoHoverForegroundBrush", Colors.White);
            // More choices: link hover darker than white body.
            SetBrush("MoreChoicesLinkHoverBrush", Color.FromRgb(0x5C, 0x5C, 0x5C));
            SetBrush("OverlayScrollThumbBrush", Color.FromArgb(0x55, 0x00, 0x00, 0x00));
            SetBrush("OverlayScrollThumbHoverBrush", Color.FromRgb(0xCD, 0xCD, 0xCD));
            SetBrush("OverlayScrollTrackHoverBrush", Color.FromRgb(0xF0, 0xF0, 0xF0));
        }
        else
        {
            SetBrush("HeaderBrush", Color.FromRgb(0x3A, 0x3A, 0x3A));
            SetBrush("FooterBrush", Color.FromRgb(0x1C, 0x1C, 0x1C));
            SetBrush("BodyBrush", Color.FromRgb(0x2B, 0x2B, 0x2B));
            SetBrush("CardBorderBrush", Color.FromRgb(0x3F, 0x3F, 0x3F));
            SetBrush("PrimaryTextBrush", Colors.White);
            SetBrush("SecondaryTextBrush", Color.FromRgb(0xB0, 0xB0, 0xB0));
            SetBrush("LinkBrush", Color.FromRgb(0x4C, 0xC2, 0xFF));
            SetBrush("KeyboardFocusBorderBrush", Colors.White);
            SetBrush("InputBackgroundBrush", Color.FromRgb(0x1C, 0x1C, 0x1C));
            SetBrush("InputBorderBrush", Color.FromRgb(0x6E, 0x6E, 0x6E));
            SetBrush("InputFocusBackgroundBrush", Colors.White);
            SetBrush("InputFocusForegroundBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            SetBrush("InputForegroundBrush", Colors.White);
            SetBrush("PlaceholderBrush", Color.FromRgb(0x6E, 0x6E, 0x6E));
            SetBrush("PlaceholderFocusBrush", Color.FromRgb(0xA8, 0xA8, 0xA8));
            SetBrush("FieldActionGlyphBrush", Color.FromRgb(0x7A, 0x7A, 0x7A));
            SetBrush("CredentialErrorBrush", Color.FromRgb(0xEF, 0xCE, 0x0B));

            SetBrush("YesRestBrush", Color.FromRgb(0x32, 0x32, 0x32));
            SetBrush("YesHoverBrush", Color.FromRgb(0x40, 0x40, 0x40));
            SetBrush("YesPressedBrush", Color.FromRgb(0x1C, 0x1C, 0x1C));
            SetBrush("YesBorderBrush", Color.FromRgb(0x47, 0x47, 0x47));
            SetBrush("YesForegroundBrush", Colors.White);
            // Dark UAC: Yes white on gray, No black on accent.
            SetBrush("NoForegroundBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            SetBrush("NoHoverForegroundBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            // More choices: link hover lighter than body (and lighter than prior #7A).
            SetBrush("MoreChoicesLinkHoverBrush", Color.FromRgb(0xB8, 0xB8, 0xB8));
            SetBrush("OverlayScrollThumbBrush", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            SetBrush("OverlayScrollThumbHoverBrush", Color.FromRgb(0x68, 0x68, 0x68));
            SetBrush("OverlayScrollTrackHoverBrush", Color.FromRgb(0x1E, 0x1E, 0x1E));
        }

        SetBrush("CloseHoverBrush", Color.FromRgb(0xC4, 0x2B, 0x1C));
        SetBrush("ClosePressedBrush", Color.FromRgb(0x79, 0x14, 0x0D));

        if (_moreChoicesExpanded)
            UpdateMoreChoicesTileVisuals();
    }

    /// <summary>
    /// Windows accent from Settings → Personalization → Colors (DWM AccentColor, ABGR).
    /// </summary>
    private static Color GetSystemAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int value)
            {
                var u = unchecked((uint)value);
                return Color.FromRgb(
                    (byte)(u & 0xFF),
                    (byte)((u >> 8) & 0xFF),
                    (byte)((u >> 16) & 0xFF));
            }
        }
        catch
        {
            // ignore
        }

        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    /// <summary>
    /// Lighter accent used for focus borders / hover glyphs (AccentPalette light slot when present).
    /// </summary>
    private static Color GetSystemAccentLightColor(Color accent)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 20)
            {
                // Index 4 (bytes 16-19) is commonly the light highlight used by WinUI focus chrome.
                return Color.FromRgb(palette[16], palette[17], palette[18]);
            }
        }
        catch
        {
            // ignore
        }

        return Lighten(accent, 0.35);
    }

    private static Color Lighten(Color color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(color.R + (255 - color.R) * amount),
            (byte)(color.G + (255 - color.G) * amount),
            (byte)(color.B + (255 - color.B) * amount));
    }

    private static Color Darken(Color color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(color.R * (1 - amount)),
            (byte)(color.G * (1 - amount)),
            (byte)(color.B * (1 - amount)));
    }

    /// <summary>
    /// Translucent accent with a transparent black wash on top (RGB darkened, alpha nudged up).
    /// </summary>
    private static Color AccentWithBlackWash(Color accent, byte accentAlpha, byte blackAlpha)
    {
        var blackT = blackAlpha / 255.0;
        var r = (byte)Math.Clamp(accent.R * (1.0 - blackT), 0, 255);
        var g = (byte)Math.Clamp(accent.G * (1.0 - blackT), 0, 255);
        var b = (byte)Math.Clamp(accent.B * (1.0 - blackT), 0, 255);
        var a = (byte)Math.Clamp(accentAlpha + blackAlpha / 2, 0, 255);
        return Color.FromArgb(a, r, g, b);
    }

    private static Color GetContrastingInk(Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
        return luminance > 0.62
            ? Color.FromRgb(0x00, 0x1A, 0x33)
            : Colors.White;
    }

    private void SetBrush(string key, Color color)
    {
        if (Resources[key] is SolidColorBrush existing && !existing.IsFrozen)
        {
            existing.Color = color;
            return;
        }

        Resources[key] = new SolidColorBrush(color);
    }

    private static bool IsAppsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int i)
                return i != 0;
        }
        catch
        {
            // ignore
        }

        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        OperationLog.Info("SessionEnd");
        StopFocusWatch();
        LockSetForegroundWindow(LsfwUnlock);
        _focusWatchTimer.Tick -= FocusWatchTimer_Tick;
        Deactivated -= Window_Deactivated;
        UninstallKeyboardLock();
        _caretTimer.Stop();
        _caretTimer.Tick -= CaretTimer_Tick;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (_uacSoundPlayer is not null)
        {
            _uacSoundPlayer.Stop();
            _uacSoundPlayer.Close();
            _uacSoundPlayer = null;
        }

        if (_uacAmplifiedTempPath is not null)
        {
            try
            {
                File.Delete(_uacAmplifiedTempPath);
            }
            catch
            {
                // ignore
            }

            _uacAmplifiedTempPath = null;
        }

        base.OnClosed(e);
    }

    private void InstallKeyboardLock()
    {
        if (_keyboardHook != IntPtr.Zero)
            return;

        _keyboardProc = KeyboardHookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = curModule is null
            ? IntPtr.Zero
            : GetModuleHandle(curModule.ModuleName);

        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, moduleHandle, 0);
    }

    private void UninstallKeyboardLock()
    {
        if (_keyboardHook == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
        _keyboardProc = null;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HcAction &&
            (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown))
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

            // Esc must always dismiss even if the dialog lost keyboard focus
            // (e.g. after a click fell through to the desktop).
            if (info.VkCode == VkEscape &&
                (info.Flags & LlkhfAltdown) == 0 &&
                (GetAsyncKeyState(VkControl) & 0x8000) == 0)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_isClosing)
                        Dismiss();
                });
                return (IntPtr)1;
            }

            if (ShouldBlockKey(info))
                return (IntPtr)1;

            // If another window owns focus, reclaim it and swallow this key so it cannot
            // type into the app behind the dimmed "secure desktop".
            if (!_isClosing && IsVisible && !IsDialogForeground())
            {
                Dispatcher.BeginInvoke(ForceSecureDesktopFocus, DispatcherPriority.Send);
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool ShouldBlockKey(KbdLlHookStruct info)
    {
        var vk = info.VkCode;

        // Win key (blocks Start and Win+* combos).
        if (vk is VkLWin or VkRWin)
            return true;

        var altDown = (info.Flags & LlkhfAltdown) != 0 ||
                      (GetAsyncKeyState(VkMenu) & 0x8000) != 0;
        var ctrlDown = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
        var shiftDown = (GetAsyncKeyState(VkShift) & 0x8000) != 0 ||
                        (GetAsyncKeyState(VkLShift) & 0x8000) != 0 ||
                        (GetAsyncKeyState(VkRShift) & 0x8000) != 0;

        // Alt+Tab / Alt+Esc
        if (altDown && vk is VkTab or VkEscape)
            return true;

        // Ctrl+Esc (Start menu)
        if (ctrlDown && vk == VkEscape)
            return true;

        // Alt+Shift / Ctrl+Shift language switch
        if (vk is VkShift or VkLShift or VkRShift)
        {
            if (altDown || ctrlDown)
                return true;
        }

        if ((altDown || ctrlDown) && vk is VkMenu or VkControl)
        {
            if (shiftDown)
                return true;
        }

        return false;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr DwExtraInfo;
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint LsfwLock = 1;
    private const uint LsfwUnlock = 2;
    private const int SwShow = 5;
    private const uint KeyeventfKeyup = 0x0002;

    // System menu: real UAC exposes Move + Close only.
    private const int GwlStyle = -16;
    private const int WsSizebox = 0x00040000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int ScSize = 0xF000;
    private const int ScMove = 0xF010;
    private const int ScMinimize = 0xF020;
    private const int ScMaximize = 0xF030;
    private const int ScClose = 0xF060;
    private const int ScRestore = 0xF120;
    private const uint MfByCommand = 0x00000000;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int WmSysCommand = 0x0112;
    private const int WmInitMenu = 0x0116;
    private const int WmInitMenuPopup = 0x0117;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockSetForegroundWindow(uint uLockCode);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetDCName(string? serverName, string? domainName, out IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? serverName, string userName, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserNameEx(int nameFormat, System.Text.StringBuilder lpNameBuffer, ref uint nSize);
}
