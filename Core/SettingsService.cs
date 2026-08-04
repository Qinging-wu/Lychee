using System.IO;
using System.Text.Json;

namespace Lychee.Core;

public sealed class AppSettings
{
    public bool AlwaysShowPanel { get; set; } = false;
    public bool AlertOnIpChange { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool SnapToEdge { get; set; } = false;
    public int FloatingBallSize { get; set; } = 56;
    public FrameMonitoringMode FrameMonitoringMode { get; set; } = FrameMonitoringMode.DesktopOutput;
    public Dictionary<string, bool> ModuleEnabled { get; set; } = new();
}

public sealed class SettingsService
{
    private readonly string _path;
    private AppSettings _current;
    private readonly object _lock = new();

    public AppSettings Current
    {
        get { lock (_lock) return _current; }
    }

    public event EventHandler? Changed;

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lychee");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        _current = Load();
    }

    public bool GetModuleEnabled(string id, bool defaultValue = true)
    {
        lock (_lock)
        {
            return _current.ModuleEnabled.TryGetValue(id, out var v) ? v : defaultValue;
        }
    }

    public void SetModuleEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            _current.ModuleEnabled[id] = enabled;
        }
        Save();
    }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_lock)
        {
            mutate(_current);
        }
        Save();
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void Save()
    {
        try
        {
            AppSettings snapshot;
            lock (_lock) snapshot = _current;
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }
}
