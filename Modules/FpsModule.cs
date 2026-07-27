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
    private System.Windows.Threading.DispatcherTimer? _timer;
    private FrameMonitoringMode? _activeMode;
    private ForegroundProcessInfo? _targetProcess;

    public override string Id => "fps";
    public override string DisplayName => "Frame Performance (Experimental)";
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
        if (foreground is not null && foreground.ProcessId != _targetProcess?.ProcessId)
        {
            _processSource.Stop();
            _targetProcess = foreground;
            _processSource.TargetProcessId = foreground.ProcessId;
            _processSource.Start();
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
            Detail = metrics.Status ?? "Collecting application frame presents...";
        }
    }

    private void SwitchMode(FrameMonitoringMode mode)
    {
        _desktopSource.Stop();
        _processSource.Stop();
        _targetProcess = null;
        _activeMode = mode;

        if (mode == FrameMonitoringMode.ForegroundApplication)
        {
            var foreground = _foregroundProcessProvider.GetCurrent();
            if (foreground is not null)
            {
                _targetProcess = foreground;
                _processSource.TargetProcessId = foreground.ProcessId;
                _processSource.Start();
            }
        }
        else
        {
            _desktopSource.Start();
        }
    }

    public override void Stop()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        _desktopSource.Stop();
        _processSource.Stop();
        _activeMode = null;
        _targetProcess = null;
    }

    public override void Dispose()
    {
        Stop();
        _desktopSource.Dispose();
        _processSource.Dispose();
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
