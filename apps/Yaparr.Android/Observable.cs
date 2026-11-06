using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Yaparr.AndroidApp;

/// <summary>Something the screen shows that can change: it says so, and the screen follows.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
