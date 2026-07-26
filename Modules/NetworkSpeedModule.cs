using System.Net.NetworkInformation;
using Lychee.Core;

namespace Lychee.Modules;

public sealed class NetworkSpeedModule : InfoModuleBase
{
    public override string Id => "network-speed";
    public override string DisplayName => "Network Speed";
    public override string Icon => "\uE704";

    private PeriodicTimer? _timer;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private NetworkInterface? _nic;
    private long _lastBytesSent;
    private long _lastBytesReceived;
    private DateTime _lastSampleTime;

    public NetworkSpeedModule()
    {
        PickBestInterface();
        CurrentValue = "↓ 0 B/s   ↑ 0 B/s";
    }

    private void PickBestInterface()
    {
        _nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .OrderByDescending(n =>
            {
                try { return n.GetIPv4Statistics().BytesReceived + n.GetIPv4Statistics().BytesSent; }
                catch { return 0; }
            })
            .FirstOrDefault();
    }

    public override void Start()
    {
        if (_timer != null) return;
        if (_nic == null)
        {
            CurrentValue = "No network adapter found";
            return;
        }

        try
        {
            var stats = _nic.GetIPv4Statistics();
            _lastBytesSent = stats.BytesSent;
            _lastBytesReceived = stats.BytesReceived;
            _lastSampleTime = DateTime.Now;
        }
        catch { return; }

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
            if (_nic == null) return;
            var stats = _nic.GetIPv4Statistics();
            var now = DateTime.Now;
            var dt = (now - _lastSampleTime).TotalSeconds;
            if (dt <= 0) return;

            var down = (stats.BytesReceived - _lastBytesReceived) / dt;
            var up = (stats.BytesSent - _lastBytesSent) / dt;

            _lastBytesSent = stats.BytesSent;
            _lastBytesReceived = stats.BytesReceived;
            _lastSampleTime = now;

            if (down < 0) down = 0;
            if (up < 0) up = 0;

            CurrentValue = $"↓ {FormatSpeed(down)}   ↑ {FormatSpeed(up)}";
        }
        catch { }
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:F0} B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024:F1} KB/s";
        return $"{bytesPerSecond / 1024 / 1024:F2} MB/s";
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
