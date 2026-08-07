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
    private bool _shutdownStarted;

    private bool _isRightSide = true;
    private bool _isBottomSide = true;
    private bool _isDragging;
    private bool _isMouseDown;
    private bool _dragStarted;
    private bool _restorePinnedAfterDrag;
    private Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;
    private const double DragThreshold = 5;

    private bool _bouncyActive;
    private DispatcherTimer? _bouncyTimer;
    private DispatcherTimer? _bouncyTimeoutTimer;
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
        InitializeComponent();

        _settings = new SettingsService();
        _moduleManager = new ModuleManager(_settings);
        _moduleManager.RegisterModule(new DateTimeModule());
        _moduleManager.RegisterModule(new NetworkSpeedModule());
        _moduleManager.RegisterModule(new PublicIpModule());
        _moduleManager.RegisterModule(new CpuModule());
        _moduleManager.RegisterModule(new MemoryModule());
        _moduleManager.RegisterModule(new LatencyModule());
        _moduleManager.RegisterModule(new FpsModule(_settings, GetActiveDisplayDeviceName));

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

        if (s.BouncyBall)
        {
            if (!_bouncyActive) StartBouncyBallMode();
        }
        else
        {
            if (_bouncyActive) StopBouncyBallMode(snapAfter: true);
        }

        if (_bouncyActive) return;

        if (s.AlwaysShowPanel)
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
        SyncBallGlow();
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
        if (_isExpanded) return;
        _isExpanded = true;

        var ballX = GetBallScreenX();
        var ballY = GetBallScreenY();

        var visibleModules = _moduleManager.Modules.Count(m => m.IsEnabled);
        var targetHeight = CalcExpandedHeight(visibleModules);
        var targetWidth = PanelWidth + BallSlot + PanelMargin;

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);

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

        SyncBallGlow();

        PanelBorder.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        PanelBorder.BeginAnimation(OpacityProperty, fade);
    }

    private void CollapsePanel(bool immediate = false, bool force = false)
    {
        if (!_isExpanded) return;
        _isExpanded = false;

        if (_settings.Current.AlwaysShowPanel && !force) return;

        var ballX = GetBallScreenX();
        var ballY = GetBallScreenY();

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);

        Width = BallSlot;
        Height = BallSlot;
        Canvas.SetLeft(BallEllipse, BallMargin);
        Canvas.SetTop(BallEllipse, BallMargin);
        SyncBallGlow();
        Left = ballX - BallMargin;
        Top = ballY - BallMargin;

        PanelBorder.Visibility = Visibility.Collapsed;
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        if (_bouncyActive || _isDragging || _isMouseDown) return;

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
            BallGlow.BeginAnimation(OpacityProperty,
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

        var ballCenterX = Left + BallSlot / 2;
        var ballCenterY = Top + BallSlot / 2;
        _isRightSide = ballCenterX >= waLeft + waWidth / 2;
        _isBottomSide = ballCenterY >= waTop + waHeight / 2;
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
            onCompleted?.Invoke();
            return;
        }

        var slide = new DoubleAnimation(Left, targetLeft, TimeSpan.FromMilliseconds(120));
        BeginAnimation(LeftProperty, slide);
        var slideY = new DoubleAnimation(Top, targetTop, TimeSpan.FromMilliseconds(120));
        slideY.Completed += (s, e) => onCompleted?.Invoke();
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

        Width = BallSlot;
        Height = BallSlot;
        Canvas.SetLeft(BallEllipse, BallMargin);
        Canvas.SetTop(BallEllipse, BallMargin);
        SyncBallGlow();

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

            var ballCenterX = Left + BallMargin + BallSize / 2;
            var ballCenterY = Top + BallMargin + BallSize / 2;
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

        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)(Left + BallSlot / 2), (int)(Top + BallSlot / 2)));
        var waLeft = screen.WorkingArea.Left * scaleX;
        var waTop = screen.WorkingArea.Top * scaleY;
        var waRight = screen.WorkingArea.Right * scaleX;
        var waBottom = screen.WorkingArea.Bottom * scaleY;

        if (Left < waLeft)
        {
            Left = waLeft;
            _bouncyVelocity.X = Math.Abs(_bouncyVelocity.X) * BouncyRestitution;
            _bouncyAngularVelocity = -_bouncyAngularVelocity * 0.85;
        }
        else if (Left + BallSlot > waRight)
        {
            Left = waRight - BallSlot;
            _bouncyVelocity.X = -Math.Abs(_bouncyVelocity.X) * BouncyRestitution;
            _bouncyAngularVelocity = -_bouncyAngularVelocity * 0.85;
        }
        if (Top < waTop)
        {
            Top = waTop;
            _bouncyVelocity.Y = Math.Abs(_bouncyVelocity.Y) * BouncyRestitution;
            _bouncyAngularVelocity = -_bouncyAngularVelocity * 0.85;
        }
        else if (Top + BallSlot > waBottom)
        {
            Top = waBottom - BallSlot;
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
        if (!_shutdownStarted)
        {
            e.Cancel = true;
            ShutdownApp();
            return;
        }
        base.OnClosing(e);
    }
}
