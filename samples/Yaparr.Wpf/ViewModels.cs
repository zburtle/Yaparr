using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Yaparr.Wpf;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record MessageVm(DateTimeOffset Time, string Sender, string Text, bool IsCatchUp = false, bool IsSystem = false, bool IsMine = false, string? Id = null)
{
    public string Display => IsSystem ? Text : $"{Time.LocalDateTime:HH:mm}  {Sender}: {Text}";
    public string Kind => IsSystem ? "system" : IsCatchUp ? "missed" : IsMine ? "mine" : "normal";

    public static MessageVm System(string text) => new(DateTimeOffset.Now, "", text, IsSystem: true);
}

public sealed record PeerVm(string Id, string Name, bool Online, DateTimeOffset LastSeen, bool IsRelayed = false)
{
    public string Display => IsRelayed ? Name + " (relayed)" : Name;
    public string Tip => Online ? Id : $"{Id}\nLast seen {LastSeen.LocalDateTime:g}";
}

public sealed class ConversationVm : Observable
{
    private int _unread;
    private readonly HashSet<string> _ids = new();

    public ConversationVm(string title, bool isRoom, Func<string, Task> send)
    {
        Title = title;
        IsRoom = isRoom;
        Send = send;
    }

    public string Title { get; }
    public bool IsRoom { get; }
    public Func<string, Task> Send { get; }

    public string? Invite { get; init; }
    public bool HasInvite => Invite != null;

    public ObservableCollection<MessageVm> Messages { get; } = new();

    public int Unread
    {
        get => _unread;
        set { _unread = value; Raise(); Raise(nameof(Label)); }
    }

    public string Label => Unread > 0 ? $"{Title}  ({Unread})" : Title;

    public void Add(MessageVm message)
    {
        if (message.Id != null && !_ids.Add(message.Id)) return;
        int i = Messages.Count;
        while (i > 0 && Messages[i - 1].Time > message.Time && !Messages[i - 1].IsSystem) i--;
        Messages.Insert(i, message);
    }
}
