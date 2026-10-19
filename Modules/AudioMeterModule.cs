using System.Windows.Threading;
using Lychee.Core;
using NAudio.CoreAudioApi;

namespace Lychee.Modules;

public sealed class AudioMeterModule : InfoModuleBase, IAudioSpectrumProvider
{
    private const float FloorThreshold = 0.001f;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private DispatcherTimer? _timer;
    private WasapiAudioCaptureSource? _captureSource;
    private MMDeviceEnumerator? _meterEnumerator;
    private MMDevice? _meterEndpoint;
    private Task _pendingSourceStop = Task.CompletedTask;
    private DateTimeOffset _nextMeterRetryAt;
    private int _meterRetryCount;
    private bool _spectrumActive;
    private string _spectrumStatus = "Spectrum inactive";

    public override string Id => "audio-meter";
    public override string DisplayName => "Audio Meter";
    public override string Icon => "\uE767";

    /// <summary>The latest immutable FFT result. The view polls this at its own render cadence.</summary>
    public SpectrumFrame? LatestSpectrum => _captureSource?.LatestFrame;
    public string SpectrumStatus => _spectrumStatus;

    public AudioMeterModule()
    {
        CurrentValue = "Level  --%";
        Detail = "Waiting for audio...";
    }

    public override void Start()
    {
        if (_timer is not null) return;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = AudioMeterPollingPolicy.GetInterval(_spectrumActive)
        };
        _timer.Tick += OnTick;
        _timer.Start();

        if (_pendingSourceStop.IsCompleted)
            InitializeCaptureSource();
        else
            _ = InitializeCaptureSourceAfterStopAsync(_pendingSourceStop);
    }

    /// <summary>Enables expensive spectrum capture only while the info panel is actually visible.</summary>
    public void SetSpectrumActive(bool active)
    {
        _spectrumActive = active;
        if (_timer is not null)
            _timer.Interval = AudioMeterPollingPolicy.GetInterval(active);
        _captureSource?.SetActive(active);
        if (!active)
            _spectrumStatus = "Spectrum paused";
        else if (_captureSource is null)
            _spectrumStatus = "Waiting for audio...";
    }

    private async Task InitializeCaptureSourceAfterStopAsync(Task previousStop)
    {
        try { await previousStop.ConfigureAwait(true); }
        catch (Exception ex) { AppLog.Warn(nameof(AudioMeterModule), $"Previous capture session did not stop cleanly: {ex}"); }
        if (_timer is not null)
            InitializeCaptureSource();
    }

    private void InitializeCaptureSource()
    {
        try
        {
            var source = new WasapiAudioCaptureSource();
            source.StatusChanged += OnSpectrumStatusChanged;
            source.DefaultDeviceChanged += OnDefaultDeviceChanged;
            _captureSource = source;
            _spectrumStatus = _spectrumActive ? "Waiting for audio..." : "Spectrum paused";
            RefreshMeterEndpoint();
            source.SetActive(_spectrumActive);
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(AudioMeterModule), $"Unable to initialize audio spectrum: {ex}");
            _spectrumStatus = "Spectrum unavailable";
            Detail = "Audio endpoint unavailable";
            ScheduleMeterRetry();
        }
    }

    private void OnSpectrumStatusChanged(string status)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        if (_dispatcher.CheckAccess())
        {
            _spectrumStatus = status;
            return;
        }

        try { _dispatcher.BeginInvoke(() => _spectrumStatus = status, DispatcherPriority.Background); }
        catch (InvalidOperationException) { }
    }

    private void OnDefaultDeviceChanged()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        try { _dispatcher.BeginInvoke(RefreshMeterEndpoint, DispatcherPriority.Background); }
        catch (InvalidOperationException) { }
    }

    private void RefreshMeterEndpoint()
    {
        DisposeMeterEndpoint();
        try
        {
            _meterEnumerator ??= new MMDeviceEnumerator();
            _meterEndpoint = _meterEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            _meterRetryCount = 0;
            _nextMeterRetryAt = DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(AudioMeterModule), $"Unable to open default audio endpoint meter: {ex.Message}");
            CurrentValue = "Level  --%";
            Detail = "Audio endpoint unavailable";
            ScheduleMeterRetry();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_meterEndpoint is null)
        {
            if (DateTimeOffset.UtcNow >= _nextMeterRetryAt)
                RefreshMeterEndpoint();
            if (_meterEndpoint is null)
            {
                CurrentValue = "Level  --%";
                Progress = null;
                return;
            }
        }

        try
        {
            var peak = Math.Clamp(_meterEndpoint.AudioMeterInformation.MasterPeakValue, 0f, 1f);
            var decibels = 20 * Math.Log10(Math.Max(peak, FloorThreshold));
            var percent = (int)Math.Round(peak * 100);
            CurrentValue = $"Level  {percent}%";
            Detail = peak > FloorThreshold ? $"{decibels:0} dBFS | Active" : "Silent";
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(AudioMeterModule), $"Audio peak meter read failed: {ex.Message}");
            DisposeMeterEndpoint();
            CurrentValue = "Level  --%";
            Detail = "Audio endpoint unavailable";
            ScheduleMeterRetry();
        }
    }

    private void ScheduleMeterRetry()
    {
        var delays = new[] { 1, 2, 5 };
        var seconds = delays[Math.Min(_meterRetryCount, delays.Length - 1)];
        _meterRetryCount++;
        _nextMeterRetryAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    private void DisposeMeterEndpoint()
    {
        var endpoint = _meterEndpoint;
        _meterEndpoint = null;
        endpoint?.Dispose();
    }

    public override void Stop()
    {
        _timer?.Stop();
        if (_timer is not null)
            _timer.Tick -= OnTick;
        _timer = null;

        DisposeMeterEndpoint();
        _meterEnumerator?.Dispose();
        _meterEnumerator = null;

        var source = _captureSource;
        _captureSource = null;
        if (source is not null)
        {
            source.StatusChanged -= OnSpectrumStatusChanged;
            source.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            _pendingSourceStop = source.DisposeAsync().AsTask();
        }

        _spectrumStatus = "Spectrum inactive";
        Progress = null;
    }

    public override async Task StopAsync()
    {
        Stop();
        await _pendingSourceStop.ConfigureAwait(true);
    }
}
