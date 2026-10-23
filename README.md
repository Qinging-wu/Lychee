# <img src="Assets/icon-thumb-64.png" alt="Lychee" width="56" height="56" style="vertical-align: middle; margin-top: 8px"> Lychee

English | [简体中文](./README.zh-CN.md)

A tiny always-on-top floating ball for Windows that shows CPU, memory, network speed, public IP, and latency at a glance. Hover to expand the info panel; move away to collapse. Built with .NET 8 WPF + WinForms. No installer, no background services.

Good for anyone who wants system stats visible without alt-tabbing to Task Manager — remote workers keeping an eye on VPN status, developers running long builds, or anyone who finds full system monitors too heavy.

## 🍎 macOS version

Lychee also has a separate macOS sibling: [Lychee4MacOS](https://github.com/Qinging-wu/Lychee4MacOS). It keeps the same floating-ball concept and core monitoring modules, using .NET 8 + Avalonia UI for macOS. The macOS port is independently versioned and currently runs from source or a self-contained command-line publish; it does not yet provide a `.app`, `.dmg`, or `.pkg` installer.

## 🚀 Quick start

Download `Lychee.exe` from [Releases](https://github.com/Qinging-wu/Lychee/releases), double-click to run. That's it — no installer, no admin rights needed. If the browser warns about the EXE, try the `Lychee-v*.zip` instead — it usually works.

Or install with a one-liner (Windows 10+, installs to `%LOCALAPPDATA%\Lychee` and creates a Start Menu shortcut).

**PowerShell:**

```powershell
irm https://raw.githubusercontent.com/Qinging-wu/Lychee/main/install.ps1 | iex
```

**PowerShell with curl:**

```powershell
curl.exe -fsSL https://raw.githubusercontent.com/Qinging-wu/Lychee/main/install.ps1 -o "$env:TEMP\lychee-install.ps1"; if ($?) { powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\lychee-install.ps1" }
```

**cmd:**

```bat
curl -fsSL https://raw.githubusercontent.com/Qinging-wu/Lychee/main/install.ps1 -o "%TEMP%\lychee-install.ps1" && powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\lychee-install.ps1"
```

To close, click **✕** on the panel or right-click the tray icon → Quit.

## ✨ Features

- **📅 Date & time** — refreshes every second
- **🌐 Network speed** — auto-selects active adapter, samples up/down rate every second
- **📍 Public IP / location** — queries ip-api.com every 20s, shows a toast + tray balloon when the IP changes (optional)
- **💻 CPU usage** — per-core utilization via `GetSystemTimes`, refreshes every second
- **🧠 Memory** — RAM and page file stats via `GlobalMemoryStatusEx`
- **⏱️ Network latency** — pings 223.5.5.5 / 1.1.1.1 / 8.8.8.8 every 5s, reports the best RTT
- **🔊 Audio meter** — shows output level percentage and dBFS; while the panel is open, renders a 16-band live spectrum with peak holds and particles
- **🖼️ Frame Performance** (v1.2.0) — two modes:
  - *Desktop output* — samples DWM composition cadence via `DwmGetCompositionTimingInfo`, shows FPS + display refresh rate
  - *Foreground app* — launches PresentMon 2.5.1 to capture per-process frame presents, reports current FPS, 60s average, 1% low, and frame range. Requires **administrator privileges** or membership in `Performance Log Users` group

Each module can be toggled on/off individually in Settings.

### Floating ball and panel

- Choose a built-in floating ball style or import a PNG, JPEG, BMP, or animated GIF.
- Resize the expanded panel by dragging an outer edge or corner. The floating ball stays in place, and a manually selected panel height is retained when the panel is reopened.
- In the compact, collapsible **Custom panel appearance** section in Settings, adjust background opacity, background and text RGB colors, and font. Each color has its own precise picker with spectrum, RGB, and Hex controls; changes preview live, and **Reset default** restores the original appearance.
- Toggling modules, including Audio meter, preserves the floating ball's screen position and keeps it within the monitor's working area.

### Frame Performance modes

Frame Performance has two modes:

- **Desktop output** — samples the Windows DWM composition cadence. It does not require PresentMon.
- **Foreground app** — uses the bundled `PresentMon_x64.exe` to capture frame presents from the selected application. Keep `Lychee.exe` and `PresentMon_x64.exe` in the same directory when using a release build.

Foreground app mode requires administrator privileges or membership in the Windows `Performance Log Users` group. If the bundled executable is not found, Lychee can only use a compatible PresentMon installation found in the NVIDIA FrameView or Intel PresentMon directories; otherwise the mode reports that PresentMon was not found. The bundled executable is excluded from single-file embedding and must remain a separate file.

### What's new

| Version | Change | Detail |
|---|---|---|
| v2.0.0 | 🔊 Audio spectrum and custom appearance | Adds a 16-band live spectrum, floating ball styles, a resizable panel, and RGB/font customization; fixes ball movement during module toggles and related interactions |
| v1.4.0 | ??? | Nothing happened (?) |
| v1.3.0 | 🧲 Snap to edge | Floating ball snaps to the nearest screen edge after dragging (toggle in Settings) |
| v1.2.0 | 🖼️ Frame Performance | No longer experimental |

Full history: [CHANGELOG](./CHANGELOG.md)

## 🖱️ Usage

- **👆 Hover** the ball to expand the panel; move away to collapse
- **✋ Drag** the ball anywhere across monitors — the panel flips direction based on screen position
- **👆👆 Double-click** the ball to pin/unpin the panel
- **📌 Pin** — keep the panel open even when the mouse leaves
- **⚙️ Settings** — open the settings window
- **◀️ Collapse** — force-close the panel
- **❌ Quit** — exit Lychee
- **📋 Tray icon** — right-click for Show/Hide, Settings, Quit; double-click to toggle visibility

When the public IP changes (possible VPN drop or network switch), a red toast pops up in the bottom-right corner with a tray balloon showing old and new IPs.

## ⭐ Star History

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=Qinging-wu/Lychee&type=Date&theme=dark" />
  <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=Qinging-wu/Lychee&type=Date" />
  <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=Qinging-wu/Lychee&type=Date" />
</picture>

## 🔧 Build

Requires .NET SDK 8.0+ with WPF and WinForms workloads.

```powershell
dotnet build -c Release -r win-x64
dotnet publish -c Release -r win-x64
```

The project targets .NET 8 and pins `NAudio.Wasapi` 2.2.1 for WASAPI loopback capture.

Spectrum checks:

```powershell
dotnet run --project tests/Lychee.AudioSpectrum.SmokeTests -c Release
dotnet run --project tests/Lychee.AudioSpectrum.VisualChecks -c Debug
dotnet run --project tests/Lychee.AudioCapture.IntegrationChecks -c Debug -- 15
dotnet run --project tests/Lychee.AudioSpectrum.VisualChecks -c Release -- --live 15 docs/audio-spectrum-live.png
dotnet run --project tests/Lychee.AudioSpectrum.ProcessResourceProbe -c Release -- 1800
```

The first command tests known tones, float32/float64, and PCM formats; the visual check renders a deterministic preview, verifies stale-frame decay, and checks 125%/150% output DPI. The live-render option captures a fresh 16-band Loopback frame and renders the production control to PNG (no raw audio is saved); keep music playing. The 30-minute process probe prints the selected executable path and start time (verify it is the Lychee-Lite build); pass a PID if more than one Lychee process is running, then press E/C to label expanded/collapsed samples.

Output: `bin\Release\net8.0-windows\win-x64\publish\Lychee.exe` (self-contained, double-click to run).

## 🏗️ Architecture

```
Lychee/
├── Core/                       # Core abstractions
│   ├── IInfoModule.cs          # Module interface + InfoModuleBase
│   ├── ModuleManager.cs        # Module registration / lifecycle
│   ├── SettingsService.cs      # JSON settings persistence
│   ├── TrayIconService.cs      # System tray icon
│   └── IpChangedEventArgs.cs   # IP change event
├── Modules/                    # Built-in modules
│   ├── DateTimeModule.cs
│   ├── NetworkSpeedModule.cs
│   ├── PublicIpModule.cs
│   ├── CpuModule.cs
│   ├── MemoryModule.cs
│   ├── LatencyModule.cs
│   └── FpsModule.cs            # v1.2.0 · Frame Performance
├── Controls/
│   └── AudioSpectrumView.cs    # 16-band WPF spectrum, peak holds, and particles
├── Core/
│   ├── FloatingBallStyles.cs   # Built-in and imported floating ball styles
│   ├── SpectrumFrame.cs         # Immutable 16-band snapshot
│   ├── SpectrumAnalyzer.cs      # PCM decode, Hann-window FFT, logarithmic bands
│   ├── WasapiAudioCaptureSource.cs # Bounded loopback capture and device recovery
│   ├── FrameMetrics.cs         # Frame monitoring types & interfaces
│   ├── DwmFrameMetricsSource.cs
│   ├── PresentMonFrameMetricsSource.cs
│   ├── ForegroundProcessProvider.cs
│   └── ...
├── ColorPickerWindow.xaml      # Spectrum, RGB, and Hex color picker
├── SettingsWindow.xaml         # Module, style, and appearance settings
```

### 🔌 Module interface

```csharp
public interface IInfoModule : IDisposable
{
    string Id { get; }              // unique id
    string DisplayName { get; }     // list label
    string Icon { get; }            // Segoe MDL2 Assets glyph
    bool IsEnabled { get; set; }    // user toggle
    string CurrentValue { get; }    // current value (data-bound)
    string? Detail { get; }         // optional secondary line
    event EventHandler? ValueChanged;
    void Start();
    void Stop();
}
```

### 📦 Adding a module

1. Inherit `InfoModuleBase`, implement `Start()` / `Stop()`, and set `CurrentValue` when data updates:

```csharp
using Lychee.Core;

public sealed class WeatherModule : InfoModuleBase
{
    public override string Id => "weather";
    public override string DisplayName => "Weather";
    public override string Icon => "";

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    public override void Start()
    {
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        _ = Task.Run(async () =>
        {
            await QueryAsync(_cts.Token);
            while (await _timer.WaitForNextTickAsync(_cts.Token))
                await QueryAsync(_cts.Token);
        });
    }

    private async Task QueryAsync(CancellationToken ct)
    {
        // ... fetch data ...
        CurrentValue = "Shanghai 26°C Sunny";
    }

    public override void Stop()
    {
        _cts?.Cancel();
        _timer?.Dispose();
    }
}
```

2. Register in `MainWindow.xaml.cs`:

```csharp
_moduleManager.RegisterModule(new WeatherModule());
```

3. Rebuild. The module appears in the info panel and settings toggle list automatically — no UI changes needed.

## ⚙️ Settings

`%AppData%\Lychee\settings.json`

```json
{
  "AlwaysShowPanel": false,
  "AlertOnIpChange": true,
  "AlertOnQueryFailure": true,
  "ShowTrayIcon": true,
  "SnapToEdge": false,
  "FloatingBallSize": 56,
  "ModuleEnabled": {
    "datetime": true,
    "network-speed": true,
    "public-ip": true,
    "cpu": true,
    "memory": true,
    "latency": true,
    "fps": true
  }
}
```

## ⚠️ Known limitations

- Some exclusive fullscreen games may still cover the ball despite topmost
- Public IP may show as IP-only (no city/country) when the geo lookup returns empty
- Some antivirus software may flag Lychee.exe as a false positive due to P/Invoke, COM interop, and the bundled PresentMon tool. The source code is fully open — you can inspect and build it yourself, or add an exclusion for Lychee if needed

## 🤝 Community

- [Contributing guide](./CONTRIBUTING.md)
- [Code of Conduct](./CODE_OF_CONDUCT.md)
- [Security policy](./SECURITY.md)

## 📄 License

Lychee is licensed under the [MIT License](./LICENSE).

The bundled PresentMon component is distributed under its own license. See [PresentMon-LICENSE.txt](./publish/PresentMon-LICENSE.txt).
