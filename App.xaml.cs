using System.Windows;

namespace Lychee;

public partial class App : Application
{
    private MainWindow? _mainWindow;

    private void App_Startup(object sender, StartupEventArgs e)
    {
        // WPF reads the legacy WINDIR variable while initializing its font cache.
        // Some launcher environments provide SystemRoot but omit WINDIR.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windowsDirectory))
            {
                Environment.SetEnvironmentVariable("windir", windowsDirectory);
            }
        }

        _mainWindow = new MainWindow();
        _mainWindow.Show();
    }
}
