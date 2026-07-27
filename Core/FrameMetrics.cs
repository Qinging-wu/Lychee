namespace Lychee.Core;

public enum FrameMonitoringMode
{
    DesktopOutput,
    ForegroundApplication
}

/// <summary>
/// A point-in-time view of a frame source. Sources may leave metrics null when
/// the operating system cannot provide that measurement reliably.
/// </summary>
public sealed record FrameMetricsSnapshot(
    DateTimeOffset CapturedAt,
    string SourceName,
    double? CurrentFps,
    double? AverageFps,
    double? OnePercentLowFps,
    double? MinimumFps,
    double? MaximumFps,
    TimeSpan Window,
    string? Status = null);

/// <summary>
/// Common boundary for the lightweight DWM sampler and a future ETW/PresentMon
/// process sampler.
/// </summary>
public interface IFrameMetricsSource : IDisposable
{
    string SourceName { get; }
    bool IsRunning { get; }
    void Start();
    bool TrySample(out FrameMetricsSnapshot snapshot);
    void Stop();
}

/// <summary>
/// Contract for a future per-process Present-event source. Keeping process
/// selection outside the module lets the settings UI switch targets later.
/// </summary>
public interface IProcessFrameMetricsSource : IFrameMetricsSource
{
    int? TargetProcessId { get; set; }
}

internal sealed class RollingFrameRateStatistics
{
    private readonly TimeSpan _window;
    private readonly Queue<FrameRateObservation> _observations = new();

    public RollingFrameRateStatistics(TimeSpan window)
    {
        _window = window;
    }

    public void Add(DateTimeOffset capturedAt, ulong frameCount, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;

        _observations.Enqueue(new FrameRateObservation(capturedAt, frameCount, elapsed));
        Trim(capturedAt);
    }

    public FrameMetricsSnapshot CreateSnapshot(DateTimeOffset capturedAt, string sourceName, string? status = null)
    {
        Trim(capturedAt);
        if (_observations.Count == 0)
        {
            return new FrameMetricsSnapshot(
                capturedAt, sourceName, null, null, null, null, null, _window, status);
        }

        var latest = _observations.Last();
        var current = latest.Frames / latest.Elapsed.TotalSeconds;
        ulong totalFrames = 0;
        var totalSeconds = 0d;
        var minimum = double.MaxValue;
        var maximum = double.MinValue;

        foreach (var observation in _observations)
        {
            var fps = observation.Frames / observation.Elapsed.TotalSeconds;
            totalFrames += observation.Frames;
            totalSeconds += observation.Elapsed.TotalSeconds;
            minimum = Math.Min(minimum, fps);
            maximum = Math.Max(maximum, fps);
        }

        return new FrameMetricsSnapshot(
            capturedAt,
            sourceName,
            current,
            totalSeconds > 0 ? totalFrames / totalSeconds : null,
            null,
            minimum,
            maximum,
            _window,
            status);
    }

    public void Clear() => _observations.Clear();

    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - _window;
        while (_observations.TryPeek(out var observation) && observation.CapturedAt < cutoff)
        {
            _observations.Dequeue();
        }
    }

    private sealed record FrameRateObservation(
        DateTimeOffset CapturedAt,
        ulong Frames,
        TimeSpan Elapsed);
}

internal sealed class RollingFrameTimeStatistics
{
    private readonly TimeSpan _window;
    private readonly object _sync = new();
    private readonly Queue<FrameTimeObservation> _observations = new();

    public RollingFrameTimeStatistics(TimeSpan window)
    {
        _window = window;
    }

    public void Add(DateTimeOffset capturedAt, double frameTimeMilliseconds)
    {
        if (!double.IsFinite(frameTimeMilliseconds) || frameTimeMilliseconds <= 0) return;

        lock (_sync)
        {
            _observations.Enqueue(new FrameTimeObservation(capturedAt, frameTimeMilliseconds));
            Trim(capturedAt);
        }
    }

    public FrameMetricsSnapshot CreateSnapshot(DateTimeOffset capturedAt, string sourceName, string? status = null)
    {
        lock (_sync)
        {
            Trim(capturedAt);
            if (_observations.Count == 0)
            {
                return new FrameMetricsSnapshot(
                    capturedAt, sourceName, null, null, null, null, null, _window, status);
            }

            var all = _observations.Select(x => x.FrameTimeMilliseconds).ToArray();
            var recentCutoff = capturedAt - TimeSpan.FromSeconds(1);
            var recent = _observations
                .Where(x => x.CapturedAt >= recentCutoff)
                .Select(x => x.FrameTimeMilliseconds)
                .ToArray();
            Array.Sort(all);
            var averageFrameTime = all.Average();
            var p99Index = Math.Clamp((int)Math.Ceiling(all.Length * 0.99) - 1, 0, all.Length - 1);

            return new FrameMetricsSnapshot(
                capturedAt,
                sourceName,
                recent.Length > 0 ? 1000d / recent.Average() : null,
                1000d / averageFrameTime,
                1000d / all[p99Index],
                1000d / all[^1],
                1000d / all[0],
                _window,
                status);
        }
    }

    public void Clear()
    {
        lock (_sync) _observations.Clear();
    }

    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - _window;
        while (_observations.TryPeek(out var observation) && observation.CapturedAt < cutoff)
        {
            _observations.Dequeue();
        }
    }

    private sealed record FrameTimeObservation(DateTimeOffset CapturedAt, double FrameTimeMilliseconds);
}
