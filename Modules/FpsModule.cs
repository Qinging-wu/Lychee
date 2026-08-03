using System.Runtime.InteropServices;
using Lychee.Core;

namespace Lychee.Modules;

public sealed class FpsModule : InfoModuleBase
{
    private const int EnumCurrentSettings = -1;
    private readonly SettingsService _settings;
    private readonly Func<string?> _displayDeviceNameProvider;
    private readonly ForegroundProcessProvider _foregroundProcessProvider;
    private readonly IFrameMetricsSource _desktopSource;
    private readonly IProcessFrameMetricsSource _processSource;
    private readonly object _transitionSync = new();
    private System.Windows.Threading.DispatcherTimer? _timer;
    private Task? _transitionTask;
    private long _transitionVersion;
    private FrameMonitoringMode _requestedMode;
    private int? _requestedProcessId;
    private bool _stopRequested;
    private FrameMonitoringMode? _activeMode;
    private ForegroundProcessInfo? _targetProcess;
    private ForegroundProcessInfo? _candidateProcess;
    private int _candidateSamples;
    private int _activeProcessId;
    private string? _transitionError;

    public override string Id => "fps";
    public override string DisplayName => "Frame Performance";
    public override string Icon => "\uE7F4";

    public FpsModule(
        SettingsService settings,
        Func<string?>? displayDeviceNameProvider = null,
        ForegroundProcessProvider? foregroundProcessProvider = null,
        IFrameMetricsSource? desktopSource = null,
        IProcessFrameMetricsSource? processSource = null)
    {
        _settings = settings;
        _displayDeviceNameProvider = displayDeviceNameProvider ?? (() => null);
        _foregroundProcessProvider = foregroundProcessProvider ?? new ForegroundProcessProvider();
        _desktopSource = desktopSource ?? new DwmFrameMetricsSource();
        _processSource = processSource ?? new PresentMonFrameMetricsSource();
        CurrentValue = "Desktop output - FPS\nDisplay - Hz";
        Detail = "Desktop output uses DWM composition timing; it is not application render FPS.";
    }

    public override void Start()
    {
        if (_timer != null) return;

        _stopRequested = false;
        SwitchMode(_settings.Current.FrameMonitoringMode);
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTick;
        _timer.Start();
        Update();
    }

    private void OnTick(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var requestedMode = _settings.Current.FrameMonitoringMode;
        if (_activeMode != requestedMode)
        {
            SwitchMode(requestedMode);
        }

        var refreshRate = GetRefreshRate(_displayDeviceNameProvider());
        if (requestedMode == FrameMonitoringMode.ForegroundApplication)
        {
            UpdateForegroundApplication(refreshRate);
        }
        else
        {
            UpdateDesktop(refreshRate);
        }
    }

    private void UpdateDesktop(int? refreshRate)
    {
        _desktopSource.TrySample(out var metrics);
        CurrentValue = $"Desktop output {FormatNumber(metrics.CurrentFps)} FPS\n" +
                       $"Display {FormatRefreshRate(refreshRate)}";

        if (metrics.AverageFps is double average &&
            metrics.MinimumFps is double minimum &&
            metrics.MaximumFps is double maximum)
        {
            Detail = $"60s average {average:0} FPS - range {minimum:0}-{maximum:0} FPS\n" +
                     "DWM composition timing; not application render FPS.";
        }
        else
        {
            Detail = $"{metrics.Status ?? "Collecting desktop composition timing..."}\n" +
                     "DWM composition timing; not application render FPS.";
        }
    }

    private void UpdateForegroundApplication(int? refreshRate)
    {
        var foreground = _foregroundProcessProvider.GetCurrent();
        if (foreground is not null)
        {
            UpdateForegroundCandidate(foreground);
        }

        if (_targetProcess is null)
        {
            CurrentValue = $"Foreground app - FPS\nDisplay {FormatRefreshRate(refreshRate)}";
            Detail = "Focus an application to begin monitoring its frame presents.";
            return;
        }

        _processSource.TrySample(out var metrics);
        CurrentValue = $"{_targetProcess.ProcessName} {FormatNumber(metrics.CurrentFps)} FPS\n" +
                       $"Display {FormatRefreshRate(refreshRate)}";

        if (Volatile.Read(ref _activeProcessId) != _targetProcess.ProcessId)
        {
            Detail = metrics.Status ?? GetTransitionError() ?? "Switching application frame collector...";
            return;
        }

        if (metrics.AverageFps is double average &&
            metrics.OnePercentLowFps is double onePercentLow &&
            metrics.MinimumFps is double minimum &&
            metrics.MaximumFps is double maximum)
        {
            Detail = $"60s average {average:0} - 1% low {onePercentLow:0} FPS\n" +
                     $"Frame range {minimum:0}-{maximum:0} FPS";
        }
        else
        {
            Detail = metrics.Status ?? GetTransitionError() ?? "Collecting application frame presents...";
        }
    }

