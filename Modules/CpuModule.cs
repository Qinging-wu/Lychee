using System.Runtime.InteropServices;
using Lychee.Core;

namespace Lychee.Modules;

public sealed class CpuModule : InfoModuleBase
{
    public override string Id => "cpu";
    public override string DisplayName => "CPU Usage";
    public override string Icon => "\uE950";

    private const long TicksPerSecond = 10_000_000;
    private const long MaxSampleGap = 60 * TicksPerSecond;

    private PeriodicTimer? _timer;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private long _lastIdleTime;
    private long _lastKernelTime;
    private long _lastUserTime;
    private bool _hasLastSample;

    public CpuModule()
    {
        CurrentValue = "CPU  —%";
        Detail = "Collecting…";
    }

    public override void Start()
    {
        if (_timer != null) return;
        _hasLastSample = false;
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
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return;

            var idleLong = ToLong(idle);
            var kernelLong = ToLong(kernel);
            var userLong = ToLong(user);

            if (!_hasLastSample)
            {
                SetBaseline(idleLong, kernelLong, userLong);
                return;
            }

            var deltaIdle = idleLong - _lastIdleTime;
            var deltaKernel = kernelLong - _lastKernelTime;
            var deltaUser = userLong - _lastUserTime;
            var deltaTotal = deltaKernel + deltaUser;

            if (deltaTotal <= 0 || deltaIdle < 0 || deltaTotal > MaxSampleGap)
            {
                SetBaseline(idleLong, kernelLong, userLong);
                CurrentValue = "CPU  —%";
                Detail = "Collecting…";
                return;
            }

            SetBaseline(idleLong, kernelLong, userLong);

            var usage = (1.0 - (double)deltaIdle / deltaTotal) * 100;
            if (usage < 0) usage = 0;
            if (usage > 100) usage = 100;

            CurrentValue = $"CPU  {usage:F0}%";
            Detail = $"{Environment.ProcessorCount} logical cores";
        }
        catch (Exception ex)
        {
            AppLog.Error("cpu", ex);
        }
    }

    private void SetBaseline(long idle, long kernel, long user)
    {
        _lastIdleTime = idle;
        _lastKernelTime = kernel;
        _lastUserTime = user;
        _hasLastSample = true;
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

    private static long ToLong(FILETIME ft) =>
        (long)(((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }
}
