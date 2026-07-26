using System.Runtime.InteropServices;
using Lychee.Core;

namespace Lychee.Modules;

public sealed class MemoryModule : InfoModuleBase
{
    public override string Id => "memory";
    public override string DisplayName => "Memory Usage";
    public override string Icon => "\uE7C0";

    private PeriodicTimer? _timer;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;

    public MemoryModule()
    {
        CurrentValue = "RAM  —%";
    }

    public override void Start()
    {
        if (_timer != null) return;
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _loopTask = Task.Run(async () =>
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(_cts.Token))
                {
                    Update();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void Update()
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
            };
            if (!GlobalMemoryStatusEx(ref memStatus))
                return;

            var load = memStatus.dwMemoryLoad;
            var totalBytes = memStatus.ullTotalPhys;
            var availBytes = memStatus.ullAvailPhys;
            var usedBytes = totalBytes - availBytes;

            CurrentValue = $"RAM  {load}%\n{FormatBytes(usedBytes)} / {FormatBytes(totalBytes)}";
            Detail = $"Free {FormatBytes(availBytes)} · Page {FormatBytes(memStatus.ullAvailPageFile)}/{FormatBytes(memStatus.ullTotalPageFile)}";
        }
        catch { }
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024UL * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    public override void Stop()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        try { _loopTask?.Wait(500); } catch { }
        _timer = null;
        _cts = null;
        _loopTask = null;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
