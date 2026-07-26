using System.Net.NetworkInformation;
using Lychee.Core;

namespace Lychee.Modules;

public sealed class LatencyModule : InfoModuleBase
{
    public override string Id => "latency";
    public override string DisplayName => "Network Latency";
    public override string Icon => "\uE9D9";

    private static readonly string[] Targets = { "223.5.5.5", "1.1.1.1", "8.8.8.8" };
    private const int TimeoutMs = 1000;
    private const int MaxSamples = 12;

    private PeriodicTimer? _timer;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private readonly Queue<long> _recentLatencies = new();
    private int _consecutiveFailures;

    public LatencyModule()
    {
        CurrentValue = "Ping  — ms";
        Detail = "Collecting…";
    }

    public override void Start()
    {
        if (_timer != null) return;
        _recentLatencies.Clear();
        _consecutiveFailures = 0;
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        _loopTask = Task.Run(async () =>
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(_cts.Token))
                {
                    await UpdateAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task UpdateAsync(CancellationToken ct)
    {
        try
        {
            var tasks = Targets.Select(async target =>
            {
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(target, TimeoutMs);
                    return (target, (long?)reply.RoundtripTime);
                }
                catch { return (target, (long?)null); }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            var best = results
                .Where(r => r.Item2.HasValue)
                .OrderBy(r => r.Item2!.Value)
                .FirstOrDefault();

            if (best.Item2.HasValue)
            {
                var latency = best.Item2.Value;
                _consecutiveFailures = 0;
                _recentLatencies.Enqueue(latency);
                while (_recentLatencies.Count > MaxSamples)
                    _recentLatencies.Dequeue();

                var arr = _recentLatencies.ToArray();
                var min = arr.Min();
                var max = arr.Max();
                var avg = (long)arr.Average();

                CurrentValue = $"Ping  {latency} ms";
                Detail = $"{best.Item1} · avg {avg} · min {min} · max {max} ms";
            }
            else
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= 3)
                    _recentLatencies.Clear();

                CurrentValue = "Ping  — ms";
                Detail = "Network unreachable";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    public override void Stop()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        try { _loopTask?.Wait(2000); } catch { }
        _timer = null;
        _cts = null;
        _loopTask = null;
    }
}
