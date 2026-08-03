using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Lychee.Core;

public interface IInfoModule : IDisposable
{
    string Id { get; }
    string DisplayName { get; }
    string Icon { get; }
    bool IsEnabled { get; set; }
    string CurrentValue { get; }
    string? Detail { get; }
    event EventHandler? ValueChanged;
    void Start();
    void Stop();
    Task StopAsync();
}

public abstract class InfoModuleBase : IInfoModule, INotifyPropertyChanged
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public virtual string Icon => "•";

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    private string _currentValue = "";
    public string CurrentValue
    {
        get => _currentValue;
        protected set => SetField(ref _currentValue, value);
    }

    private string? _detail;
    public string? Detail
    {
        get => _detail;
        protected set => SetField(ref _detail, value);
    }

    private double? _progress;
    public double? Progress
    {
        get => _progress;
        protected set
        {
            if (EqualityComparer<double?>.Default.Equals(_progress, value)) return;
            var wasNull = _progress == null;
            _progress = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
            if (wasNull != (value == null))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasProgress)));
        }
    }

    public bool HasProgress => _progress.HasValue;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ValueChanged;

    public abstract void Start();
    public abstract void Stop();

    public virtual Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }

    protected void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(CurrentValue))
        {
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public virtual void Dispose() => Stop();
}
