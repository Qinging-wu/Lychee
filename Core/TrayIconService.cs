using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Lychee.Core;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    public event EventHandler? ShowHideRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadEmbeddedIcon(),
            Text = "Lychee",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.DoubleClick += (s, e) => ShowHideRequested?.Invoke(this, EventArgs.Empty);
    }

    private static Icon LoadEmbeddedIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("icon.ico", StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                using var ms = asm.GetManifestResourceStream(resName);
                if (ms != null) return new Icon(ms);
            }
        }
        catch { }

        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/icon.png"));
            if (streamInfo != null)
            {
                using var s = streamInfo.Stream;
                using var bmp = new Bitmap(s);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
        catch { }

        try { return SystemIcons.Application; }
        catch { return new Icon(System.Drawing.SystemIcons.GetStockIcon(StockIconId.Application), 16, 16); }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("Show / Hide");
        showItem.Click += (s, e) => ShowHideRequested?.Invoke(this, EventArgs.Empty);

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (s, e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("Quit Lychee");
        exitItem.Click += (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.AddRange(new ToolStripItem[]
        {
            showItem,
            settingsItem,
            new ToolStripSeparator(),
            exitItem
        });
        return menu;
    }

    public void ShowBalloon(string title, string message,
        ToolTipIcon icon = ToolTipIcon.Info, int timeout = 8000)
    {
        _notifyIcon.ShowBalloonTip(timeout, title, message, icon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
