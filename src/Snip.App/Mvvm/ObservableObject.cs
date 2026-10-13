using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Snip.App.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(propertyName); return true;
    }
}
