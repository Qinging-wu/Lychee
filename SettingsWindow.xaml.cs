using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
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
