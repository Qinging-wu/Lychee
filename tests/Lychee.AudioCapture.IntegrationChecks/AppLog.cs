namespace Lychee.Core;

internal static class AppLog
{
    public static void Error(string source, Exception exception) => Console.Error.WriteLine($"ERROR [{source}] {exception}");
    public static void Warn(string source, string message) => Console.Error.WriteLine($"WARN [{source}] {message}");
}
