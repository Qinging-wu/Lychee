using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Lychee.Core;

namespace Lychee;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ModuleManager _moduleManager;
    private bool _isInitialized;

    public SettingsWindow(SettingsService settings, ModuleManager moduleManager)
    {
        InitializeComponent();
        _settings = settings;
        _moduleManager = moduleManager;

        AlwaysShowCheckBox.IsChecked = _settings.Current.AlwaysShowPanel;
        AlertIpCheckBox.IsChecked = _settings.Current.AlertOnIpChange;
        AlertQueryFailCheckBox.IsChecked = _settings.Current.AlertOnQueryFailure;
        SnapCheckBox.IsChecked = _settings.Current.SnapToEdge;
        BouncyBallCheckBox.IsChecked = _settings.Current.BouncyBall;
        RefreshBallStyles();
        PanelFontComboBox.ItemsSource = Fonts.SystemFontFamilies.OrderBy(family => family.Source).ToList();
        PanelFontComboBox.SelectedValue = _settings.Current.PanelFontFamily;
        LoadAppearanceControls(_settings.Current);
        FrameModeComboBox.SelectedIndex = _settings.Current.FrameMonitoringMode == FrameMonitoringMode.ForegroundApplication
            ? 1
            : 0;
        ModuleToggleList.ItemsSource = _moduleManager.Modules.ToList();
        _isInitialized = true;
        UpdateRestartButtonVisibility();
    }

    private void AlwaysShow_Changed(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.AlwaysShowPanel = AlwaysShowCheckBox.IsChecked == true);
    }

    private void AlertIp_Changed(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.AlertOnIpChange = AlertIpCheckBox.IsChecked == true);
    }

    private void AlertQueryFail_Changed(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.AlertOnQueryFailure = AlertQueryFailCheckBox.IsChecked == true);
    }

    private void Snap_Changed(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.SnapToEdge = SnapCheckBox.IsChecked == true);
    }

    private void BouncyBall_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = BouncyBallCheckBox.IsChecked == true;
        _settings.Update(s => s.BouncyBall = enabled);
        if (enabled) Close();
    }

    private void BallStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || BallStyleComboBox.SelectedValue is not string styleId) return;
        _settings.Update(settings => settings.FloatingBallStyle = styleId);
    }

    private void LoadAppearanceControls(AppSettings settings)
    {
        UseCustomAppearanceCheckBox.IsChecked = settings.UseCustomPanelAppearance;
        BackgroundRedTextBox.Text = settings.PanelBackgroundRed.ToString();
        BackgroundGreenTextBox.Text = settings.PanelBackgroundGreen.ToString();
        BackgroundBlueTextBox.Text = settings.PanelBackgroundBlue.ToString();
        TextRedTextBox.Text = settings.PanelTextRed.ToString();
        TextGreenTextBox.Text = settings.PanelTextGreen.ToString();
        TextBlueTextBox.Text = settings.PanelTextBlue.ToString();
        BackgroundOpacitySlider.Value = Math.Clamp(settings.PanelBackgroundOpacity, 0, 100);
        OpacityValueText.Text = $"{(int)BackgroundOpacitySlider.Value}%";
        RefreshAppearancePreview();
    }

    private void UseCustomAppearance_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _settings.Update(settings => settings.UseCustomPanelAppearance = UseCustomAppearanceCheckBox.IsChecked == true);
        RefreshAppearancePreview();
    }

    private void AppearanceColor_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized ||
            !TryReadRgb(BackgroundRedTextBox, BackgroundGreenTextBox, BackgroundBlueTextBox, out var background) ||
            !TryReadRgb(TextRedTextBox, TextGreenTextBox, TextBlueTextBox, out var text)) return;

        EnableCustomAppearance();
        _settings.Update(settings =>
        {
            settings.PanelBackgroundRed = background.R;
            settings.PanelBackgroundGreen = background.G;
            settings.PanelBackgroundBlue = background.B;
            settings.PanelTextRed = text.R;
            settings.PanelTextGreen = text.G;
            settings.PanelTextBlue = text.B;
        });
        RefreshAppearancePreview();
    }

    private static bool TryReadRgb(
        System.Windows.Controls.TextBox redBox,
        System.Windows.Controls.TextBox greenBox,
        System.Windows.Controls.TextBox blueBox,
        out (int R, int G, int B) rgb)
    {
        rgb = default;
        if (!int.TryParse(redBox.Text, out var red) || red is < 0 or > 255 ||
            !int.TryParse(greenBox.Text, out var green) || green is < 0 or > 255 ||
            !int.TryParse(blueBox.Text, out var blue) || blue is < 0 or > 255)
            return false;

        rgb = (red, green, blue);
        return true;
    }

    private void BackgroundOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        EnableCustomAppearance();
        var opacity = (int)Math.Round(BackgroundOpacitySlider.Value);
        OpacityValueText.Text = $"{opacity}%";
        _settings.Update(settings => settings.PanelBackgroundOpacity = opacity);
        RefreshAppearancePreview();
    }

    private void PanelFont_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || PanelFontComboBox.SelectedValue is not string fontFamily) return;
        EnableCustomAppearance();
        _settings.Update(settings => settings.PanelFontFamily = fontFamily);
        RefreshAppearancePreview();
    }

    private void EnableCustomAppearance()
    {
        if (UseCustomAppearanceCheckBox.IsChecked != true)
            UseCustomAppearanceCheckBox.IsChecked = true;
    }

    private void RefreshAppearancePreview()
    {
        if (!TryReadRgb(BackgroundRedTextBox, BackgroundGreenTextBox, BackgroundBlueTextBox, out var background) ||
            !TryReadRgb(TextRedTextBox, TextGreenTextBox, TextBlueTextBox, out var text)) return;

        var alpha = (byte)Math.Round(Math.Clamp(BackgroundOpacitySlider.Value, 0, 100) * 2.55);
        AppearancePreviewBorder.Background = new SolidColorBrush(Color.FromArgb(
            alpha, (byte)background.R, (byte)background.G, (byte)background.B));
        AppearancePreviewText.Foreground = new SolidColorBrush(Color.FromRgb(
            (byte)text.R, (byte)text.G, (byte)text.B));
        try
        {
            var family = PanelFontComboBox.SelectedValue as string ?? PanelFontComboBox.Text;
            AppearancePreviewText.FontFamily = new System.Windows.Media.FontFamily(
                string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family);
        }
        catch (ArgumentException)
        {
            AppearancePreviewText.FontFamily = System.Windows.SystemFonts.MessageFontFamily;
        }
    }

    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        _isInitialized = false;
        _settings.Update(settings =>
        {
            settings.UseCustomPanelAppearance = false;
            settings.PanelBackgroundRed = 30;
            settings.PanelBackgroundGreen = 30;
            settings.PanelBackgroundBlue = 46;
            settings.PanelBackgroundOpacity = 90;
            settings.PanelTextRed = 255;
            settings.PanelTextGreen = 255;
            settings.PanelTextBlue = 255;
            settings.PanelFontFamily = "Segoe UI";
        });
        PanelFontComboBox.SelectedValue = "Segoe UI";
        LoadAppearanceControls(_settings.Current);
        _isInitialized = true;
    }

    private void ImportBallImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Win32OpenFileDialog
        {
            Title = "Import floating ball image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lychee");
            Directory.CreateDirectory(directory);
            var extension = Path.GetExtension(dialog.FileName);
            var destination = Path.Combine(directory, $"custom-ball-{Guid.NewGuid():N}{extension}");
            File.Copy(dialog.FileName, destination);
            _settings.Update(settings =>
            {
                settings.CustomBallImagePath = destination;
                settings.FloatingBallStyle = FloatingBallStyles.CustomId;
            });
            RefreshBallStyles();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to import image:\n{ex.Message}", "Import failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshBallStyles()
    {
        var customPath = _settings.Current.CustomBallImagePath;
        BallStyleComboBox.ItemsSource = FloatingBallStyles.GetAvailable(customPath);
        BallStyleComboBox.SelectedValue = FloatingBallStyles.Get(
            _settings.Current.FloatingBallStyle, customPath).Id;
    }

    private void FrameMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || FrameModeComboBox.SelectedItem is not ComboBoxItem item) return;

        var mode = string.Equals(
            item.Tag?.ToString(),
            nameof(FrameMonitoringMode.ForegroundApplication),
            StringComparison.Ordinal)
            ? FrameMonitoringMode.ForegroundApplication
            : FrameMonitoringMode.DesktopOutput;
        _settings.Update(settings => settings.FrameMonitoringMode = mode);
        UpdateRestartButtonVisibility();
    }

    private void RestartElevated_Click(object sender, RoutedEventArgs e)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.Current.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user cancelled the UAC prompt; keep the current instance open.
        }
    }

    private void UpdateRestartButtonVisibility()
    {
        var applicationMode = FrameModeComboBox.SelectedIndex == 1;
        RestartElevatedButton.Visibility = applicationMode && !IsRunningAsAdministrator()
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void ModuleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        if (cb.DataContext is not IInfoModule module) return;

        _moduleManager.SetEnabled(module.Id, cb.IsChecked == true);
    }
}