    private void SwitchMode(FrameMonitoringMode mode)
    {
        _candidateProcess = null;
        _candidateSamples = 0;
        _targetProcess = null;
        _activeMode = mode;

        ForegroundProcessInfo? foreground = null;
        if (mode == FrameMonitoringMode.ForegroundApplication)
        {
            foreground = _foregroundProcessProvider.GetCurrent();
            if (foreground is not null)
            {
                _candidateProcess = foreground;
                _candidateSamples = 2;
                _targetProcess = foreground;
            }
        }

        RequestTransition(mode, foreground?.ProcessId);
    }

    public override void Stop()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        _activeMode = null;
        _targetProcess = null;
        _candidateProcess = null;
        _candidateSamples = 0;
        _ = RequestStop();
    }

    public override async Task StopAsync()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        _activeMode = null;
        _targetProcess = null;
        _candidateProcess = null;
        _candidateSamples = 0;

        var transition = RequestStop();
        await transition.ConfigureAwait(false);
    }

    public override void Dispose()
    {
        // Dispose can still be called through the synchronous IDisposable path.
        // Wait for the serialized worker before releasing its metric sources.
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Disposal must remain best-effort even if a collector failed.
        }
        _desktopSource.Dispose();
        _processSource.Dispose();
    }

    private void UpdateForegroundCandidate(ForegroundProcessInfo foreground)
    {
        if (_candidateProcess?.ProcessId != foreground.ProcessId)
        {
            _candidateProcess = foreground;
            _candidateSamples = 1;
            return;
        }

        _candidateSamples++;
        if (_candidateSamples < 2 || _targetProcess?.ProcessId == foreground.ProcessId) return;

        _targetProcess = foreground;
        RequestTransition(FrameMonitoringMode.ForegroundApplication, foreground.ProcessId);
    }

    private Task RequestStop()
    {
        lock (_transitionSync)
        {
            _stopRequested = true;
            _requestedProcessId = null;
            _transitionVersion++;
            return EnsureTransitionWorkerLocked();
        }
    }

    private Task RequestTransition(FrameMonitoringMode mode, int? processId)
    {
        lock (_transitionSync)
        {
            _stopRequested = false;
            _requestedMode = mode;
            _requestedProcessId = processId;
            _transitionVersion++;
            return EnsureTransitionWorkerLocked();
        }
    }

    private Task EnsureTransitionWorkerLocked()
    {
        if (_transitionTask is null || _transitionTask.IsCompleted)
        {
            _transitionTask = Task.Run(RunTransitionWorker);
        }

        return _transitionTask;
    }

    private void RunTransitionWorker()
    {
        while (true)
        {
            FrameMonitoringMode mode;
            int? processId;
            bool stop;
            long version;
            lock (_transitionSync)
            {
                version = _transitionVersion;
                mode = _requestedMode;
                processId = _requestedProcessId;
                stop = _stopRequested;
            }

            try
            {
                Volatile.Write(ref _activeProcessId, 0);
                _desktopSource.Stop();
                _processSource.Stop();

                if (!stop)
                {
                    if (mode == FrameMonitoringMode.ForegroundApplication && processId is > 0)
                    {
                        _processSource.TargetProcessId = processId;
                        _processSource.Start();
                        if (_processSource.IsRunning)
                        {
                            Volatile.Write(ref _activeProcessId, processId.Value);
                        }
                    }
                    else if (mode == FrameMonitoringMode.DesktopOutput)
                    {
                        _desktopSource.Start();
                    }
                }

                lock (_transitionSync)
                {
                    _transitionError = null;
                    if (version == _transitionVersion) return;
                }
            }
            catch (Exception ex)
            {
                lock (_transitionSync)
                {
                    _transitionError = ex.Message;
                    if (version == _transitionVersion) return;
                }
            }
        }
    }

    private string? GetTransitionError()
    {
        lock (_transitionSync) return _transitionError;
    }

    private static string FormatNumber(double? value) =>
        value is >= 0 and < 10000 ? value.Value.ToString("0") : "-";

    private static string FormatRefreshRate(int? refreshRate) =>
        refreshRate is > 1 ? $"{refreshRate.Value} Hz" : "- Hz";

    private static int? GetRefreshRate(string? deviceName)
    {
        try
        {
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            return EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode) && mode.DisplayFrequency > 1
                ? (int)mode.DisplayFrequency
                : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(
        string? deviceName,
        int modeNumber,
        ref DevMode deviceMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TtOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }
}
