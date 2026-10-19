using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lychee.Core;
using Lychee.Modules;

namespace Lychee;

public partial class MainWindow : Window
{
    private const double BallSize = 48;
    private const double BallMargin = 4;
    private const double BallSlot = 56;          // total window size occupied by the ball (incl. margins)
    private const double PanelWidth = 260;
    private const double PanelMargin = 4;
    private const double BallHoverMargin = 8;    // proximity zone around the ball that counts as hover
    private const double PanelHoverMargin = 4;   // tolerance around the panel that keeps it open

    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int ResizeBorder = 8;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private readonly SettingsService _settings;
    private readonly ModuleManager _moduleManager;
    private readonly TrayIconService _tray;
    private readonly DispatcherTimer _hoverTimer;
    private readonly bool _audioSpectrumUiTest;
    private readonly bool _audioSpectrumUiTestCollapsed;
    private DispatcherTimer? _topmostTimer;
    private bool _isExpanded = false;
    private bool _panelCollapsing = false;
    private bool _isHoveringBall = false;
    private SettingsWindow? _settingsWindow;
    private Window? _alertToast;
    private bool _shutdownStarted;

    private bool _isRightSide = true;
    private bool _isBottomSide = true;
    private bool _isDragging;
    private bool _isMouseDown;
    private bool _isUserSizing;
    private bool _isUserSizingHeight;
    private bool _hasUserResizedHeight;
    private bool _dragStarted;
    private bool _restorePinnedAfterDrag;
    private Point _dragStartScreen;
    private double _dragStartBallScreenX;
    private double _dragStartBallScreenY;
    private const double DragThreshold = 5;

    private bool _bouncyActive;
    private DispatcherTimer? _bouncyTimer;
    private DispatcherTimer? _bouncyTimeoutTimer;
    private DispatcherTimer? _ballGifTimer;
    private IReadOnlyList<BitmapFrame>? _ballGifFrames;
    private IReadOnlyList<TimeSpan>? _ballGifFrameDelays;
    private int _ballGifFrameIndex;
    private DateTime _bouncyLastTick;
    private Vector _bouncyVelocity;
    private double _bouncyAngularVelocity;
    private DateTime _bouncyLastImpulse;
    private static readonly TimeSpan BouncyImpulseCooldown = TimeSpan.FromMilliseconds(220);
    private const double BouncyMinSpeed = 90;
    private const double BouncyMaxSpeed = 1400;
    private const double BouncyImpulse = 380;
    private const double BouncyRestitution = 0.86;
    private const double BouncyFrictionPerSec = 0.18;
    private const double BouncyAngularFrictionPerSec = 0.55;
    private static readonly TimeSpan BouncyDuration = TimeSpan.FromMinutes(1);
    private readonly Random _bouncyRng = new();

    public MainWindow()
    {
        var commandLineArgs = Environment.GetCommandLineArgs();
        _audioSpectrumUiTestCollapsed = commandLineArgs.Any(
            arg => string.Equals(arg, "--audio-spectrum-ui-test-collapsed", StringComparison.OrdinalIgnoreCase));
        _audioSpectrumUiTest = _audioSpectrumUiTestCollapsed || commandLineArgs.Any(
            arg => string.Equals(arg, "--audio-spectrum-ui-test", StringComparison.OrdinalIgnoreCase));
        InitializeComponent();
        if (_audioSpectrumUiTest)
        {
            ShowInTaskbar = true;
            ShowActivated = true;
        }

        _settings = new SettingsService();
        _moduleManager = new ModuleManager(_settings);
        _moduleManager.RegisterModule(new DateTimeModule());
        _moduleManager.RegisterModule(new NetworkSpeedModule());
        _moduleManager.RegisterModule(new PublicIpModule());
        _moduleManager.RegisterModule(new CpuModule());
        _moduleManager.RegisterModule(new MemoryModule());
        _moduleManager.RegisterModule(new LatencyModule());
        _moduleManager.RegisterModule(new FpsModule(_settings, GetActiveDisplayDeviceName));
        _moduleManager.RegisterModule(new AudioMeterModule());
        if (_audioSpectrumUiTest)
        {
            // Keep this view isolated without changing the user's saved module preferences.
            foreach (var module in _moduleManager.Modules)
                module.IsEnabled = module.Id == "audio-meter";
        }

        if (_moduleManager.Get("public-ip") is PublicIpModule ipModule)
        {
            ipModule.IpChanged += OnIpChanged;
            ipModule.QueryFailed += OnIpQueryFailed;
            ipModule.QueryRecovered += OnIpQueryRecovered;
        }

        _moduleManager.ValueChanged += (s, e) => Dispatcher.Invoke(RefreshModuleList);
        _settings.Changed += (s, e) => Dispatcher.Invoke(ApplySettings);

        _tray = new TrayIconService();
        _tray.ShowHideRequested += (s, e) => Dispatcher.Invoke(ToggleVisibility);
        _tray.SettingsRequested += (s, e) => Dispatcher.Invoke(OpenSettings);
        _tray.ExitRequested += (s, e) => Dispatcher.Invoke(ShutdownApp);

        Width = BallSlot;
        Height = BallSlot;

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _hoverTimer.Tick += HoverTimer_Tick;
        _hoverTimer.Start();

        RefreshModuleList();
        ApplyBallStyle();
    }

    public void StartModules() => _moduleManager.StartAll();

    private void UpdateAudioSpectrumActivity()
    {
        if (_moduleManager.Get("audio-meter") is AudioMeterModule audioMeter)
            audioMeter.SetSpectrumActive(Visibility == Visibility.Visible && PanelBorder.Visibility == Visibility.Visible);
    }

