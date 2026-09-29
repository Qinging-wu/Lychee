using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
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
