using System.IO;
using System.Text;

namespace VSIXInstaller;

/// <summary>
/// Appends operational events (success and failure) under
/// %LOCALAPPDATA%\Temp\VSIXInstallerLogs.
/// </summary>
internal static class OperationLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath =
        Path.Combine(Path.GetTempPath(), "VSIXInstallerLogs");
    private static readonly string SessionFilePath = Path.Combine(
        DirectoryPath,
        $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");

    public static string LogDirectory => DirectoryPath;
    public static string CurrentSessionFile => SessionFilePath;

    public static void Info(string operation, string? detail = null) =>
        Write("INFO", operation, detail);

    public static void Success(string operation, string? detail = null) =>
        Write("SUCCESS", operation, detail);

    public static void Fail(string operation, string? detail = null) =>
        Write("FAIL", operation, detail);

    private static void Write(string level, string operation, string? detail)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append('\t')
                .Append(level)
                .Append('\t')
                .Append(operation);

            if (!string.IsNullOrWhiteSpace(detail))
            {
                line.Append('\t')
                    .Append(detail.Replace('\r', ' ').Replace('\n', ' '));
            }

            line.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(SessionFilePath, line.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break the UI.
        }
    }
}
