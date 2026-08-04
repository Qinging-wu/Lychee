# <img src="Assets/icon-thumb-64.png" alt="Lychee" width="56" height="56" align="center"> Lychee

English | [简体中文](./README.zh-CN.md)

A tiny always-on-top floating ball for Windows that shows CPU, memory, network speed, public IP, and latency at a glance. Hover to expand the info panel; move away to collapse. Built with .NET 8 WPF + WinForms. No installer, no admin rights, no background services.

![screenshot](Assets/screenshot.png)

Good for anyone who wants system stats visible without alt-tabbing to Task Manager — remote workers keeping an eye on VPN status, developers running long builds, or anyone who finds full system monitors too heavy.

## 🚀 Quick start

Download `Lychee.exe` from [Releases](https://github.com/Qinging-wu/Lychee/releases), double-click to run. That's it — no installer, no admin rights needed. If the browser warns about the EXE, try the `Lychee-v*.zip` instead — it usually works.

To close, click **✕** on the panel or right-click the tray icon → Quit.

## ✨ Features

- **📅 Date & time** — refreshes every second
- **🌐 Network speed** — auto-selects active adapter, samples up/down rate every second
- **📍 Public IP / location** — queries ip-api.com every 20s, shows a toast + tray balloon when the IP changes (optional)
- **💻 CPU usage** — per-core utilization via `GetSystemTimes`, refreshes every second
- **🧠 Memory** — RAM and page file stats via `GlobalMemoryStatusEx`
- **⏱️ Network latency** — pings 223.5.5.5 / 1.1.1.1 / 8.8.8.8 every 5s, reports the best RTT
- **🖼️ Frame Performance** (v1.2.0) — two modes:
  - *Desktop output* — samples DWM composition cadence via `DwmGetCompositionTimingInfo`, shows FPS + display refresh rate
  - *Foreground app* — launches PresentMon 2.5.1 to capture per-process frame presents, reports current FPS, 60s average, 1% low, and frame range. Requires **administrator privileges** or membership in `Performance Log Users` group

Each module can be toggled on/off individually in Settings.

### What's new

| Version | Change | Detail |
|---|---|---|
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

## 🔧 Build

Requires .NET SDK 8.0+ with WPF and WinForms workloads.

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64
```

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
├── Core/
│   ├── FrameMetrics.cs         # Frame monitoring types & interfaces
│   ├── DwmFrameMetricsSource.cs
│   ├── PresentMonFrameMetricsSource.cs
│   ├── ForegroundProcessProvider.cs
│   └── ...
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
  "ShowTrayIcon": true,
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

## 📄 License

MIT
