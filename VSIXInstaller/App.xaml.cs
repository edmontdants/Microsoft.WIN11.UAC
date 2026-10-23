using System.Linq;
using System.Windows;

namespace VSIXInstaller;

public partial class App : Application
{
    /// <summary>
    /// When true (<c>--test-vsix</c> / <c>/test-vsix</c>), submitting the current logged-in
    /// user skips LogonUser and still runs the post-success VSIX launch path.
    /// </summary>
    public static bool TestVsixLaunch { get; private set; }

    static App()
    {
        // Required for PasswordBox.SelectionTextBrush (white dots on accent selection).
        AppContext.SetSwitch(
            "Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering",
            false);

        TestVsixLaunch = Environment.GetCommandLineArgs().Any(static a =>
            a.Equals("--test-vsix", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/test-vsix", StringComparison.OrdinalIgnoreCase));
    }
}