    private void RefreshModuleList()
    {
        UpdateAudioSpectrumActivity();
        var visibleModules = _moduleManager.Modules.Where(m => m.IsEnabled).ToList();
        var current = ModuleList.ItemsSource as List<IInfoModule>;
        if (current != null &&
            current.Count == visibleModules.Count &&
            !current.Except(visibleModules).Any())
        {
            return;
        }
        ModuleList.ItemsSource = visibleModules;
        if (_isExpanded)
            ResizeExpandedPanelToCurrentModules();
    }

    private double CalcExpandedHeight(int moduleCount)
    {
        var approxItemHeight = 70;
        var headerHeight = 50;
        var padding = 16;
        var spectrumHeight = _moduleManager.Get("audio-meter") is { IsEnabled: true } ? 80 : 0;
        var target = headerHeight + padding + moduleCount * approxItemHeight + spectrumHeight;
        if (target > 540) target = 540;
        return target;
    }

    private void ApplySettings()
    {
        var s = _settings.Current;
        ApplyBallStyle();
        ApplyPanelAppearance(s);

        if (s.BouncyBall)
        {
            if (!_bouncyActive) StartBouncyBallMode();
        }
        else
        {
            if (_bouncyActive) StopBouncyBallMode(snapAfter: true);
        }

        if (_bouncyActive) return;

        var previewingCustomAppearance = _settingsWindow is { IsVisible: true } && s.UseCustomPanelAppearance;
        if (s.AlwaysShowPanel || previewingCustomAppearance)
        {
            ExpandPanel(immediate: true);
        }
        else if (!_isHoveringBall)
        {
            CollapsePanel(immediate: true);
        }

        if (s.SnapToEdge)
        {
            SnapToNearestEdge();
        }
    }

