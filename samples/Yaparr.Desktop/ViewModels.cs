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

    private byte[]? _image;
    private AttachmentState? _state;
    private long _received;

    public MessageVm(DateTimeOffset time, string sender, string text, bool isCatchUp = false, bool isSystem = false, bool isMine = false, string? id = null, Attachment? attachment = null)
    {
        Time = time; Sender = sender; Text = text; IsCatchUp = isCatchUp; IsSystem = isSystem; IsMine = isMine; Id = id; Attachment = attachment;
    }

    public Attachment? Attachment { get; }
    public ChatMessage? Source { get; init; }
    public bool HasAttachment => Attachment != null;
    public bool HasText => Text.Length > 0;
    public byte[]? Image => _image;
    public byte[]? Thumbnail => Attachment?.Thumbnail;
    public int PixelWidth => Attachment?.Width ?? 0;
    public int PixelHeight => Attachment?.Height ?? 0;
    public bool IsFetching => _image == null && _state == AttachmentState.Fetching;
    public double ProgressPercent => Attachment is { Size: > 0 } a ? Math.Min(100, _received * 100.0 / a.Size) : 0;
    public bool NeedsAction => _image == null && _state is AttachmentState.NeedsConfirmation or AttachmentState.Unavailable;
    public string ActionText => _state == AttachmentState.NeedsConfirmation
        ? $"Download · {FormatSize(Attachment?.Size ?? 0)}"
        : "Not available yet · Try again";

    public void SetImage(byte[] data) { _image = data; _state = AttachmentState.Ready; RaiseAttachment(); }

    public void Apply(AttachmentUpdate update)
    {
        if (_image != null) return;
        _state = update.State;
        _received = update.Received;
        RaiseAttachment();
    }

    private void RaiseAttachment()
    {
        Raise(nameof(Image)); Raise(nameof(IsFetching)); Raise(nameof(ProgressPercent)); Raise(nameof(NeedsAction)); Raise(nameof(ActionText));
    }

    public static string FormatSize(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

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
    public string FullTime => Time.LocalDateTime.ToString("dddd, MMMM d, yyyy  HH:mm:ss");

    private bool _showDay;

    /// <summary>True when this is the first message of its day, so a date line is shown above it.</summary>
    public bool ShowDay
    {
        get => _showDay;
        set { if (_showDay != value) { _showDay = value; Raise(); } }
    }

    public string DayLabel
    {
        get
        {
            var day = Time.LocalDateTime.Date;
            var today = DateTime.Now.Date;
            if (day == today) return "Today";
            if (day == today.AddDays(-1)) return "Yesterday";
            return day.Year == today.Year ? day.ToString("dddd, MMMM d") : day.ToString("dddd, MMMM d, yyyy");
        }
    }
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
    public bool IsDirect => !IsRoom;

    /// <summary>Full peer ID of the other person in a direct chat.</summary>
    public string? PeerId { get; init; }
    public bool HasPeerId => PeerId != null;

    private string _membersText = "";
    public string MembersText
    {
        get => _membersText;
        set { if (_membersText == value) return; _membersText = value; Raise(); }
    }
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

    // A date line goes above the first message of each day; only the inserted message and the one after it can change.
    private void MarkDays(int index)
    {
        for (int j = index; j <= index + 1 && j < Messages.Count; j++)
            Messages[j].ShowDay = j == 0 || Messages[j - 1].Time.LocalDateTime.Date != Messages[j].Time.LocalDateTime.Date;
    }

    public void Add(MessageVm message)
    {
        if (message.Id != null && !_ids.Add(message.Id)) return;
        if (message.Id != null && _delivery.TryGetValue(message.Id, out var status)) message.Delivery = status;
        int i = Messages.Count;
        while (i > 0 && Messages[i - 1].Time > message.Time) i--;
        Messages.Insert(i, message);
        MarkDays(i);
    }
}
