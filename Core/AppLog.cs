using System.IO;

namespace Lychee.Core;

/// <summary>
/// Minimal file logger. Writes to %AppData%\Lychee\logs\lychee.log and keeps
/// a bounded set of rotated files (size + count limits) so it never grows unbounded.
/// Thread-safe; safe to call from any module's background loop.
/// </summary>
public static class AppLog
{
    private const long MaxFileBytes = 512 * 1024;
    private const int MaxBackupFiles = 3;

    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lychee", "logs");
    private static readonly string LogFilePath = Path.Combine(LogDirectory, "lychee.log");

    public static void Error(string source, Exception exception)
        => Write("ERROR", source, exception.ToString());

    public static void Error(string source, string message)
        => Write("ERROR", source, message);

    public static void Warn(string source, string message)
        => Write("WARN", source, message);

    public static void Info(string source, string message)
        => Write("INFO", source, message);

    public static void Info(string message)
        => Write("INFO", "Lychee", message);

    private static void Write(string level, string source, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                RollIfNeeded();
                File.AppendAllText(LogFilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{source}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    private static void RollIfNeeded()
    {
        try
        {
            var fi = new FileInfo(LogFilePath);
            if (!fi.Exists || fi.Length < MaxFileBytes) return;

            for (var i = MaxBackupFiles - 1; i >= 1; i--)
            {
                var dst = $"{LogFilePath}.{i}";
                if (File.Exists(dst)) File.Delete(dst);
                var src = $"{LogFilePath}.{i - 1}";
                if (File.Exists(src)) File.Move(src, dst);
            }
            if (File.Exists(LogFilePath + ".0")) File.Delete(LogFilePath + ".0");
            File.Move(LogFilePath, LogFilePath + ".0");
        }
        catch
        {
            // Rotation failure should not crash the app either.
        }
    }
}