    private void ApplyPanelAppearance(AppSettings settings)
    {
        if (!settings.UseCustomPanelAppearance)
        {
            PanelBorder.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x2E));
            Resources["ModuleNameBrush"] = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            Resources["ModuleValueBrush"] = Brushes.White;
            Resources["ModuleDetailBrush"] = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x9F));
            Resources["ModuleFontFamily"] = System.Windows.SystemFonts.MessageFontFamily;
            return;
        }

        var alpha = (byte)Math.Round(Math.Clamp(settings.PanelBackgroundOpacity, 0, 100) * 2.55);
        var background = Color.FromArgb(
            alpha,
            (byte)Math.Clamp(settings.PanelBackgroundRed, 0, 255),
            (byte)Math.Clamp(settings.PanelBackgroundGreen, 0, 255),
            (byte)Math.Clamp(settings.PanelBackgroundBlue, 0, 255));
        var text = Color.FromRgb(
            (byte)Math.Clamp(settings.PanelTextRed, 0, 255),
            (byte)Math.Clamp(settings.PanelTextGreen, 0, 255),
            (byte)Math.Clamp(settings.PanelTextBlue, 0, 255));

        PanelBorder.Background = new SolidColorBrush(background);
        Resources["ModuleNameBrush"] = new SolidColorBrush(text);
        Resources["ModuleValueBrush"] = new SolidColorBrush(text);
        Resources["ModuleDetailBrush"] = new SolidColorBrush(text);
        try
        {
            Resources["ModuleFontFamily"] = new System.Windows.Media.FontFamily(
                string.IsNullOrWhiteSpace(settings.PanelFontFamily) ? "Segoe UI" : settings.PanelFontFamily);
        }
        catch (ArgumentException)
        {
            Resources["ModuleFontFamily"] = System.Windows.SystemFonts.MessageFontFamily;
        }
    }

    private void ApplyBallStyle()
    {
        _ballGifTimer?.Stop();
        _ballGifTimer = null;
        _ballGifFrames = null;
        _ballGifFrameDelays = null;
        _ballGifFrameIndex = 0;

        var style = FloatingBallStyles.Get(
            _settings.Current.FloatingBallStyle,
            _settings.Current.CustomBallImagePath);

        var isCustom = style.Id == FloatingBallStyles.CustomId;
        var imageUri = isCustom
            ? new Uri(style.ResourcePath, UriKind.Absolute)
            : new Uri($"pack://application:,,,/{style.ResourcePath}", UriKind.Absolute);
        BitmapSource imageSource;
        if (string.Equals(Path.GetExtension(style.ResourcePath), ".gif", StringComparison.OrdinalIgnoreCase))
        {
            Stream stream;
            if (isCustom)
            {
                stream = File.OpenRead(style.ResourcePath);
            }
            else
            {
                stream = Application.GetResourceStream(imageUri)?.Stream
                    ?? throw new FileNotFoundException($"Unable to load floating ball image: {style.ResourcePath}");
            }

            using (stream)
            {
                var decoder = new GifBitmapDecoder(stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var frames = decoder.Frames.ToList();
                foreach (var frame in frames)
                {
                    if (frame.CanFreeze) frame.Freeze();
                }
                _ballGifFrames = frames;
                _ballGifFrameDelays = frames.Select(GetGifFrameDelay).ToList();
                imageSource = _ballGifFrames[0];

                if (_ballGifFrames.Count > 1)
                {
                    _ballGifTimer = new DispatcherTimer(DispatcherPriority.Background)
                    {
                        Interval = _ballGifFrameDelays[0]
                    };
                    _ballGifTimer.Tick += BallGifTimer_Tick;
                    _ballGifTimer.Start();
                }
            }
        }
        else if (isCustom)
        {
            var bitmap = new BitmapImage();
            using (var stream = File.OpenRead(style.ResourcePath))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            if (bitmap.CanFreeze) bitmap.Freeze();
            imageSource = bitmap;
        }
        else
        {
            imageSource = new BitmapImage(imageUri);
        }

        RenderOptions.SetBitmapScalingMode(BallEllipse,
            style.Id == FloatingBallStyles.PixelLycheeId
                ? BitmapScalingMode.NearestNeighbor
                : BitmapScalingMode.HighQuality);
        BallEllipse.Fill = new ImageBrush
        {
            ImageSource = imageSource,
            Stretch = Stretch.UniformToFill
        };
    }

    private static TimeSpan GetGifFrameDelay(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.GetQuery("/grctlext/Delay") is { } delayValue)
            {
                var delayHundredths = Convert.ToInt32(delayValue);
                if (delayHundredths > 0)
                    return TimeSpan.FromMilliseconds(delayHundredths * 10);
            }
        }
        catch
        {
            // Use a short default delay for GIFs without frame timing metadata.
        }

        return TimeSpan.FromMilliseconds(100);
    }

    private void BallGifTimer_Tick(object? sender, EventArgs e)
    {
        if (_ballGifFrames is not { Count: > 1 } frames || BallEllipse.Fill is not ImageBrush brush) return;
        _ballGifFrameIndex = (_ballGifFrameIndex + 1) % frames.Count;
        brush.ImageSource = frames[_ballGifFrameIndex];
        if (_ballGifFrameDelays is { } delays)
            _ballGifTimer!.Interval = delays[_ballGifFrameIndex];
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style | WS_THICKFRAME);
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        var testableExStyle = _audioSpectrumUiTest
            ? ex & ~(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)
            : ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, testableExStyle | WS_EX_TOPMOST);
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(MainWindowHook);
        var initialPositionFlags = SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW;
        if (!_audioSpectrumUiTest) initialPositionFlags |= SWP_NOACTIVATE;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, (uint)initialPositionFlags);

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _topmostTimer.Tick += (s, e) =>
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        };
        _topmostTimer.Start();

        PositionToRightEdge();
        StartModules();
        ApplySettings();
        if (_audioSpectrumUiTest)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var workArea = SystemParameters.WorkArea;
                var expandedWidth = PanelWidth + BallSlot + PanelMargin;
                var expandedHeight = CalcExpandedHeight(1);
                var panelLeft = workArea.Left + (workArea.Width - expandedWidth) / 2;
                var panelTop = workArea.Top + (workArea.Height - expandedHeight) / 2;
                Left = panelLeft + PanelWidth + PanelMargin - Canvas.GetLeft(BallEllipse);
                Top = panelTop - PanelMargin;
                _isRightSide = true;
                _isBottomSide = false;
                Canvas.SetTop(BallEllipse, BallMargin);
                Canvas.SetTop(PanelBorder, PanelMargin);
                SyncBallGlow();
                if (_audioSpectrumUiTestCollapsed)
                    CollapsePanel(immediate: true, force: true);
                else
                    ExpandPanel(immediate: true);
            });
        }
    }

    private IntPtr MainWindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WM_ENTERSIZEMOVE:
                _isUserSizing = true;
                _isUserSizingHeight = false;
                return IntPtr.Zero;
            case WM_EXITSIZEMOVE:
                if (_isUserSizingHeight)
                    _hasUserResizedHeight = true;
                _isUserSizingHeight = false;
                _isUserSizing = false;
                return IntPtr.Zero;
        }

        if (message != WM_NCHITTEST || !_isExpanded || _bouncyActive || !GetWindowRect(hwnd, out var rect))
            return IntPtr.Zero;

        var x = unchecked((short)((long)lParam & 0xFFFF));
        var y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
        var onLeft = _isRightSide && x >= rect.Left && x < rect.Left + ResizeBorder;
        var onRight = !_isRightSide && x <= rect.Right && x > rect.Right - ResizeBorder;
        var onTop = _isBottomSide && y >= rect.Top && y < rect.Top + ResizeBorder;
        var onBottom = !_isBottomSide && y <= rect.Bottom && y > rect.Bottom - ResizeBorder;

        int? hit = (onLeft, onRight, onTop, onBottom) switch
        {
            (true, _, true, _) => HTTOPLEFT,
            (_, true, true, _) => HTTOPRIGHT,
            (true, _, _, true) => HTBOTTOMLEFT,
            (_, true, _, true) => HTBOTTOMRIGHT,
            (true, _, _, _) => HTLEFT,
            (_, true, _, _) => HTRIGHT,
            (_, _, true, _) => HTTOP,
            (_, _, _, true) => HTBOTTOM,
            _ => null
        };
        if (hit is null) return IntPtr.Zero;
        handled = true;
        return new IntPtr(hit.Value);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isExpanded || PanelBorder.Visibility != Visibility.Visible) return;

        // Remember where the ball is on screen so it can be kept there.
        var oldBallScreenX = Left + Canvas.GetLeft(BallEllipse);
        var oldBallScreenY = Top + Canvas.GetTop(BallEllipse);

        PanelBorder.Width = Math.Max(200, ActualWidth - BallSlot - PanelMargin);
        PanelBorder.Height = Math.Max(140, ActualHeight - PanelMargin * 2);
        Canvas.SetLeft(BallEllipse, _isRightSide
            ? Math.Max(BallMargin, ActualWidth - BallSlot)
            : BallMargin);
        Canvas.SetTop(BallEllipse, _isBottomSide
            ? Math.Max(BallMargin, ActualHeight - BallSlot + BallMargin)
            : BallMargin);
        Canvas.SetLeft(PanelBorder, _isRightSide ? PanelMargin : BallSlot);
        Canvas.SetTop(PanelBorder, PanelMargin);
        SyncBallGlow();

        if (_isUserSizing)
        {
            if (e.HeightChanged && _isExpanded && PanelBorder.Visibility == Visibility.Visible)
                _isUserSizingHeight = true;
            return;
        }

        // If the system resized the window differently than requested (work-area
        // clamping, DPI change, ...), shift the window origin by the same delta so
        // the ball's screen position - and therefore the next collapse position -
        // stays put instead of drifting/jumping.
        Left += oldBallScreenX - (Left + Canvas.GetLeft(BallEllipse));
        Top += oldBallScreenY - (Top + Canvas.GetTop(BallEllipse));
    }

    private void PositionToRightEdge()
    {
        // The window keeps its expanded size for its entire lifetime; expand and
        // collapse only toggle the panel's visibility. Transparent areas are
        // click-through, and never resizing the transparent window avoids the
        // one-frame compositing artifacts (dark remnant / flicker) entirely.
        var workArea = SystemParameters.WorkArea;
        var visibleModules = _moduleManager.Modules.Count(m => m.IsEnabled);
        var height = CalcExpandedHeight(visibleModules);
        height = Math.Min(height, Math.Max(180, workArea.Height));
        var width = PanelWidth + BallSlot + PanelMargin;

        MinWidth = BallSlot + PanelMargin + 200;
        MinHeight = Math.Min(180, height);
        Width = width;
        Height = height;

        var ballY = workArea.Top + (workArea.Height - BallSize) / 2;
        _isRightSide = true;
        _isBottomSide = ballY + BallSize / 2 >= workArea.Top + workArea.Height / 2;
        var ballCanvasY = _isBottomSide ? height - BallSlot + BallMargin : BallMargin;

        Left = workArea.Right - BallMargin - BallSize - (PanelWidth + PanelMargin);
        Top = ballY - ballCanvasY;

        Canvas.SetLeft(BallEllipse, PanelWidth + PanelMargin);
        Canvas.SetTop(BallEllipse, ballCanvasY);
        Canvas.SetLeft(PanelBorder, PanelMargin);
        Canvas.SetTop(PanelBorder, PanelMargin);
        SyncBallGlow();
    }

    private double GetBallScreenX()
    {
        return Left + Canvas.GetLeft(BallEllipse);
    }

    private double GetBallScreenY()
    {
        return Top + Canvas.GetTop(BallEllipse);
    }

    // Ball's monitor working area in WPF (DIP) units.
    private (double Left, double Top, double Right, double Bottom) GetBallWorkArea()
    {
        var ballCenter = PointToScreen(new Point(
            Canvas.GetLeft(BallEllipse) + BallSize / 2,
            Canvas.GetTop(BallEllipse) + BallSize / 2));
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)ballCenter.X, (int)ballCenter.Y));
        var source = PresentationSource.FromVisual(this);
        double scaleX = 1, scaleY = 1;
        if (source?.CompositionTarget != null)
        {
            scaleX = source.CompositionTarget.TransformFromDevice.M11;
            scaleY = source.CompositionTarget.TransformFromDevice.M22;
        }
        var wa = screen.WorkingArea;
        return (wa.Left * scaleX, wa.Top * scaleY, wa.Right * scaleX, wa.Bottom * scaleY);
    }

    private void SyncBallGlow()
    {
        Canvas.SetLeft(BallGlow, Canvas.GetLeft(BallEllipse) - BallMargin);
        Canvas.SetTop(BallGlow, Canvas.GetTop(BallEllipse) - BallMargin);
    }

    private string? GetActiveDisplayDeviceName()
    {
        if (PresentationSource.FromVisual(this) is null)
        {
            return System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;
        }

        var ballCenter = PointToScreen(new Point(
            Canvas.GetLeft(BallEllipse) + BallSize / 2,
            Canvas.GetTop(BallEllipse) + BallSize / 2));
        return System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)ballCenter.X, (int)ballCenter.Y)).DeviceName;
    }

    private void ExpandPanel(bool immediate = false)
    {
        if (_panelCollapsing)
        {
            // The cursor came back before the collapse fade finished; restore the panel.
            _panelCollapsing = false;
            PanelBorder.BeginAnimation(OpacityProperty, null);
            PanelBorder.Opacity = 1;
            if (_isExpanded) return;
        }

        if (_isExpanded) return;
        _isExpanded = true;

        // The window already has its final size; showing the panel is purely a
        // visibility change, so no transparent-window resize artifacts occur.
        PanelBorder.Width = Math.Max(200, ActualWidth - BallSlot - PanelMargin);
        PanelBorder.Height = Math.Max(140, ActualHeight - PanelMargin * 2);
        Canvas.SetLeft(PanelBorder, _isRightSide ? PanelMargin : BallSlot);
        Canvas.SetTop(PanelBorder, PanelMargin);
        PanelBorder.Visibility = Visibility.Visible;
        UpdateAudioSpectrumActivity();
        ResizeExpandedPanelToCurrentModules();

        PanelBorder.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        PanelBorder.BeginAnimation(OpacityProperty, fade);
    }

    private void CollapsePanel(bool immediate = false, bool force = false)
    {
        if (!_isExpanded && PanelBorder.Visibility != Visibility.Visible)
        {
            UpdateAudioSpectrumActivity();
            return;
        }

        if (_settings.Current.AlwaysShowPanel && !force)
        {
            UpdateAudioSpectrumActivity();
            return;
        }

        if (!immediate && PanelBorder.Visibility == Visibility.Visible)
        {
            if (_panelCollapsing) return;
            // Fade the panel out before hiding it so the collapse reads as one
            // smooth motion; the window geometry never changes here.
            _panelCollapsing = true;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
            fade.Completed += (s, e) =>
            {
                if (_panelCollapsing) FinishPanelCollapse();
            };
            PanelBorder.BeginAnimation(OpacityProperty, fade);
            return;
        }

        FinishPanelCollapse();
    }

    private void FinishPanelCollapse()
    {
        _panelCollapsing = false;
        PanelBorder.BeginAnimation(OpacityProperty, null);
        PanelBorder.Opacity = 1;
        _isExpanded = false;
        PanelBorder.Visibility = Visibility.Collapsed;
        UpdateAudioSpectrumActivity();
    }

    private void ResizeExpandedPanelToCurrentModules()
    {
        // Keep a manually selected panel height across hover collapse/expand.
        // Without this, every unpinned re-expand resets the user's resize to the
        // automatic module-based height.
        if (_hasUserResizedHeight) return;

        var targetHeight = Math.Max(MinHeight, CalcExpandedHeight(_moduleManager.Modules.Count(module => module.IsEnabled)));
        if (Math.Abs(targetHeight - Height) < 1) return;

        var ballScreenY = GetBallScreenY();
        var workArea = GetBallWorkArea();
        targetHeight = Math.Min(targetHeight, Math.Max(MinHeight, workArea.Bottom - workArea.Top));
        var difference = targetHeight - Height;
        var desiredTop = _isBottomSide ? Top - difference : Top;
        Top = Math.Clamp(desiredTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - targetHeight));
        Height = targetHeight;
        PanelBorder.Height = targetHeight - PanelMargin * 2;
        Canvas.SetTop(BallEllipse, ballScreenY - Top);
        SyncBallGlow();
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        if (_audioSpectrumUiTestCollapsed || _bouncyActive || _isDragging || _isMouseDown) return;

        if (_settingsWindow is { IsVisible: true } && _settings.Current.UseCustomPanelAppearance)
        {
            if (!_isExpanded) ExpandPanel(immediate: true);
            return;
        }

        var src = PresentationSource.FromVisual(this);
        double scaleX = 1, scaleY = 1;
        if (src != null)
        {
            scaleX = src.CompositionTarget.TransformFromDevice.M11;
            scaleY = src.CompositionTarget.TransformFromDevice.M22;
        }

        var cursor = System.Windows.Forms.Cursor.Position;
        var winOrigin = PointToScreen(new Point(0, 0));
        var relX = (cursor.X - winOrigin.X) * scaleX;
        var relY = (cursor.Y - winOrigin.Y) * scaleY;

        // Anchor the hover zones to the ball and panel instead of the window rect:
        // the window moves/resizes between the collapsed and expanded states, so a
        // cursor resting near the ball's edge (e.g. the transparent margin strips on
        // the docked side) would flip in/out of the window rect on every tick and
        // make the panel and ball flicker between two positions.
        var ballLeft = Canvas.GetLeft(BallEllipse);
        var ballTop = Canvas.GetTop(BallEllipse);
        bool inBallZone = relX >= ballLeft - BallHoverMargin && relX <= ballLeft + BallSize + BallHoverMargin
                       && relY >= ballTop - BallHoverMargin && relY <= ballTop + BallSize + BallHoverMargin;

        bool inPanelZone = false;
        if (_isExpanded && PanelBorder.Visibility == Visibility.Visible)
        {
            var panelLeft = Canvas.GetLeft(PanelBorder);
            var panelTop = Canvas.GetTop(PanelBorder);
            inPanelZone = relX >= panelLeft - PanelHoverMargin && relX <= panelLeft + PanelBorder.ActualWidth + PanelHoverMargin
                       && relY >= panelTop - PanelHoverMargin && relY <= panelTop + PanelBorder.ActualHeight + PanelHoverMargin;
        }

        bool inHoverZone = inBallZone || inPanelZone;

        if (inHoverZone && !_isHoveringBall)
        {
            _isHoveringBall = true;
            BallScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.1, TimeSpan.FromMilliseconds(120)));
            BallScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.1, TimeSpan.FromMilliseconds(120)));
            BallGlow.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(120)));
            if (!_settings.Current.AlwaysShowPanel)
            {
                ExpandPanel();
            }
        }
        else if (!inHoverZone && _isHoveringBall)
        {
            _isHoveringBall = false;
            BallScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)));
            BallScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)));
            BallGlow.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(120)));
            if (!_settings.Current.AlwaysShowPanel)
            {
                CollapsePanel();
            }
        }
    }

    private bool IsMouseOverPanel()
    {
        if (PanelBorder.Visibility != Visibility.Visible) return false;
        var pos = Mouse.GetPosition(PanelBorder);
        return pos.X >= 0 && pos.X <= PanelBorder.ActualWidth
            && pos.Y >= 0 && pos.Y <= PanelBorder.ActualHeight;
    }

    private void Ball_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_bouncyActive)
        {
            if (e.ClickCount >= 2)
            {
                _settings.Update(s => s.BouncyBall = false);
            }
            return;
        }

        if (e.ClickCount >= 2)
        {
            TogglePin();
            return;
        }

        _isMouseDown = true;

        _dragStartScreen = PointToScreen(e.GetPosition(this));

        if (_isExpanded)
        {
            _restorePinnedAfterDrag = _settings.Current.AlwaysShowPanel;
            CollapsePanel(immediate: true, force: _settings.Current.AlwaysShowPanel);
        }

        _dragStartBallScreenX = GetBallScreenX();
        _dragStartBallScreenY = GetBallScreenY();
        _isDragging = false;
        _dragStarted = false;
        BallEllipse.CaptureMouse();
    }

    private void Ball_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var currentScreen = PointToScreen(e.GetPosition(this));
        var dxDevice = currentScreen.X - _dragStartScreen.X;
        var dyDevice = currentScreen.Y - _dragStartScreen.Y;

        if (!_dragStarted && (Math.Abs(dxDevice) > DragThreshold || Math.Abs(dyDevice) > DragThreshold))
        {
            _dragStarted = true;
            _isDragging = true;
        }

        if (_isDragging)
        {
            double scaleX = 1, scaleY = 1;
            var src = PresentationSource.FromVisual(this);
            if (src != null)
            {
                scaleX = src.CompositionTarget.TransformFromDevice.M11;
                scaleY = src.CompositionTarget.TransformFromDevice.M22;
            }

            // Anchor the drag to the BALL's screen position, not the window's:
            // the collapse shrink can apply mid-drag and change the window origin,
            // so deriving the window position from the ball (with the canvas
            // offset read fresh each move) keeps the ball glued to the cursor.
            var targetBallX = _dragStartBallScreenX + dxDevice * scaleX;
            var targetBallY = _dragStartBallScreenY + dyDevice * scaleY;

            var vs = SystemInformation.VirtualScreen;
            double vsLeft = vs.Left * scaleX;
            double vsTop = vs.Top * scaleY;
            double vsRight = vs.Right * scaleX;
            double vsBottom = vs.Bottom * scaleY;

            if (targetBallX < vsLeft) targetBallX = vsLeft;
            if (targetBallX + BallSize > vsRight) targetBallX = vsRight - BallSize;
            if (targetBallY < vsTop) targetBallY = vsTop;
            if (targetBallY + BallSize > vsBottom) targetBallY = vsBottom - BallSize;

            Left = targetBallX - Canvas.GetLeft(BallEllipse);
            Top = targetBallY - Canvas.GetTop(BallEllipse);
        }
    }

    private void Ball_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isMouseDown = false;

        if (_isDragging)
        {
            _isDragging = false;
            _dragStarted = false;
            BallEllipse.ReleaseMouseCapture();
            RefreshBallSideFromPosition();
            if (_settings.Current.SnapToEdge)
            {
                SnapToNearestEdge(RestorePinnedPanelAfterDrag);
            }
            else
            {
                RestorePinnedPanelAfterDrag();
            }
        }
    }

    private void RestorePinnedPanelAfterDrag()
    {
        if (!_restorePinnedAfterDrag) return;
        _restorePinnedAfterDrag = false;
        if (_settings.Current.AlwaysShowPanel)
        {
            ExpandPanel();
        }
    }

    private void RefreshBallSideFromPosition()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)Left, (int)Top));
        var wa = screen.WorkingArea;

        double scaleX = 1, scaleY = 1;
        var src = PresentationSource.FromVisual(this);
        if (src != null)
        {
            scaleX = src.CompositionTarget.TransformFromDevice.M11;
            scaleY = src.CompositionTarget.TransformFromDevice.M22;
        }

        var waLeft = wa.Left * scaleX;
        var waWidth = wa.Width * scaleX;
        var waTop = wa.Top * scaleY;
        var waHeight = wa.Height * scaleY;

        var ballCenterX = GetBallScreenX() + BallSize / 2;
        var ballCenterY = GetBallScreenY() + BallSize / 2;
        _isRightSide = ballCenterX >= waLeft + waWidth / 2;
        _isBottomSide = ballCenterY >= waTop + waHeight / 2;
        RelayoutDockedCanvas();
    }

    // Re-arranges the ball/panel canvas positions for the current dock side
    // (ball in the docked corner, panel on the opposite side) and shifts the
    // window origin by the same delta so the ball's screen position is kept.
    private void RelayoutDockedCanvas()
    {
        var oldBallX = Canvas.GetLeft(BallEllipse);
        var oldBallY = Canvas.GetTop(BallEllipse);

        var newBallX = _isRightSide ? Math.Max(BallMargin, ActualWidth - BallSlot) : BallMargin;
        var newBallY = _isBottomSide ? Math.Max(BallMargin, ActualHeight - BallSlot + BallMargin) : BallMargin;
        if (Math.Abs(newBallX - oldBallX) < 0.01 && Math.Abs(newBallY - oldBallY) < 0.01) return;

        Canvas.SetLeft(BallEllipse, newBallX);
        Canvas.SetTop(BallEllipse, newBallY);
        Canvas.SetLeft(PanelBorder, _isRightSide ? PanelMargin : BallSlot);
        Canvas.SetTop(PanelBorder, PanelMargin);
        SyncBallGlow();

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left += oldBallX - newBallX;
        Top += oldBallY - newBallY;
    }

    private void SnapToNearestEdge(Action? onCompleted = null)
    {
        var src = PresentationSource.FromVisual(this);
        if (src == null) return;
        double scaleX = src.CompositionTarget.TransformFromDevice.M11;
        double scaleY = src.CompositionTarget.TransformFromDevice.M22;

        var ballCenterDevice = PointToScreen(new Point(
            Canvas.GetLeft(BallEllipse) + BallSize / 2,
            Canvas.GetTop(BallEllipse) + BallSize / 2));
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)ballCenterDevice.X, (int)ballCenterDevice.Y));
        var wa = screen.WorkingArea;

        var waLeft = wa.Left * scaleX;
        var waWidth = wa.Width * scaleX;
        var waTop = wa.Top * scaleY;
        var waHeight = wa.Height * scaleY;
        var waRight = waLeft + waWidth;
        var waBottom = waTop + waHeight;

        // Ball's actual screen position (works whether the panel is collapsed or expanded).
        var ballScreenX = GetBallScreenX();
        var ballScreenY = GetBallScreenY();
        var ballCenterX = ballScreenX + BallSize / 2;
        var ballCenterY = ballScreenY + BallSize / 2;

        var dLeft = Math.Abs(ballCenterX - waLeft);
        var dRight = Math.Abs(waRight - ballCenterX);
        var dTop = Math.Abs(ballCenterY - waTop);
        var dBottom = Math.Abs(waBottom - ballCenterY);

        var min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

        var ballLeft = Canvas.GetLeft(BallEllipse);
        var ballTop = Canvas.GetTop(BallEllipse);

        double targetLeft = Left, targetTop = Top;
        if (min == dLeft) targetLeft = waLeft - ballLeft;
        else if (min == dRight) targetLeft = waRight - ballLeft - BallSize;
        else if (min == dTop) targetTop = waTop - ballTop;
        else targetTop = waBottom - ballTop - BallSize;

        _isRightSide = ballCenterX >= waLeft + waWidth / 2;
        _isBottomSide = ballCenterY >= waTop + waHeight / 2;

        if (Math.Abs(targetLeft - Left) < 0.5 && Math.Abs(targetTop - Top) < 0.5)
        {
            RelayoutDockedCanvas();
            onCompleted?.Invoke();
            return;
        }

        // Commit the final position to the base value once the animation finishes:
        // HoldEnd animations keep Left/Top "held", and a later BeginAnimation(null)
        // (expand/collapse) would otherwise revert them to the stale pre-snap base
        // and teleport the window.
        var slide = new DoubleAnimation(Left, targetLeft, TimeSpan.FromMilliseconds(120));
        slide.Completed += (s, e) =>
        {
            BeginAnimation(LeftProperty, null);
            Left = targetLeft;
        };
        BeginAnimation(LeftProperty, slide);
        var slideY = new DoubleAnimation(Top, targetTop, TimeSpan.FromMilliseconds(120));
        slideY.Completed += (s, e) =>
        {
            BeginAnimation(TopProperty, null);
            Top = targetTop;
            RelayoutDockedCanvas();
            onCompleted?.Invoke();
        };
        BeginAnimation(TopProperty, slideY);
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => TogglePin();

    private void StartBouncyBallMode()
    {
        if (_bouncyActive) return;
        _bouncyActive = true;

        if (_settings.Current.AlwaysShowPanel)
            _settings.Update(s => s.AlwaysShowPanel = false);
        if (_settings.Current.SnapToEdge)
            _settings.Update(s => s.SnapToEdge = false);

        CollapsePanel(immediate: true, force: true);

        var angle = _bouncyRng.NextDouble() * Math.PI * 2;
        var speed = 320 + _bouncyRng.NextDouble() * 160;
        _bouncyVelocity = new Vector(Math.Cos(angle) * speed, Math.Sin(angle) * speed);
        _bouncyAngularVelocity = (_bouncyRng.NextDouble() * 2 - 1) * 360;

        _bouncyLastTick = DateTime.UtcNow;
        _bouncyLastImpulse = DateTime.MinValue;

        _bouncyTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _bouncyTimer.Tick += BouncyTimer_Tick;
        _bouncyTimer.Start();

        _bouncyTimeoutTimer = new DispatcherTimer
        {
            Interval = BouncyDuration
        };
        _bouncyTimeoutTimer.Tick += (s, e) =>
        {
            _settings.Update(st => st.BouncyBall = false);
        };
        _bouncyTimeoutTimer.Start();
    }

    private void StopBouncyBallMode(bool snapAfter)
    {
        if (!_bouncyActive) return;
        _bouncyActive = false;

        _bouncyTimer?.Stop();
        _bouncyTimer = null;
        _bouncyTimeoutTimer?.Stop();
        _bouncyTimeoutTimer = null;

        BallRotate.Angle = 0;
        _bouncyVelocity = new Vector(0, 0);
        _bouncyAngularVelocity = 0;

        if (snapAfter)
        {
            RefreshBallSideFromPosition();
            SnapToNearestEdge();
        }
    }

    private void BouncyTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _bouncyLastTick).TotalSeconds;
        _bouncyLastTick = now;
        if (dt <= 0 || dt > 0.1) dt = 1.0 / 60;

        var src = PresentationSource.FromVisual(this);
        double scaleX = 1, scaleY = 1;
        if (src?.CompositionTarget != null)
        {
            scaleX = src.CompositionTarget.TransformFromDevice.M11;
            scaleY = src.CompositionTarget.TransformFromDevice.M22;
        }

        var friction = Math.Pow(1.0 - BouncyFrictionPerSec, dt);
        _bouncyVelocity *= friction;
        _bouncyAngularVelocity *= Math.Pow(1.0 - BouncyAngularFrictionPerSec, dt);

        var speed = _bouncyVelocity.Length;
        if (speed < BouncyMinSpeed)
        {
            if (speed > 0.001)
                _bouncyVelocity *= BouncyMinSpeed / speed;
            else
                _bouncyVelocity = new Vector(BouncyMinSpeed, 0);
        }
        else if (speed > BouncyMaxSpeed)
        {
            _bouncyVelocity *= BouncyMaxSpeed / speed;
        }

        if (now - _bouncyLastImpulse > BouncyImpulseCooldown)
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var mouseX = cursor.X * scaleX;
            var mouseY = cursor.Y * scaleY;

            var ballCenterX = GetBallScreenX() + BallSize / 2;
            var ballCenterY = GetBallScreenY() + BallSize / 2;
            var dx = ballCenterX - mouseX;
            var dy = ballCenterY - mouseY;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < BallSize)
            {
                var len = Math.Max(dist, 0.001);
                var dir = new Vector(dx / len, dy / len);
                _bouncyVelocity += dir * BouncyImpulse;
                _bouncyAngularVelocity += (_bouncyRng.NextDouble() * 2 - 1) * 240;
                _bouncyLastImpulse = now;
            }
        }

        Left += _bouncyVelocity.X * dt;
        Top += _bouncyVelocity.Y * dt;
        BallRotate.Angle += _bouncyAngularVelocity * dt;

        var ballX = Left + Canvas.GetLeft(BallEllipse);
        var ballY = Top + Canvas.GetTop(BallEllipse);
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)(ballX + BallSize / 2), (int)(ballY + BallSize / 2)));
        var waLeft = screen.WorkingArea.Left * scaleX;
        var waTop = screen.WorkingArea.Top * scaleY;
        var waRight = screen.WorkingArea.Right * scaleX;
        var waBottom = screen.WorkingArea.Bottom * scaleY;

        if (ballX < waLeft)
        {
            Left += waLeft - ballX;
            _bouncyVelocity.X = Math.Abs(_bouncyVelocity.X) * BouncyRestitution;
            _bouncyAngularVelocity = -_bouncyAngularVelocity * 0.85;
        }
        else if (ballX + BallSize > waRight)
        {
            Left += waRight - (ballX + BallSize);
            _bouncyVelocity.X = -Math.Abs(_bouncyVelocity.X) * BouncyRestitution;
            _bouncyAngularVelocity = -_bouncyAngularVelocity * 0.85;
        }
        if (ballY < waTop)
        {
            Top += waTop - ballY;
            _bouncyVelocity.Y = Math.Abs(_bouncyVelocity.Y) * BouncyRestitution;
            _bouncyAngularVelocity = -_bouncyAngularVelocity * 0.85;
        }
        else if (ballY + BallSize > waBottom)
        {
            Top += waBottom - (ballY + BallSize);
            _bouncyVelocity.Y = -Math.Abs(_bouncyVelocity.Y) * BouncyRestitution;
            _bouncyAngularVelocity = -_bouncyAngularVelocity * 0.85;
        }
    }

    private void TogglePin()
    {
        _settings.Update(s => s.AlwaysShowPanel = !s.AlwaysShowPanel);
        if (_settings.Current.AlwaysShowPanel)
        {
            ExpandPanel();
        }
        else if (!_isHoveringBall)
        {
            CollapsePanel();
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Current.AlwaysShowPanel)
        {
            _settings.Update(s => s.AlwaysShowPanel = false);
        }
        CollapsePanel();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => ShutdownApp();

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, _moduleManager);
        _settingsWindow.Owner = this;
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (!_settings.Current.AlwaysShowPanel && !_isHoveringBall)
                CollapsePanel(immediate: true);
        };
        _settingsWindow.Show();
    }

    private void ToggleVisibility()
    {
        if (Visibility == Visibility.Visible)
        {
            Visibility = Visibility.Hidden;
        }
        else
        {
            Visibility = Visibility.Visible;
        }
        UpdateAudioSpectrumActivity();
    }

    private async void ShutdownApp()
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        StopBouncyBallMode(snapAfter: false);
        await _moduleManager.DisposeAsync();
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    private void OnIpChanged(object? sender, IpChangedEventArgs e)
    {
        if (!_settings.Current.AlertOnIpChange) return;

        Dispatcher.Invoke(() =>
        {
            _tray.ShowBalloon(
                "Public IP Changed",
                $"Old: {e.OldIp}\nNew: {e.NewIp}",
                System.Windows.Forms.ToolTipIcon.Warning);
            ShowIpChangeToast(e.OldIp, e.NewIp);
        });
    }

    private void OnIpQueryFailed(object? sender, QueryFailedEventArgs e)
    {
        if (!_settings.Current.AlertOnQueryFailure) return;

        Dispatcher.Invoke(() =>
        {
            _tray.ShowBalloon(
                "Public IP Query Failed",
                $"{e.Message}\nNetwork may be down or VPN dropped.",
                System.Windows.Forms.ToolTipIcon.Error);
            ShowAlertToast(
                "Public IP Query Failed",
                e.Message,
                "Network may be down or VPN dropped.");
        });
    }

    private void OnIpQueryRecovered(object? sender, QueryRecoveredEventArgs e)
    {
        if (!_settings.Current.AlertOnQueryFailure) return;

        Dispatcher.Invoke(() =>
        {
            _tray.ShowBalloon(
                "Network Recovered",
                $"Public IP: {e.Ip}",
                System.Windows.Forms.ToolTipIcon.Info);
            var displayLines = e.Display.Split('\n');
            displayLines[0] = $"IP: {displayLines[0]}";
            ShowAlertToast(
                "Network Recovered",
                System.Windows.Media.Color.FromArgb(0xF2, 0x2E, 0xCC, 0x71),
                displayLines);
        });
    }

    private void ShowIpChangeToast(string oldIp, string newIp)
    {
        ShowAlertToast("Public IP Changed", $"Old: {oldIp}", $"New: {newIp}");
    }

    private void ShowAlertToast(string title, params string[] lines)
    {
        ShowAlertToast(title, null, lines);
    }

    private void ShowAlertToast(string title, System.Windows.Media.Color? background, params string[] lines)
    {
        _alertToast?.Close();
        _alertToast = null;

        var toast = new Window
        {
            Width = 320,
            Height = 120,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        var border = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                background ?? System.Windows.Media.Color.FromArgb(0xF2, 0xE8, 0x4D, 0x3D)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(6)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6)
        });
        foreach (var line in lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 12
            });
        }
        border.Child = stack;
        toast.Content = border;

        var area = SystemParameters.WorkArea;
        toast.Left = area.Right - toast.Width - 16;
        toast.Top = area.Bottom - toast.Height - 16;
        _alertToast = toast;
        toast.Show();

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400))
        {
            BeginTime = TimeSpan.FromSeconds(5)
        };
        fadeOut.Completed += (s, e) =>
        {
            toast.Close();
            if (_alertToast == toast) _alertToast = null;
        };
        toast.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_audioSpectrumUiTest) return;
        if (_settingsWindow is { IsVisible: true } && _settings.Current.UseCustomPanelAppearance) return;
        if (!_settings.Current.AlwaysShowPanel && !_isHoveringBall && !IsMouseOverPanel())
        {
            CollapsePanel();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_shutdownStarted)
        {
            e.Cancel = true;
            ShutdownApp();
            return;
        }
        base.OnClosing(e);
    }
}
