using System.Runtime.InteropServices;
using Lychee.Core;

namespace Lychee.Modules;

public sealed class AudioMeterModule : InfoModuleBase
{
    public override string Id => "audio-meter";
    public override string DisplayName => "Audio Meter";
    public override string Icon => "\uE767";

    private System.Windows.Threading.DispatcherTimer? _timer;
    private IAudioMeterInformation? _meter;
    private bool _initFailed;

    public AudioMeterModule()
    {
        CurrentValue = "Level  --%";
        Detail = "Waiting for audio...";
    }

    public override void Start()
    {
        if (_timer != null) return;

        TryInitialize();

        _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void TryInitialize()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device);
            if (hr != 0)
            {
                SetInitializationFailure($"GetDefaultAudioEndpoint failed: 0x{hr:X8}");
                return;
            }

            var iidMeter = typeof(IAudioMeterInformation).GUID;
            hr = device.Activate(ref iidMeter, ClsCtxInprocServer, IntPtr.Zero, out var meter);
            if (hr != 0)
            {
                SetInitializationFailure($"Activate(IAudioMeterInformation) failed: 0x{hr:X8}");
                return;
            }

            ReleaseMeter();
            _meter = meter;
            _initFailed = false;
        }
        catch (COMException ex)
        {
            SetInitializationFailure($"Audio endpoint initialization failed: 0x{ex.HResult:X8}");
        }
        finally
        {
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_meter == null)
        {
            if (!_initFailed) TryInitialize();
            if (_meter == null)
            {
                CurrentValue = "Level  --%";
                Progress = null;
                return;
            }
        }

        try
        {
            var hr = _meter.GetPeakValue(out var peak);
            if (hr != 0)
            {
                ReleaseMeter();
                _initFailed = false;
                CurrentValue = "Level  --%";
                Progress = null;
                Detail = $"GetPeakValue failed: 0x{hr:X8}";
                return;
            }

            peak = Math.Clamp(peak, 0f, 1f);

            var decibels = 20 * Math.Log10(Math.Max(peak, FloorThreshold));
            var logLevel = Math.Clamp((decibels - DbFloor) / -DbFloor, 0.0, 1.0);
            var meterLevel = Math.Pow(logLevel, ProgressExponent);
            var percent = (int)Math.Round(peak * 100);
            CurrentValue = $"Level  {percent}%";
            Progress = meterLevel;
            Detail = peak > FloorThreshold
                ? $"{decibels:0} dBFS | Active"
                : "Silent";
        }
        catch (COMException ex)
        {
            ReleaseMeter();
            _initFailed = false;
            CurrentValue = "Level  --%";
            Progress = null;
            Detail = $"GetPeakValue failed: 0x{ex.HResult:X8}";
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

        ReleaseMeter();
        Progress = null;
    }

    private void SetInitializationFailure(string detail)
    {
        ReleaseMeter();
        _initFailed = true;
        CurrentValue = "Level  --%";
        Detail = detail;
        Progress = null;
    }

    private void ReleaseMeter()
    {
        var meter = _meter;
        _meter = null;
        ReleaseComObject(meter);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private const uint ClsCtxInprocServer = 0x1;
    private const float FloorThreshold = 0.001f;
    private const double DbFloor = -40.0;
    private const double ProgressExponent = 2.5;

    private enum EDataFlow
    {
        Render = 0
    }

    private enum ERole
    {
        Console = 0
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IAudioMeterInformation endpoint);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);
    }
}
