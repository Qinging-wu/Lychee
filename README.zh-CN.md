# <img src="Assets/icon-thumb-64.png" alt="Lychee" width="56" height="56" style="vertical-align: middle; margin-top: 8px"> Lychee

[English](./README.md) | 简体中文

一个极小的 Windows 桌面悬浮球，置顶显示 CPU、内存、网速、公网 IP 和延迟。鼠标悬停展开信息面板，移开自动收起。基于 .NET 8 WPF + WinForms，无需安装、无后台服务。

适合不想 alt-tab 切任务管理器看系统状态的用户——远程办公盯 VPN 状态、开发者跑长任务、或者单纯觉得完整系统监视器太重的人。

## 🍎 macOS 版本

Lychee 另有独立的 macOS 姊妹仓库：[Lychee4MacOS](https://github.com/Qinging-wu/Lychee4MacOS)。它保留了悬浮球和主要系统监控模块，使用 .NET 8 + Avalonia UI 适配 macOS。macOS 版本独立维护和发布，目前支持从源码运行或通过命令行发布自包含可执行文件，暂未提供 `.app`、`.dmg` 或 `.pkg` 安装包。

## 🚀 快速开始

从 [Releases](https://github.com/Qinging-wu/Lychee/releases) 下载 `Lychee.exe`，双击运行。无需安装，无需管理员权限。如果浏览器拦截 EXE 下载，可以尝试 `Lychee-v*.zip`，通常可以正常下载。

也可以用一行命令安装（Windows 10+，安装到 `%LOCALAPPDATA%\Lychee` 并创建开始菜单快捷方式）。

**PowerShell：**

```powershell
irm https://raw.githubusercontent.com/Qinging-wu/Lychee/main/install.ps1 | iex
```

**PowerShell + curl：**

```powershell
curl.exe -fsSL https://raw.githubusercontent.com/Qinging-wu/Lychee/main/install.ps1 -o "$env:TEMP\lychee-install.ps1"; if ($?) { powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\lychee-install.ps1" }
```

**cmd：**

```bat
curl -fsSL https://raw.githubusercontent.com/Qinging-wu/Lychee/main/install.ps1 -o "%TEMP%\lychee-install.ps1" && powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\lychee-install.ps1"
```

关闭方式：点击面板上的 **✕**，或右键托盘图标 → 退出。

## ✨ 功能

- **📅 日期时间** — 每秒刷新
- **🌐 网络速度** — 自动选择活动网卡，每秒采样上传/下载速率
- **📍 公网 IP / 归属地** — 每 20 秒查询 ip-api.com，IP 变化时弹出提醒 + 托盘气泡（可选）
- **💻 CPU 使用率** — 通过 `GetSystemTimes` 采集全核利用率，每秒刷新
- **🧠 内存** — 通过 `GlobalMemoryStatusEx` 获取 RAM 占用和页面文件统计
- **⏱️ 网络延迟** — 每 5 秒 ping 223.5.5.5 / 1.1.1.1 / 8.8.8.8，取最优值
- **🔊 音频频谱** — 显示默认输出设备总体电平、百分比和 dBFS；面板展开时绘制 16 段实时频谱、峰值标记与粒子
- **🖼️ 帧性能**（v1.2.0）— 两种模式：
  - *桌面输出* — 通过 `DwmGetCompositionTimingInfo` 采样 DWM 合成帧率，显示 FPS + 显示刷新率
  - *前台应用* — 启动 PresentMon 2.5.1 采集进程级帧呈现，报告当前 FPS、60 秒平均、1% Low 和帧范围。需要**管理员权限**或加入 `Performance Log Users` 组

每个模块都可以在设置中单独开关。

### 悬浮球与面板

- 可选择内置悬浮球样式，或导入 PNG、JPEG、BMP 图片及动态 GIF。
- 展开面板后可拖动外侧边缘或角落缩放。缩放时悬浮球保持原位，手动调整的面板高度也会在重新展开后保留。
- 设置中的“自定义面板外观”区域可折叠且保持紧凑，可调整背景透明度、背景和文字 RGB 颜色及字体。每种颜色都有独立的精确选择器，支持色谱、RGB 和 Hex 输入；修改会实时预览，“恢复默认”可还原原始外观。
- 切换功能模块（包括 Audio meter）时，悬浮球会保持屏幕位置，并留在所在显示器的工作区内。

### 帧性能模式

帧性能功能包含两种模式：

- **桌面输出** — 通过 Windows DWM 合成节奏采样，不需要 PresentMon。
- **前台应用** — 使用内置的 `PresentMon_x64.exe` 采集所选应用的帧呈现数据。使用发布版时，请将 `Lychee.exe` 和 `PresentMon_x64.exe` 放在同一目录下。

前台应用模式需要管理员权限，或将当前用户加入 Windows 的 `Performance Log Users` 组。如果找不到内置 EXE，Lychee 还会尝试使用 NVIDIA FrameView 或 Intel PresentMon 目录中的兼容版本；如果仍未找到，前台应用模式会提示 PresentMon 不存在。该 EXE 不会嵌入单文件程序，必须作为独立文件保留。

### 更新内容

| 版本 | 变更 | 说明 |
|---|---|---|
| v2.0.0 | 🔊 音频频谱与自定义外观 | 新增 16 段实时频谱、悬浮球样式、可缩放面板和 RGB/字体自定义；修复模块切换时球体移位及相关交互问题 |
| v1.4.0 | ??? | 什么都没有发生 (?) |
| v1.3.0 | 🧲 吸附边缘 | 拖拽结束后悬浮球自动吸附到最近的屏幕边缘（可在设置中开关） |
| v1.2.0 | 🖼️ 帧性能 | 不再是实验性功能 |

完整历史见：[CHANGELOG](./CHANGELOG.zh-CN.md)

## 🖱️ 使用

- **👆 悬停**悬浮球展开面板，移开自动收起
- **✋ 拖拽**悬浮球到任意显示器任意位置，面板根据屏幕位置自动翻转方向
- **👆👆 双击**悬浮球固定/取消固定面板
- **📌 固定** — 面板保持展开，不再跟随鼠标自动收起
- **⚙️ 设置** — 打开设置窗口
- **◀️ 收起** — 强制收起面板
- **❌ 退出** — 退出应用
- **📋 托盘图标** — 右键弹出菜单（显示/隐藏、设置、退出）；双击切换可见性

公网 IP 发生变化时（可能是 VPN 掉线或网络切换），右下角弹出红色提醒并显示托盘气泡，标明新旧 IP。

## ⭐ Star History

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=Qinging-wu/Lychee&type=Date&theme=dark" />
  <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=Qinging-wu/Lychee&type=Date" />
  <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=Qinging-wu/Lychee&type=Date" />
</picture>

## 🔧 构建

需要 .NET SDK 8.0+（包含 WPF + WinForms 工作负载）。

```powershell
dotnet build -c Release -r win-x64
dotnet publish -c Release -r win-x64
```

项目保持 .NET 8，并固定使用 `NAudio.Wasapi` 2.2.1 采集 WASAPI Loopback 音频。

频谱检查：

```powershell
dotnet run --project tests/Lychee.AudioSpectrum.SmokeTests -c Release
dotnet run --project tests/Lychee.AudioSpectrum.VisualChecks -c Debug
dotnet run --project tests/Lychee.AudioCapture.IntegrationChecks -c Debug -- 15
dotnet run --project tests/Lychee.AudioSpectrum.VisualChecks -c Release -- --live 15 docs/audio-spectrum-live.png
dotnet run --project tests/Lychee.AudioSpectrum.ProcessResourceProbe -c Release -- 1800
```

第一条使用已知频率以及 float32/float64、PCM 格式验证分析器；可视化检查会离屏绘制确定性预览、验证旧频谱帧衰减，并检查 125%/150% DPI。实时渲染模式会被动捕获一帧 16 段频谱并将正式控件渲染为 PNG（不保存原始音频）；测试期间请持续播放音乐。30 分钟进程探针会打印目标可执行文件路径和启动时间，请确认它是 Lychee-Lite；若有多个 Lychee 进程可传入 PID。面板展开时按 E，收起时按 C，为采样分组。

产物路径：`bin\Release\net8.0-windows\win-x64\publish\Lychee.exe`（自包含，双击运行）。

## 🏗️ 架构

```
Lychee/
├── Core/                       # 核心抽象
│   ├── IInfoModule.cs          # 模块接口 + InfoModuleBase
│   ├── ModuleManager.cs        # 模块注册 / 生命周期
│   ├── SettingsService.cs      # JSON 设置持久化
│   ├── TrayIconService.cs      # 系统托盘图标
│   └── IpChangedEventArgs.cs   # IP 变化事件
├── Modules/                    # 内置模块
│   ├── DateTimeModule.cs
│   ├── NetworkSpeedModule.cs
│   ├── PublicIpModule.cs
│   ├── CpuModule.cs
│   ├── MemoryModule.cs
│   ├── LatencyModule.cs
│   └── FpsModule.cs            # v1.2.0 · 帧性能
├── Controls/
│   └── AudioSpectrumView.cs    # 16 段 WPF 频谱、峰值保持与粒子
├── Core/
│   ├── FloatingBallStyles.cs   # 内置与导入的悬浮球样式
│   ├── SpectrumFrame.cs           # 不可变的 16 段频谱快照
│   ├── SpectrumAnalyzer.cs        # PCM 解码、Hann 窗 FFT 与对数频段
│   ├── WasapiAudioCaptureSource.cs # 有界 Loopback 采集与设备恢复
│   ├── FrameMetrics.cs         # 帧监控类型与接口
│   ├── DwmFrameMetricsSource.cs
│   ├── PresentMonFrameMetricsSource.cs
│   ├── ForegroundProcessProvider.cs
│   └── ...
├── ColorPickerWindow.xaml      # 色谱、RGB 与 Hex 精确取色器
├── SettingsWindow.xaml         # 模块、样式和外观设置
```

### 🔌 模块接口

```csharp
public interface IInfoModule : IDisposable
{
    string Id { get; }              // 唯一标识
    string DisplayName { get; }     // 列表标签
    string Icon { get; }            // Segoe MDL2 Assets 字形
    bool IsEnabled { get; set; }    // 用户开关
    string CurrentValue { get; }    // 当前值（数据绑定）
    string? Detail { get; }         // 可选的副行
    event EventHandler? ValueChanged;
    void Start();
    void Stop();
}
```

### 📦 新增模块

1. 继承 `InfoModuleBase`，实现 `Start()` / `Stop()`，数据更新时给 `CurrentValue` 赋值即可：

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
        // ... 拉取数据 ...
        CurrentValue = "上海 26°C 晴";
    }

    public override void Stop()
    {
        _cts?.Cancel();
        _timer?.Dispose();
    }
}
```

2. 在 `MainWindow.xaml.cs` 中注册：

```csharp
_moduleManager.RegisterModule(new WeatherModule());
```

3. 重新编译。模块会自动出现在信息面板和设置开关列表中，无需改动 UI 代码。

## ⚙️ 配置文件

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

## ⚠️ 已知限制

- 部分独占全屏游戏可能仍会盖住悬浮球
- 归属地查询不可用时，公网 IP 可能仅显示数字本身（无城市/国家信息）
- 部分杀毒软件可能将 Lychee.exe 误判为病毒，原因是使用了 P/Invoke、COM 互操作以及内置的 PresentMon 工具。源代码完全开源，可以自行审查和编译，或在杀软中添加排除项

## 🤝 社区与贡献

- [贡献指南](./CONTRIBUTING.md)
- [行为准则](./CODE_OF_CONDUCT.md)
- [安全策略](./SECURITY.md)

## 📄 许可证

Lychee 使用 [MIT 许可证](./LICENSE) 发布。

内置的 PresentMon 组件使用其自身的许可证发布，详见 [PresentMon-LICENSE.txt](./publish/PresentMon-LICENSE.txt)。
