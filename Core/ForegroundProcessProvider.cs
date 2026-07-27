using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Lychee.Core;

public sealed record ForegroundProcessInfo(int ProcessId, string ProcessName, string? WindowTitle);

public sealed class ForegroundProcessProvider
{
    public ForegroundProcessInfo? GetCurrent()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == Environment.ProcessId) return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var title = new StringBuilder(512);
            _ = GetWindowText(window, title, title.Capacity);
            return new ForegroundProcessInfo(
                (int)processId,
                process.ProcessName,
                title.Length > 0 ? title.ToString() : null);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);
}
