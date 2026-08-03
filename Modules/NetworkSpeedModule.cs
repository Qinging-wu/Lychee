using System.Net.NetworkInformation;
using Lychee.Core;

namespace Lychee.Modules;

public sealed class NetworkSpeedModule : InfoModuleBase
{
    public override string Id => "network-speed";
    public override string DisplayName => "Network Speed";
    public override string Icon => "\uE704";

    private const int IdleSamplesBeforeReselect = 4;

    private PeriodicTimer? _timer;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private NetworkInterface? _nic;
    private long _lastBytesSent;
    private long _lastBytesReceived;
    private DateTime _lastSampleTime;
    private int _idleSampleCount;

    public NetworkSpeedModule()
    {
        _nic = PickBestInterface();
        CurrentValue = "↓ 0 B/s   ↑ 0 B/s";
    }

    private static NetworkInterface? PickBestInterface()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsUsable)
            .OrderByDescending(TotalTraffic)
            .FirstOrDefault();
    }

    private static bool IsUsable(NetworkInterface nic)
    {
        if (nic == null) return false;
        if (nic.OperationalStatus != OperationalStatus.Up) return false;
        var type = nic.NetworkInterfaceType;
        if (type == NetworkInterfaceType.Loopback || type == NetworkInterfaceType.Tunnel) return false;
        try { nic.GetIPv4Statistics(); return true; }
        catch { return false; }
    }

    private static long TotalTraffic(NetworkInterface nic)
    {
        try
        {
            var s = nic.GetIPv4Statistics();
            return s.BytesReceived + s.BytesSent;
        }
        catch { return 0; }
    }

    private void ResetBaseline()
    {
        try
        {
            var stats = _nic!.GetIPv4Statistics();
            _lastBytesSent = stats.BytesSent;
            _lastBytesReceived = stats.BytesReceived;
        }
        catch (Exception ex)
        {
            AppLog.Error("network-speed", ex);
        }
        _lastSampleTime = DateTime.Now;
    }

    public override void Start()
    {
        if (_timer != null) return;
        if (_nic == null)
        {
            CurrentValue = "No network adapter found";
            return;
        }

        ResetBaseline();

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
            if (_nic == null || !IsUsable(_nic))
            {
                _nic = PickBestInterface();
                if (_nic == null)
                {
                    CurrentValue = "No network adapter found";
                    return;
                }
                ResetBaseline();
            }

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

            // If the current adapter has been idle for a while, the active route may
            // have moved (VPN drop/reconnect, WiFi<->Ethernet switch). Re-pick the
            // adapter with the most traffic and, if it changed, switch to it.
            if (down + up <= 0.1)
                _idleSampleCount++;
            else
                _idleSampleCount = 0;

            if (_idleSampleCount >= IdleSamplesBeforeReselect)
            {
                var best = PickBestInterface();
                if (best != null && best.Id != _nic.Id)
                {
                    _nic = best;
                    _idleSampleCount = 0;
                    ResetBaseline();
                }
            }

            CurrentValue = $"↓ {FormatSpeed(down)}   ↑ {FormatSpeed(up)}";
        }
        catch (Exception ex)
        {
            AppLog.Error("network-speed", ex);
        }
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
