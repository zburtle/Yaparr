using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Yaparr.Chat;

namespace Yaparr.Desktop;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Stable colours and initials for people and rooms, so the same name always looks the same.</summary>
public static class Avatars
{
    private static readonly IBrush[] Palette =
    {
        new SolidColorBrush(Color.Parse("#4F6BED")), new SolidColorBrush(Color.Parse("#E5484D")),
        new SolidColorBrush(Color.Parse("#12A594")), new SolidColorBrush(Color.Parse("#F76B15")),
        new SolidColorBrush(Color.Parse("#8E4EC6")), new SolidColorBrush(Color.Parse("#0091FF")),
        new SolidColorBrush(Color.Parse("#E93D82")), new SolidColorBrush(Color.Parse("#46A758")),
    };

    public static IBrush BrushFor(string name)
    {
        uint hash = 2166136261;
        foreach (var c in name) hash = (hash ^ c) * 16777619;
        return Palette[hash % Palette.Length];
    }

    public static string Initial(string name)
    {
        foreach (var c in name) if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        return "?";
    }
}

public sealed class MessageVm : Observable
{
    private DeliveryStatus? _delivery;

    public MessageVm(DateTimeOffset time, string sender, string text, bool isCatchUp = false, bool isSystem = false, bool isMine = false, string? id = null)
    {
        Time = time; Sender = sender; Text = text; IsCatchUp = isCatchUp; IsSystem = isSystem; IsMine = isMine; Id = id;
    }

    public DateTimeOffset Time { get; }
    public string Sender { get; }
    public string Text { get; }
    public bool IsCatchUp { get; }
    public bool IsSystem { get; }
    public bool IsMine { get; }
    public string? Id { get; }

    public DeliveryStatus? Delivery
    {
        get => _delivery;
        set { _delivery = value; Raise(); Raise(nameof(Display)); Raise(nameof(Kind)); Raise(nameof(CanRetry)); Raise(nameof(Status)); Raise(nameof(HasStatus)); Raise(nameof(IsFailed)); }
    }

    public bool CanRetry => IsMine && Id != null && Delivery is { State: DeliveryState.NotDelivered or DeliveryState.PartlyDelivered };

    private string Suffix => !IsMine || Delivery is not { } d ? "" : d.State switch
    {
        DeliveryState.Sending => "   …",
        DeliveryState.Delivered => "   ✓",
        DeliveryState.PartlyDelivered => $"   ✓ {d.Delivered} of {d.Total} · right-click to retry",
        DeliveryState.NotDelivered => "   ⚠ not delivered · right-click to retry",
        DeliveryState.Queued => "   (nobody online yet; it will be handed over when someone connects)",
        _ => "",
    };

    public string TimeText => Time.LocalDateTime.ToString("HH:mm");
    public bool ShowSender => !IsMine && !IsSystem;
    public bool IsOther => !IsMine && !IsSystem;
    public IBrush SenderBrush => Avatars.BrushFor(Sender);

    /// <summary>Delivery line under one of my messages; empty once it is simply delivered.</summary>
    public string Status => !IsMine || Delivery is not { } d ? "" : d.State switch
    {
        DeliveryState.Sending => "Sending…",
        DeliveryState.Delivered => "Delivered",
        DeliveryState.PartlyDelivered => $"Delivered to {d.Delivered} of {d.Total} · right-click to retry",
        DeliveryState.NotDelivered => "Not delivered · right-click to retry",
        DeliveryState.Queued => "Nobody online yet; it will be handed over when someone connects",
        _ => "",
    };
    public bool HasStatus => Status.Length > 0;
    public bool IsFailed => IsMine && Delivery is { State: DeliveryState.NotDelivered };

    public string Display => IsSystem ? Text : $"{Time.LocalDateTime:HH:mm}  {Sender}: {Text}{Suffix}";
    public string Kind => IsSystem ? "system" : IsCatchUp ? "missed" : CanRetry ? "failed" : IsMine ? "mine" : "normal";

    public static MessageVm System(string text) => new(DateTimeOffset.Now, "", text, isSystem: true);
}

public sealed record PeerVm(string Id, string Name, bool Online, DateTimeOffset LastSeen, bool IsRelayed = false, PeerLink Link = PeerLink.Offline)
{
    public string Display => Name;
    public string Initial => Avatars.Initial(Name);
    public IBrush Avatar => Avatars.BrushFor(Name);
    public bool Warn => Link is PeerLink.NotResponding or PeerLink.Reconnecting;

    public string StateText => Link switch
    {
        PeerLink.Direct => "online",
        PeerLink.Relayed => "relayed",
        PeerLink.Reconnecting => "reconnecting…",
        PeerLink.NotResponding => "not responding",
        _ => Online ? (IsRelayed ? "relayed" : "online") : "offline",
    };

    public string Tip => (Link switch
    {
        PeerLink.Direct => "Connected directly.",
        PeerLink.Relayed => "Connected through another member who forwards messages.",
        PeerLink.Reconnecting => "The connection dropped. Yaparr is trying again.",
        PeerLink.NotResponding => "Connected, but the last message was not confirmed.",
        _ => Online ? "Online." : $"Last seen {LastSeen.LocalDateTime:g}",
    }) + "\n" + Id;
}

public sealed class ConversationVm : Observable
{
    private int _unread;
    private string _title;
    private readonly HashSet<string> _ids = new();

    private readonly Dictionary<string, DeliveryStatus> _delivery = new();

    public ConversationVm(string title, bool isRoom, Func<string, Task<string>> send, Action<string>? retry = null)
    {
        _title = title;
        IsRoom = isRoom;
        Send = send;
        Retry = retry;
    }

    public string Title
    {
        get => _title;
        set { _title = value; Raise(); Raise(nameof(Label)); Raise(nameof(Name)); Raise(nameof(Initial)); Raise(nameof(Avatar)); }
    }
    public bool IsRoom { get; }

    /// <summary>The title without the "(private) " and "(direct)" decorations used in headers.</summary>
    public string Name
    {
        get
        {
            var name = _title;
            if (name.StartsWith("(private) ")) name = name["(private) ".Length..];
            if (name.EndsWith(" (direct)")) name = name[..^" (direct)".Length];
            return name;
        }
    }
    public string Subtitle => IsRoom ? "Private room" : "Direct chat";
    public string Initial => Avatars.Initial(Name);
    public IBrush Avatar => Avatars.BrushFor(Name);
    public bool HasUnread => _unread > 0;
    public Func<string, Task<string>> Send { get; }
    public Action<string>? Retry { get; }

    public void ApplyDelivery(DeliveryStatus status)
    {
        _delivery[status.MessageId] = status;
        foreach (var m in Messages)
            if (m.Id == status.MessageId) m.Delivery = status;
    }

    public string? Invite { get; init; }
    public bool HasInvite => Invite != null;

    public ObservableCollection<MessageVm> Messages { get; } = new();

    public int Unread
    {
        get => _unread;
        set { _unread = value; Raise(); Raise(nameof(Label)); Raise(nameof(HasUnread)); }
    }

    public string Label => Unread > 0 ? $"{Title}  ({Unread})" : Title;

    public void Add(MessageVm message)
    {
        if (message.Id != null && !_ids.Add(message.Id)) return;
        if (message.Id != null && _delivery.TryGetValue(message.Id, out var status)) message.Delivery = status;
        int i = Messages.Count;
        while (i > 0 && Messages[i - 1].Time > message.Time && !Messages[i - 1].IsSystem) i--;
        Messages.Insert(i, message);
    }
}
