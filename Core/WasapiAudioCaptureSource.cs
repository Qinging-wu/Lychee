using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Lychee.Core;

/// <summary>Runs bounded WASAPI loopback capture and spectrum analysis away from the WPF thread.</summary>
public sealed class WasapiAudioCaptureSource : IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)
    ];

    private readonly MMDeviceEnumerator _enumerator;
    private readonly EndpointNotificationClient _notificationClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _signalSync = new();
    private readonly object _disposeSync = new();
    private TaskCompletionSource _signal = NewSignal();
    private long _signalVersion;
    private Task? _controlTask;
    private Task? _disposeTask;
    private volatile string? _currentDeviceId;
    private int _hasReceivedAudio;
    private int _active;
    private int _disposed;
    private SpectrumFrame _latestFrame = SpectrumFrame.Silence(DateTimeOffset.UtcNow);

    public SpectrumFrame LatestFrame => Volatile.Read(ref _latestFrame);
    public event Action<string>? StatusChanged;
    public event Action? DefaultDeviceChanged;

    public WasapiAudioCaptureSource()
    {
        _enumerator = new MMDeviceEnumerator();
        _notificationClient = new EndpointNotificationClient(OnRenderEndpointChanged, OnEndpointRemoved);
        try
        {
            var result = _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            if (result != 0)
                MarshalResult.ThrowIfFailed(result, "RegisterEndpointNotificationCallback");
        }
        catch
        {
            _enumerator.Dispose();
            throw;
        }

        _controlTask = Task.Run(ControlLoopAsync);
    }

    public void SetActive(bool active)
    {
        lock (_disposeSync)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var value = active ? 1 : 0;
            if (Interlocked.Exchange(ref _active, value) == value) return;
            SignalControlLoop();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            Interlocked.Exchange(ref _disposed, 1);
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _active, 0);
        SignalControlLoop();
        _shutdown.Cancel();

        if (_controlTask is not null)
            await _controlTask.ConfigureAwait(false);

        try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); }
        catch (Exception ex) { AppLog.Warn(nameof(WasapiAudioCaptureSource), $"Unable to unregister audio device notifications: {ex.Message}"); }
        _enumerator.Dispose();
        _shutdown.Dispose();
    }

    private async Task ControlLoopAsync()
    {
        CaptureSession? session = null;
        var retryIndex = 0;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var observedSignal = GetSignalVersion();
                if (Volatile.Read(ref _active) == 0)
                {
                    if (session is not null)
                    {
                        await StopSessionAsync(session).ConfigureAwait(false);
                        session = null;
                    }
                    _currentDeviceId = null;
                    Interlocked.Exchange(ref _hasReceivedAudio, 0);
                    retryIndex = 0;
                    Volatile.Write(ref _latestFrame, SpectrumFrame.Silence(DateTimeOffset.UtcNow));
                    await WaitForSignalAsync(observedSignal).ConfigureAwait(false);
                    continue;
                }

                if (session is null)
                {
                    try
                    {
                        MMDevice? endpoint = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                        _currentDeviceId = endpoint.ID;
                        CaptureSession? newSession = null;
                        try
                        {
                            newSession = new CaptureSession(endpoint, PublishFrame, OnAnalysisFailure);
                            endpoint = null;
                            Volatile.Write(ref _activeSessionId, newSession.SessionId);
                            newSession.Start();
                            session = newSession;
                        }
                        catch
                        {
                            Volatile.Write(ref _activeSessionId, 0);
                            if (newSession is not null)
                                await newSession.DisposeAsync().ConfigureAwait(false);
                            throw;
                        }
                        finally
                        {
                            endpoint?.Dispose();
                        }
                        SetStatus("Waiting for audio...");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn(nameof(WasapiAudioCaptureSource), $"Unable to start loopback capture: {ex}");
                        SetStatus("Spectrum unavailable");
                        await WaitForRetryOrSignalAsync(observedSignal, GetRetryDelay(retryIndex++)).ConfigureAwait(false);
                        continue;
                    }
                }

                var signalTask = WaitForSignalAsync(observedSignal);
                var completed = await Task.WhenAny(session.Stopped, signalTask).ConfigureAwait(false);
                if (completed == signalTask)
                {
                    await signalTask.ConfigureAwait(false);
                    var activeSession = session;
                    await StopSessionAsync(activeSession).ConfigureAwait(false);
                    session = null;
                    if (Interlocked.Exchange(ref _hasReceivedAudio, 0) != 0)
                        retryIndex = 0;
                    Volatile.Write(ref _latestFrame, SpectrumFrame.Silence(DateTimeOffset.UtcNow));
                    if (Volatile.Read(ref _active) != 0)
                        SetStatus("Waiting for audio...");
                    continue;
                }

                var captureError = await session.Stopped.ConfigureAwait(false);
                await StopSessionAsync(session).ConfigureAwait(false);
                session = null;
                if (Interlocked.Exchange(ref _hasReceivedAudio, 0) != 0)
                    retryIndex = 0;
                Volatile.Write(ref _latestFrame, SpectrumFrame.Silence(DateTimeOffset.UtcNow));

                if (Volatile.Read(ref _active) != 0)
                {
                    SetStatus("Spectrum unavailable");
                    if (captureError is not null)
                        AppLog.Warn(nameof(WasapiAudioCaptureSource), $"Loopback capture stopped: {captureError}");
                    await WaitForRetryOrSignalAsync(GetSignalVersion(), GetRetryDelay(retryIndex++)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(WasapiAudioCaptureSource), ex);
            SetStatus("Spectrum unavailable");
        }
        finally
        {
            if (session is not null)
                await StopSessionAsync(session).ConfigureAwait(false);
            _currentDeviceId = null;
            Volatile.Write(ref _hasReceivedAudio, 0);
            Volatile.Write(ref _latestFrame, SpectrumFrame.Silence(DateTimeOffset.UtcNow));
        }
    }

    private async Task StopSessionAsync(CaptureSession session)
    {
        if (session.SessionId == Volatile.Read(ref _activeSessionId))
            Volatile.Write(ref _activeSessionId, 0);
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(WasapiAudioCaptureSource), $"Unable to stop loopback capture cleanly: {ex}");
        }
    }

    private void PublishFrame(long sessionId, SpectrumFrame frame)
    {
        if (Volatile.Read(ref _active) == 0 || sessionId != Volatile.Read(ref _activeSessionId)) return;
        Volatile.Write(ref _latestFrame, frame);
        if (Interlocked.Exchange(ref _hasReceivedAudio, 1) == 0)
            SetStatus("Listening");
    }

    private long _activeSessionId;

    private void OnAnalysisFailure(long sessionId, Exception exception)
    {
        if (sessionId != Volatile.Read(ref _activeSessionId)) return;
        AppLog.Warn(nameof(WasapiAudioCaptureSource), $"Spectrum analysis failed: {exception}");
        SetStatus("Spectrum unavailable");
        SignalControlLoop();
    }

    private void OnRenderEndpointChanged()
    {
        try { DefaultDeviceChanged?.Invoke(); }
        catch (Exception ex) { AppLog.Warn(nameof(WasapiAudioCaptureSource), $"Audio device listener failed: {ex.Message}"); }
        SignalControlLoop();
    }

    private void OnEndpointRemoved(string deviceId)
    {
        if (string.Equals(deviceId, _currentDeviceId, StringComparison.OrdinalIgnoreCase))
            OnRenderEndpointChanged();
    }

    private void SetStatus(string status)
    {
        try { StatusChanged?.Invoke(status); }
        catch (Exception ex) { AppLog.Warn(nameof(WasapiAudioCaptureSource), $"Spectrum status listener failed: {ex.Message}"); }
    }

    private async Task WaitForRetryOrSignalAsync(long observedSignal, TimeSpan delay)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var delayTask = Task.Delay(delay, delayCancellation.Token);
        var signalTask = WaitForSignalAsync(observedSignal);
        var completed = await Task.WhenAny(delayTask, signalTask).ConfigureAwait(false);
        if (completed == signalTask)
        {
            delayCancellation.Cancel();
            await signalTask.ConfigureAwait(false);
            return;
        }
        await delayTask.ConfigureAwait(false);
    }

    private Task WaitForSignalAsync(long observedSignal)
    {
        lock (_signalSync)
            return _signalVersion != observedSignal ? Task.CompletedTask : _signal.Task;
    }

    private long GetSignalVersion()
    {
        lock (_signalSync) return _signalVersion;
    }

    private void SignalControlLoop()
    {
        lock (_signalSync)
        {
            _signalVersion++;
            _signal.TrySetResult();
            _signal = NewSignal();
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TimeSpan GetRetryDelay(int retryIndex)
        => RetryDelays[Math.Min(retryIndex, RetryDelays.Length - 1)];

    private sealed class CaptureSession : IAsyncDisposable
    {
        private readonly WasapiLoopbackCapture _capture;
        private readonly MMDevice _endpoint;
        private readonly Channel<AudioPacket> _packets = Channel.CreateBounded<AudioPacket>(
            new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        private readonly TaskCompletionSource<Exception?> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action<long, SpectrumFrame> _publish;
        private readonly Action<long, Exception> _analysisFailed;
        private readonly long _sessionId;
        private readonly Task _analysisTask;
        private int _started;

        public Task<Exception?> Stopped => _stopped.Task;
        public long SessionId => _sessionId;

        public CaptureSession(
            MMDevice endpoint,
            Action<long, SpectrumFrame> publish,
            Action<long, Exception> analysisFailed)
        {
            _endpoint = endpoint;
            _capture = new WasapiLoopbackCapture(endpoint);
            _publish = publish;
            _analysisFailed = analysisFailed;
            _sessionId = Interlocked.Increment(ref _nextSessionId);
            _analysisTask = AnalyzePacketsAsync();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
        }

        private static long _nextSessionId;

        public void Start()
        {
            _capture.StartRecording();
            Volatile.Write(ref _started, 1);
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;
            var data = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
            _packets.Writer.TryWrite(new AudioPacket(data, DateTimeOffset.UtcNow));
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            _packets.Writer.TryComplete(e.Exception);
            _stopped.TrySetResult(e.Exception);
        }

        private async Task AnalyzePacketsAsync()
        {
            SpectrumAnalyzer? analyzer = null;
            try
            {
                analyzer = new SpectrumAnalyzer(_capture.WaveFormat);
                await foreach (var packet in _packets.Reader.ReadAllAsync().ConfigureAwait(false))
                    analyzer.Process(packet.Data, packet.CapturedAt, frame => _publish(_sessionId, frame));
            }
            catch (Exception ex)
            {
                _analysisFailed(_sessionId, ex);
                try { _capture.StopRecording(); }
                catch { }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _started) != 0 && !_stopped.Task.IsCompleted)
            {
                try { _capture.StopRecording(); }
                catch (Exception ex) { _stopped.TrySetResult(ex); }
            }

            if (Volatile.Read(ref _started) != 0)
                await _stopped.Task.ConfigureAwait(false);
            else
                _packets.Writer.TryComplete();

            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { await _analysisTask.ConfigureAwait(false); }
            catch { }
            _capture.Dispose();
            _endpoint.Dispose();
        }

        private sealed record AudioPacket(byte[] Data, DateTimeOffset CapturedAt);
    }

    private sealed class EndpointNotificationClient : IMMNotificationClient
    {
        private readonly Action _renderChanged;
        private readonly Action<string> _deviceRemoved;

        public EndpointNotificationClient(Action renderChanged, Action<string> deviceRemoved)
        {
            _renderChanged = renderChanged;
            _deviceRemoved = deviceRemoved;
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Console)
                _renderChanged();
        }

        public void OnDeviceRemoved(string deviceId) => _deviceRemoved(deviceId);
        public void OnDeviceAdded(string deviceId) { }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState != DeviceState.Active)
                _deviceRemoved(deviceId);
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    private static class MarshalResult
    {
        public static void ThrowIfFailed(int result, string operation)
        {
            if (result < 0)
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result,
                    new IntPtr(-1));
            if (result != 0)
                throw new InvalidOperationException($"{operation} returned 0x{result:X8}.");
        }
    }
}
