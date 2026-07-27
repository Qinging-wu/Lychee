using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lychee.Core;

/// <summary>
/// Samples the Desktop Window Manager's global composition counter. This is
/// desktop output/composition cadence, not a selected application's render FPS.
/// </summary>
public sealed class DwmFrameMetricsSource : IFrameMetricsSource
{
    private readonly RollingFrameRateStatistics _statistics = new(TimeSpan.FromSeconds(60));
    private ulong? _previousFrame;
    private long _previousTimestamp;

    public string SourceName => "Desktop output";
    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning) return;

        IsRunning = true;
        _previousFrame = null;
        _previousTimestamp = Stopwatch.GetTimestamp();
        _statistics.Clear();
    }

    public bool TrySample(out FrameMetricsSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;
        if (!IsRunning)
        {
            snapshot = Empty(now, "Sampler is stopped");
            return false;
        }

        var timing = new DwmTimingInfo
        {
            Size = (uint)Marshal.SizeOf<DwmTimingInfo>()
        };

        var result = DwmGetCompositionTimingInfo(IntPtr.Zero, ref timing);
        if (result < 0)
        {
            snapshot = Empty(now, $"DWM timing unavailable (0x{result:X8})");
            return false;
        }

        var timestamp = Stopwatch.GetTimestamp();
        if (_previousFrame is null)
        {
            _previousFrame = timing.Frame;
            _previousTimestamp = timestamp;
            snapshot = _statistics.CreateSnapshot(now, SourceName, "Warming up");
            return true;
        }

        var elapsed = Stopwatch.GetElapsedTime(_previousTimestamp, timestamp);
        if (timing.Frame >= _previousFrame && elapsed >= TimeSpan.FromMilliseconds(100))
        {
            // Ignore long pauses (sleep/resume or a blocked UI thread) instead of
            // reporting a misleading near-zero rate for that period.
            if (elapsed <= TimeSpan.FromSeconds(5))
            {
                _statistics.Add(now, timing.Frame - _previousFrame.Value, elapsed);
            }
        }
        else
        {
            _statistics.Clear();
        }

        _previousFrame = timing.Frame;
        _previousTimestamp = timestamp;
        snapshot = _statistics.CreateSnapshot(now, SourceName);
        return true;
    }

    public void Stop()
    {
        IsRunning = false;
        _previousFrame = null;
        _statistics.Clear();
    }

    public void Dispose() => Stop();

    private FrameMetricsSnapshot Empty(DateTimeOffset now, string status) =>
        new(now, SourceName, null, null, null, null, null, TimeSpan.FromSeconds(60), status);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetCompositionTimingInfo(IntPtr windowHandle, ref DwmTimingInfo timingInfo);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UnsignedRatio
    {
        public uint Numerator;
        public uint Denominator;
    }

    // dwmapi.h includes pshpack1.h before declaring DWM_TIMING_INFO.
    // Default CLR alignment makes this structure larger and DWM returns
    // MILERR_MISMATCHED_SIZE (0x88980090).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DwmTimingInfo
    {
        public uint Size;
        public UnsignedRatio RefreshRate;
        public ulong RefreshPeriod;
        public UnsignedRatio ComposeRate;
        public ulong VBlank;
        public ulong Refresh;
        public uint DxRefresh;
        public ulong Compose;
        public ulong Frame;
        public uint DxPresent;
        public ulong RefreshFrame;
        public ulong FrameSubmitted;
        public uint DxPresentSubmitted;
        public ulong FrameConfirmed;
        public uint DxPresentConfirmed;
        public ulong RefreshConfirmed;
        public uint DxRefreshConfirmed;
        public ulong FramesLate;
        public uint FramesOutstanding;
        public ulong FrameDisplayed;
        public ulong QpcFrameDisplayed;
        public ulong RefreshFrameDisplayed;
        public ulong FrameComplete;
        public ulong QpcFrameComplete;
        public ulong FramePending;
        public ulong QpcFramePending;
        public ulong FramesDisplayed;
        public ulong FramesComplete;
        public ulong FramesPending;
        public ulong FramesAvailable;
        public ulong FramesDropped;
        public ulong FramesMissed;
        public ulong RefreshNextDisplayed;
        public ulong RefreshNextPresented;
        public ulong RefreshesDisplayed;
        public ulong RefreshesPresented;
        public ulong RefreshStarted;
        public ulong PixelsReceived;
        public ulong PixelsDrawn;
        public ulong BuffersEmpty;
    }
}
