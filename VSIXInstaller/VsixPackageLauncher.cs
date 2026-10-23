using System.Diagnostics;
using System.IO;

namespace VSIXInstaller;

/// <summary>
/// Launches Visual Studio's VSIXInstaller for the fixed Temp package folder.
/// </summary>
internal static class VsixPackageLauncher
{
    private const string PackageFolderName = "ResXManager.VSIX";
    private const string PackageFileName = "ResXManager.VSIX.vsix";

    public static string PackagePath =>
        Path.Combine(Path.GetTempPath(), PackageFolderName, PackageFileName);

    /// <summary>
    /// Starts VSIXInstaller.exe with the ResXManager package. Returns false when the
    /// package or installer cannot be found / started.
    /// </summary>
    public static bool TryLaunch(out string detail)
    {
        var packagePath = PackagePath;
        if (!File.Exists(packagePath))
        {
            detail = $"package_missing path={packagePath}";
            return false;
        }

        var installer = TryFindVsixInstaller();
        if (installer is null)
        {
            detail = "vsixinstaller_not_found";
            return false;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = installer,
                Arguments = $"\"{packagePath}\"",
                UseShellExecute = true,
            };

            using var process = Process.Start(start);
            if (process is null)
            {
                detail = $"start_failed installer={installer}";
                return false;
            }

            detail = $"installer={installer}; package={packagePath}; pid={process.Id}";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"exception={ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string? TryFindVsixInstaller()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");

        if (File.Exists(vswhere))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-latest -find **/VSIXInstaller.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process is not null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(10_000);
                    var line = output
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(static l => l.Trim())
                        .FirstOrDefault(File.Exists);
                    if (line is not null)
                        return line;
                }
            }
            catch
            {
                // Fall through to well-known paths.
            }
        }

        foreach (var year in new[] { "2022", "18", "17", "2026" })
        {
            foreach (var edition in new[] { "Professional", "Enterprise", "Community", "BuildTools" })
            {
                var candidate = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Microsoft Visual Studio",
                    year,
                    edition,
                    @"Common7\IDE\VSIXInstaller.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
