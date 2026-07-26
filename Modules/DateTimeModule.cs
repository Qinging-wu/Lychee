using Lychee.Core;

namespace Lychee.Modules;

public sealed class DateTimeModule : InfoModuleBase
{
    public override string Id => "datetime";
    public override string DisplayName => "Date & Time";
    public override string Icon => "\uE823";

    private PeriodicTimer? _timer;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;

    public override void Start()
    {
        if (_timer != null) return;
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _loopTask = Task.Run(async () =>
        {
            Update();
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
        CurrentValue = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
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
}
