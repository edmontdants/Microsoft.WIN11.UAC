using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace VSIXInstaller;

/// <summary>
/// Full-screen wallpaper + dim layer behind the credential dialog (secure-desktop look).
/// Absorbs clicks without activating, so the credential window keeps focus and does not flicker.
/// </summary>
public partial class BackdropWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExNoactivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoactivate = 3;

    public BackdropWindow()
    {
        InitializeComponent();
        FitToPrimaryScreen();
        ApplyDesktopBackground();
        SourceInitialized += (_, _) => ApplyNoActivateStyle();
    }

    public void ApplyDimBrush(Brush brush) => DimOverlay.Background = brush;

    public void RefreshDesktopBackground() => ApplyDesktopBackground();

    private void ApplyNoActivateStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, ex | WsExNoactivate);

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Clicks on the dimmer must not deactivate the credential dialog.
        if (msg == WmMouseActivate)
        {
            handled = true;
            return (IntPtr)MaNoactivate;
        }

        return IntPtr.Zero;
    }

    private void FitToPrimaryScreen()
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Root.Width = Width;
        Root.Height = Height;
        DesktopWallpaper.Width = Width;
        DesktopWallpaper.Height = Height;
    }

    private void ApplyDesktopBackground()
    {
        var wallpaper = TryLoadWallpaperImage();
        if (wallpaper is null)
        {
            DesktopWallpaper.Source = null;
            DesktopWallpaper.Visibility = Visibility.Collapsed;
            return;
        }

        DesktopWallpaper.Source = wallpaper;
        DesktopWallpaper.Visibility = Visibility.Visible;
    }

    private static ImageSource? TryLoadWallpaperImage()
    {
        foreach (var path in GetWallpaperCandidatePaths())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;

                using var stream = File.OpenRead(path);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                // Try next candidate.
            }
        }

        return null;
    }

    private static IEnumerable<string> GetWallpaperCandidatePaths()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");

        string? registryPath = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            registryPath = key?.GetValue("WallPaper") as string;
        }
        catch
        {
            // ignore
        }

        if (!string.IsNullOrWhiteSpace(registryPath))
            yield return registryPath;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
