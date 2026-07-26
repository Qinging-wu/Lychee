using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lychee.Core;
using Lychee.Modules;

namespace Lychee;

public partial class MainWindow : Window
{
    private const double BallSize = 48;
    private const double BallMargin = 4;
    private const double BallSlot = 56;          // total window size occupied by the ball (incl. margins)
    private const double PanelWidth = 240;
    private const double PanelMargin = 4;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly SettingsService _settings;
    private readonly ModuleManager _moduleManager;
    private readonly TrayIconService _tray;
    private readonly DispatcherTimer _hoverTimer;
    private DispatcherTimer? _topmostTimer;
    private bool _isExpanded = false;
    private bool _isHoveringBall = false;
    private SettingsWindow? _settingsWindow;

    private bool _isRightSide = true;
    private bool _isBottomSide = true;
    private bool _isDragging;
    private bool _isMouseDown;
    private bool _dragStarted;
    private Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;
    private const double DragThreshold = 5;

    public MainWindow()
    {
        InitializeComponent();

        _settings = new SettingsService();
        _moduleManager = new ModuleManager(_settings);
        _moduleManager.RegisterModule(new DateTimeModule());
        _moduleManager.RegisterModule(new NetworkSpeedModule());
        _moduleManager.RegisterModule(new PublicIpModule());
        _moduleManager.RegisterModule(new CpuModule());
        _moduleManager.RegisterModule(new MemoryModule());
        _moduleManager.RegisterModule(new LatencyModule());

        if (_moduleManager.Get("public-ip") is PublicIpModule ipModule)
        {
            ipModule.IpChanged += OnIpChanged;
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
    }

    public void StartModules() => _moduleManager.StartAll();

    private void RefreshModuleList()
    {
        var visibleModules = _moduleManager.Modules.Where(m => m.IsEnabled).ToList();
        var current = ModuleList.ItemsSource as List<IInfoModule>;
        if (current != null &&
            current.Count == visibleModules.Count &&
            !current.Except(visibleModules).Any())
        {
            return;
        }
        ModuleList.ItemsSource = visibleModules;
    }

    private double CalcExpandedHeight(int moduleCount)
    {
        var approxItemHeight = 70;
        var headerHeight = 50;
        var padding = 16;
        var target = headerHeight + padding + moduleCount * approxItemHeight;
        if (target > 540) target = 540;
        return target;
    }

    private void ApplySettings()
    {
        var s = _settings.Current;
        if (s.AlwaysShowPanel)
        {
            ExpandPanel(immediate: true);
        }
        else if (!_isHoveringBall)
        {
            CollapsePanel(immediate: true);
        }
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

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
    }

    private void PositionToRightEdge()
    {
        var workArea = SystemParameters.WorkArea;
        Top = workArea.Top + (workArea.Height - BallSlot) / 2;
        Left = workArea.Right - BallSlot;
        _isRightSide = true;
        _isBottomSide = Top + BallSlot / 2 >= workArea.Top + workArea.Height / 2;
        Width = BallSlot;
        Height = BallSlot;
        Canvas.SetLeft(BallEllipse, BallMargin);
        Canvas.SetTop(BallEllipse, BallMargin);
        Canvas.SetTop(PanelBorder, PanelMargin);
    }

    private double GetBallScreenX()
    {
        return Left + Canvas.GetLeft(BallEllipse);
    }

    private double GetBallScreenY()
    {
        return Top + Canvas.GetTop(BallEllipse);
    }

    private void ExpandPanel(bool immediate = false)
    {
        if (_isExpanded) return;
        _isExpanded = true;

        var ballX = GetBallScreenX();
        var ballY = GetBallScreenY();

        var visibleModules = _moduleManager.Modules.Count(m => m.IsEnabled);
        var targetHeight = CalcExpandedHeight(visibleModules);
        var targetWidth = PanelWidth + BallSlot + PanelMargin;

        Width = targetWidth;
        Height = targetHeight;

        PanelBorder.Height = targetHeight - PanelMargin * 2;
        PanelBorder.Visibility = Visibility.Visible;

        if (_isRightSide)
        {
            Canvas.SetLeft(PanelBorder, PanelMargin);
            Canvas.SetLeft(BallEllipse, PanelWidth + PanelMargin);
            Left = ballX - PanelWidth - PanelMargin;
        }
        else
        {
            Canvas.SetLeft(BallEllipse, BallMargin);
            Canvas.SetLeft(PanelBorder, BallSlot);
            Left = ballX - BallMargin;
        }

        if (_isBottomSide)
        {
            // expand upward when docked to bottom
            Top = ballY - targetHeight + BallSlot - BallMargin;
            Canvas.SetTop(BallEllipse, targetHeight - BallSlot + BallMargin);
            Canvas.SetTop(PanelBorder, PanelMargin);
        }
        else
        {
            // expand upward when docked to bottom
            Top = ballY - BallMargin;
            Canvas.SetTop(BallEllipse, BallMargin);
            Canvas.SetTop(PanelBorder, PanelMargin);
        }

        PanelBorder.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        PanelBorder.BeginAnimation(OpacityProperty, fade);
    }

    private void CollapsePanel(bool immediate = false)
    {
        if (!_isExpanded) return;
        _isExpanded = false;

        if (_settings.Current.AlwaysShowPanel) return;

        var ballX = GetBallScreenX();
        var ballY = GetBallScreenY();

        Width = BallSlot;
        Height = BallSlot;
        Canvas.SetLeft(BallEllipse, BallMargin);
        Canvas.SetTop(BallEllipse, BallMargin);
        Left = ballX - BallMargin;
        Top = ballY - BallMargin;

        PanelBorder.Visibility = Visibility.Collapsed;
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDragging || _isMouseDown) return;

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

        bool inWindow = relX >= 0 && relX <= ActualWidth
                     && relY >= 0 && relY <= ActualHeight;

        if (inWindow && !_isHoveringBall)
        {
            _isHoveringBall = true;
            BallScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.1, TimeSpan.FromMilliseconds(120)));
            BallScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.1, TimeSpan.FromMilliseconds(120)));
            BallShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(120)));
            if (!_settings.Current.AlwaysShowPanel)
            {
                ExpandPanel();
            }
        }
        else if (!inWindow && _isHoveringBall)
        {
            _isHoveringBall = false;
            BallScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)));
            BallScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)));
            BallShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
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
        if (e.ClickCount >= 2)
        {
            TogglePin();
            return;
        }

        _isMouseDown = true;

        _dragStartScreen = PointToScreen(e.GetPosition(this));

        if (_isExpanded && !_settings.Current.AlwaysShowPanel)
        {
            CollapsePanel(immediate: true);
        }

        _dragStartLeft = Left;
        _dragStartTop = Top;
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

            var newLeft = _dragStartLeft + dxDevice * scaleX;
            var newTop = _dragStartTop + dyDevice * scaleY;

            var vs = SystemInformation.VirtualScreen;
            double vsLeft = vs.Left * scaleX;
            double vsTop = vs.Top * scaleY;
            double vsRight = vs.Right * scaleX;
            double vsBottom = vs.Bottom * scaleY;

            if (newLeft < vsLeft) newLeft = vsLeft;
            if (newLeft + BallSlot > vsRight) newLeft = vsRight - BallSlot;
            if (newTop < vsTop) newTop = vsTop;
            if (newTop + BallSlot > vsBottom) newTop = vsBottom - BallSlot;

            Left = newLeft;
            Top = newTop;
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

        var ballCenterX = Left + BallSlot / 2;
        var ballCenterY = Top + BallSlot / 2;
        _isRightSide = ballCenterX >= waLeft + waWidth / 2;
        _isBottomSide = ballCenterY >= waTop + waHeight / 2;
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => TogglePin();

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
    }

    private void ShutdownApp()
    {
        _moduleManager.Dispose();
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

    private void ShowIpChangeToast(string oldIp, string newIp)
    {
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
                System.Windows.Media.Color.FromArgb(0xF2, 0xE8, 0x4D, 0x3D)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(6)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "Public IP Changed",
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"Old: {oldIp}",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 12
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"New: {newIp}",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 12
        });
        border.Child = stack;
        toast.Content = border;

        var area = SystemParameters.WorkArea;
        toast.Left = area.Right - toast.Width - 16;
        toast.Top = area.Bottom - toast.Height - 16;
        toast.Show();

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400))
        {
            BeginTime = TimeSpan.FromSeconds(5)
        };
        fadeOut.Completed += (s, e) => toast.Close();
        toast.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_settings.Current.AlwaysShowPanel && !_isHoveringBall && !IsMouseOverPanel())
        {
            CollapsePanel();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _moduleManager.Dispose();
        _tray.Dispose();
        base.OnClosing(e);
    }
}
