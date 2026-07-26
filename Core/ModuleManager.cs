namespace Lychee.Core;

public sealed class ModuleManager : IDisposable
{
    private readonly List<IInfoModule> _modules = new();
    private readonly SettingsService _settings;
    private bool _disposed;

    public IReadOnlyList<IInfoModule> Modules => _modules;

    public event EventHandler? ValueChanged;

    public ModuleManager(SettingsService settings)
    {
        _settings = settings;
    }

    public void RegisterModule(IInfoModule module)
    {
        module.IsEnabled = _settings.GetModuleEnabled(module.Id, defaultValue: true);
        module.ValueChanged += OnModuleValueChanged;
        _modules.Add(module);
    }

    public void StartAll()
    {
        foreach (var m in _modules)
        {
            if (m.IsEnabled) m.Start();
        }
    }

    public void StopAll()
    {
        foreach (var m in _modules) m.Stop();
    }

    public void SetEnabled(string moduleId, bool enabled)
    {
        var m = _modules.FirstOrDefault(x => x.Id == moduleId);
        if (m == null) return;
        m.IsEnabled = enabled;
        _settings.SetModuleEnabled(moduleId, enabled);
        if (enabled) m.Start(); else m.Stop();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    public IInfoModule? Get(string id) => _modules.FirstOrDefault(x => x.Id == id);

    private void OnModuleValueChanged(object? sender, EventArgs e)
    {
        ValueChanged?.Invoke(sender, e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll();
        foreach (var m in _modules) m.Dispose();
    }
}
