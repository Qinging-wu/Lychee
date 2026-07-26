using System.Windows;
using System.Windows.Controls;
using Lychee.Core;

namespace Lychee;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ModuleManager _moduleManager;

    public SettingsWindow(SettingsService settings, ModuleManager moduleManager)
    {
        InitializeComponent();
        _settings = settings;
        _moduleManager = moduleManager;

        AlwaysShowCheckBox.IsChecked = _settings.Current.AlwaysShowPanel;
        AlertIpCheckBox.IsChecked = _settings.Current.AlertOnIpChange;
        ModuleToggleList.ItemsSource = _moduleManager.Modules.ToList();
    }

    private void AlwaysShow_Changed(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.AlwaysShowPanel = AlwaysShowCheckBox.IsChecked == true);
    }

    private void AlertIp_Changed(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.AlertOnIpChange = AlertIpCheckBox.IsChecked == true);
    }

    private void ModuleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        if (cb.DataContext is not IInfoModule module) return;

        _moduleManager.SetEnabled(module.Id, cb.IsChecked == true);
    }
}
