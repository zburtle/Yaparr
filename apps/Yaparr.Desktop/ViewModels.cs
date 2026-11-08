using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Yaparr.Chat;
using Yaparr.Identity;

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
    private bool _isMine;
    private string _deviceNote = "";

    public MessageVm(DateTimeOffset time, string sender, string text, bool isCatchUp = false, bool isSystem = false, bool isMine = false, string? id = null, Attachment? attachment = null)
    {
        Time = time; Sender = sender; Text = text; IsCatchUp = isCatchUp; IsSystem = isSystem; _isMine = isMine; Id = id; Attachment = attachment;
    }

    public Attachment? Attachment { get; }
    public ChatMessage? Source { get; init; }

    // ---- reactions ----

    private string _reactionSignature = "";

    /// <summary>The emoji on this message, with how many put them there; the last one, when there are any, is the button to add another.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ReactionChipVm> Reactions { get; } = new();

    public bool HasReactions => Reactions.Count > 0;

    /// <summary>A message that is not a notice can be reacted to (it has a key to point at).</summary>
    public bool CanReact => Key != null && !IsSystem;

    /// <summary>Where the button that opens the picker sits: on the outer top corner of the bubble.</summary>
    public Thickness ReactButtonMargin => IsMine ? new Thickness(0, -10, -8, 0) : new Thickness(-8, -10, 0, 0);

    /// <summary>The reactions sit on the same side as the bubble.</summary>
    public HorizontalAlignment ReactionAlign => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /// <summary>Shows what the message's reactions come to now. Nothing changes on screen when they are as they were.</summary>
    public void SetReactions(IReadOnlyList<ReactionSummary> reactions, PeerId me, Func<PeerId, string> nameOf)
    {
        var signature = string.Join("|", reactions.Select(r => r.Emoji + ":" + string.Join(",", r.Accounts.Select(a => a.Value)))) + "/" + me.Value;
        if (signature == _reactionSignature) return;
        _reactionSignature = signature;
        Reactions.Clear();
        foreach (var r in reactions)
        {
            var names = r.Accounts.Select(nameOf).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            var who = names.Count <= 8 ? string.Join(", ", names) : string.Join(", ", names.Take(8)) + " and " + (names.Count - 8) + " more";
            Reactions.Add(new ReactionChipVm(this, r.Emoji, r.Count, r.Accounts.Contains(me), who + "\n" + (EmojiData.Find(r.Emoji)?.Name ?? "")));
        }
        if (reactions.Count > 0) Reactions.Add(ReactionChipVm.Add(this));
        Raise(nameof(HasReactions));
    }

    /// <summary>The card the sender attached for a link in the words, shown below them.</summary>
    public LinkPreview? Card { get; init; }
    public bool HasPreview => Card != null;
    public string PreviewTitle => Card?.Title is { Length: > 0 } t ? t : Card?.Host ?? "";
    public string PreviewDescription => Card?.Description ?? "";
    public bool HasPreviewDescription => PreviewDescription.Length > 0;
    public string PreviewHost => Card?.Host ?? "";
    public bool HasPreviewImage => PreviewImage != null;

    private Bitmap? _previewImage;
    private bool _previewImageTried;

    /// <summary>The small picture of the card, once it has been read from the message.</summary>
    public Bitmap? PreviewImage
    {
        get
        {
            if (_previewImageTried || Card?.Thumbnail is not { Length: > 0 } bytes) return _previewImage;
            _previewImageTried = true;
            try { _previewImage = new Bitmap(new MemoryStream(bytes)); } catch (Exception) { }
            return _previewImage;
        }
    }

    /// <summary>The text channel of a room message; null for direct messages and notices.</summary>
    public string? Channel { get; init; }
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
    public bool IsMine => _isMine;
    public string? Id { get; }

    /// <summary>
    /// A message shows as yours once it is known to come from one of your own devices. That can be learned after the message arrived
    /// (the proof that a device is yours travels with its presence, not with its messages), so it is set later.
    /// </summary>
    public void ClaimAsMine(string deviceNote)
    {
        if (_isMine) return;
        _isMine = true;
        _deviceNote = deviceNote;
        foreach (var name in new[] { nameof(IsMine), nameof(ReactionAlign), nameof(ReactButtonMargin), nameof(ShowSender), nameof(IsOther), nameof(Kind), nameof(Status), nameof(HasStatus), nameof(IsFailed), nameof(CanRetry), nameof(DeviceNote), nameof(TimeText), nameof(Display), nameof(Highlight) })
            Raise(name);
    }

    /// <summary>What names this message, for a reply to point at (<see cref="ChatMessage.Key"/>). Null for notices.</summary>
    public string? Key { get; init; }

    /// <summary>The key of the message this one answers, if it answers one.</summary>
    public string? ReplyKey { get; init; }

    /// <summary>The people this message names.</summary>
    public IReadOnlyList<Mention> Mentions { get; init; } = Array.Empty<Mention>();

    /// <summary>It names you.</summary>
    public bool MentionsMe { get; init; }

    private IReadOnlyList<string>? _marks;

    /// <summary>The words to stand out in the text: @ and the name, for each person named.</summary>
    public IReadOnlyList<string> MentionMarks => _marks ??= Mentions.Select(m => m.Tag).ToList();

    private string _quoteSender = "", _quoteText = "";
    private bool _replyToMe;

    public bool HasQuote => ReplyKey != null;
    public string QuoteSender { get => _quoteSender; private set { _quoteSender = value; Raise(); } }
    public string QuoteText { get => _quoteText; private set { _quoteText = value; Raise(); } }

    /// <summary>It answers a message of yours.</summary>
    public bool ReplyToMe { get => _replyToMe; private set { if (_replyToMe == value) return; _replyToMe = value; Raise(); Raise(nameof(Highlight)); } }

    /// <summary>Worth a second look: it names you or answers you (not something you wrote yourself).</summary>
    public bool Highlight => !IsMine && (MentionsMe || ReplyToMe);

    /// <summary>Fills in what the reply quotes, once the message it answers is known.</summary>
    public void SetQuote(string sender, string text, bool answersMe)
    {
        QuoteSender = sender;
        QuoteText = text;
        ReplyToMe = answersMe;
    }

    /// <summary>A short form to show in a quote or above the composer.</summary>
    public string Preview
    {
        get
        {
            if (Attachment != null) return HasText ? "📷 " + Flatten(Text) : "📷 Picture";
            return Flatten(Text);
        }
    }

    private static string Flatten(string text)
    {
        var flat = string.Join(' ', text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length <= 140 ? flat : flat[..140] + "…";
    }

    private string _deliveryDetail = "";

    /// <summary>Who has the message and who is still to get it, for the tooltip on the status line.</summary>
    public string DeliveryDetail
    {
        get => _deliveryDetail;
        set { if (_deliveryDetail == value) return; _deliveryDetail = value; Raise(); }
    }

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

    public string TimeText => Time.LocalDateTime.ToString("HH:mm") + (DeviceNote.Length > 0 ? "  ·  " + DeviceNote : "");

    /// <summary>For a message you sent from another of your devices: which one.</summary>
    public string DeviceNote { get => _deviceNote; init => _deviceNote = value; }
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

public sealed record PeerVm(string Id, string Name, bool Online, DateTimeOffset LastSeen, bool IsRelayed = false, PeerLink Link = PeerLink.Offline, bool Blocked = false, RoomRole Role = RoomRole.Member, bool BannedHere = false)
{
    public string Display => BannedHere ? Name + "  (banned)" : Role switch { RoomRole.Owner => Name + "  (owner)", RoomRole.Admin => Name + "  (admin)", _ => Name };
    public string Initial => Avatars.Initial(Name);
    public IBrush Avatar => Avatars.BrushFor(Name);
    public bool Warn => Link is PeerLink.NotResponding or PeerLink.Reconnecting;

    public string StateText => BannedHere ? "banned from this room" : Blocked ? "blocked" : Link switch
    {
        PeerLink.Direct => "direct",
        PeerLink.ViaServer => "via TURN",
        PeerLink.Relayed => "via member",
        PeerLink.Reconnecting => "reconnecting…",
        PeerLink.NotResponding => "not responding",
        _ => Online ? (IsRelayed ? "via member" : "online") : "offline",
    };

    public string Tip => (Blocked ? "Blocked. You see nothing from them. Right-click to unblock." : Link switch
    {
        PeerLink.Direct => "Connected directly to them.",
        PeerLink.ViaServer => "Connected through the shared relay server (TURN). It works, but its bandwidth is limited, so Yaparr keeps looking for a better path.",
        PeerLink.Relayed => "Connected through another member who forwards your traffic. They cannot read it.",
        PeerLink.Reconnecting => "The connection dropped. Yaparr is trying again.",
        PeerLink.NotResponding => "Connected, but the last message was not confirmed.",
        _ => Online ? "Online." : $"Last seen {LastSeen.LocalDateTime:g}",
    }) + "\n" + Id;
}

/// <summary>One emoji under a message: the emoji, how many put it there, whether you did, and who (for the tooltip). The button that adds another has <see cref="IsAdd"/>.</summary>
public sealed class ReactionChipVm : Observable
{
    public ReactionChipVm(MessageVm owner, string emoji, int count, bool mine, string tooltip)
    {
        Owner = owner; Emoji = emoji; Count = count; Mine = mine; Tooltip = tooltip;
    }

    public static ReactionChipVm Add(MessageVm owner) => new(owner, "\u263A\uFE0F", 0, false, "Add a reaction") { IsAdd = true };

    public MessageVm Owner { get; }
    public string Emoji { get; }
    public int Count { get; }
    public bool Mine { get; }
    public string Tooltip { get; }
    public bool IsAdd { get; private init; }
    public bool ShowCount => !IsAdd;
    public bool ShowEmoji => !IsAdd;
    public string CountText => Count.ToString();
}

public sealed class ConversationVm : Observable
{
    /// <summary>Counts the reactions of this conversation as they stand now (the room's or the direct chat's).</summary>
    public Func<ReactionSet>? ReactionSource { get; set; }

    /// <summary>Puts an emoji on a message or takes it off: the message's key, the emoji, whether to put it on, and the message's channel.</summary>
    public Func<string, string, bool, string?, Task>? React { get; set; }

    private int _unread;
    private string _title;
    private readonly HashSet<string> _ids = new();

    private readonly Dictionary<string, DeliveryStatus> _delivery = new();
    private readonly Dictionary<string, string> _deliveryDetail = new();

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

    /// <summary>The title without the "(direct)" decoration used in headers.</summary>
    public string Name
    {
        get
        {
            var name = _title;
            if (name.EndsWith(" (direct)")) name = name[..^" (direct)".Length];
            return name;
        }
    }
    public string Subtitle => IsRoom ? "Room" : "Direct chat";

    private string _syncText = "";
    private bool _syncing;

    /// <summary>What a room is doing to get going ("Finding members…"); empty when it is ready.</summary>
    public string SyncText
    {
        get => _syncText;
        private set { if (_syncText == value) return; _syncText = value; Raise(); Raise(nameof(HasSyncText)); Raise(nameof(ShowSubtitle)); }
    }

    public bool HasSyncText => _syncText.Length > 0;

    public bool ShowSubtitle => _syncText.Length == 0;

    /// <summary>The room is working on it, so the list shows a progress bar.</summary>
    public bool IsSyncing
    {
        get => _syncing;
        private set { if (_syncing == value) return; _syncing = value; Raise(); }
    }

    /// <summary>Where the room is in getting going; call now and then.</summary>
    public RoomSyncState UpdateSync(ChatRoom room)
    {
        var state = room.SyncState;
        SyncText = SyncStatus.RoomText(state);
        IsSyncing = SyncStatus.IsBusy(state);
        return state;
    }
    public bool IsDirect => !IsRoom;

    /// <summary>Full peer ID of the other person in a direct chat.</summary>
    public string? PeerId { get; init; }
    public bool HasPeerId => PeerId != null;

    // ---- voice channel ----

    public ObservableCollection<VoiceMemberVm> VoiceMembers { get; } = new();
    public bool HasVoiceMembers => VoiceMembers.Count > 0;
    // Rooms with channels join voice by clicking a voice channel; the button is only for older rooms that have just the one.
    private string _pushToTalkHint = "";

    /// <summary>Tells the person in voice which key to hold, or nothing when push-to-talk is off.</summary>
    public string PushToTalkHint
    {
        get => _pushToTalkHint;
        set { if (_pushToTalkHint == value) return; _pushToTalkHint = value; Raise(); Raise(nameof(HasPushToTalkHint)); }
    }

    public bool HasPushToTalkHint => _pushToTalkHint.Length > 0;

    public bool CanJoinVoice => IsRoom && !_inVoice && !_showChannels;
    public bool CanUseVoiceControls => _inVoice;

    private bool _inVoice, _muted, _deafened;

    public bool InVoice
    {
        get => _inVoice;
        set { if (_inVoice == value) return; _inVoice = value; Raise(); Raise(nameof(CanJoinVoice)); Raise(nameof(CanUseVoiceControls)); Raise(nameof(CanShare)); Raise(nameof(ShareTip)); Raise(nameof(CanWatchShares)); Raise(nameof(NeedsVoiceToWatch)); }
    }

    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; Raise(); Raise(nameof(MuteText)); Raise(nameof(MuteGlyph)); Raise(nameof(MuteTip)); }
    }

    public bool Deafened
    {
        get => _deafened;
        set { if (_deafened == value) return; _deafened = value; Raise(); Raise(nameof(DeafenText)); Raise(nameof(DeafenTip)); }
    }

    // ---- screen sharing ----

    public ObservableCollection<ShareVm> Shares { get; } = new();
    public bool HasShares => Shares.Count > 0;

    /// <summary>Shares are listed, and can be watched, only while you are in the room's voice channel.</summary>
    public bool CanWatchShares => HasShares && _inVoice;
    public bool NeedsVoiceToWatch => HasShares && !_inVoice;

    public void SharesChanged() { Raise(nameof(HasShares)); Raise(nameof(CanWatchShares)); Raise(nameof(NeedsVoiceToWatch)); Raise(nameof(ShowVoiceRow)); }

    private bool _sharing;
    public bool IsSharing
    {
        get => _sharing;
        set { if (_sharing == value) return; _sharing = value; Raise(); Raise(nameof(CanShare)); Raise(nameof(CanStopSharing)); }
    }

    /// <summary>Sharing a screen needs being in the room's voice channel, and works on Windows only for now; watching works everywhere.</summary>
    public bool CanShare => IsRoom && (_inVoice || _inVoiceElsewhere) && !_sharing && Yaparr.Screen.ScreenCapture.Supported;

    private bool _inVoiceElsewhere;

    /// <summary>Another of your devices is in this room's call and this one is not: this one can still share its screen into that call.</summary>
    public bool InVoiceElsewhere
    {
        get => _inVoiceElsewhere;
        set { if (_inVoiceElsewhere == value) return; _inVoiceElsewhere = value; Raise(); Raise(nameof(CanShare)); Raise(nameof(ShareTip)); }
    }

    public string ShareTip => _inVoice ? "Share your screen" : "Share this device's screen in the call you are in on your other device";
    public bool CanStopSharing => _sharing;

    public string MuteGlyph => _muted ? "🔇" : "🎤";
    public string MuteTip => _muted ? "Unmute" : "Mute";
    public string DeafenTip => _deafened ? "Undeafen" : "Deafen";
    public string MuteText => _muted ? "🔇 Unmute" : "🎤 Mute";
    public string DeafenText => _deafened ? "🎧 Undeafen" : "🎧 Deafen";
    public void VoiceMembersChanged() { Raise(nameof(HasVoiceMembers)); Raise(nameof(ShowVoiceRow)); }

    /// <summary>The row of people in voice and shared screens, shown only when there is something in it.</summary>
    public bool ShowVoiceRow => IsRoom && (HasVoiceMembers || HasShares);

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

    public void ApplyDelivery(DeliveryStatus status, IReadOnlyList<DeliveryRecipient>? recipients = null)
    {
        _delivery[status.MessageId] = status;
        var detail = DescribeRecipients(recipients);
        if (detail.Length > 0) _deliveryDetail[status.MessageId] = detail;
        else _deliveryDetail.TryGetValue(status.MessageId, out detail!);
        foreach (var m in Messages.Concat(_stored.Values.SelectMany(l => l)))
            if (m.Id == status.MessageId) { m.DeliveryDetail = detail ?? ""; m.Delivery = status; }
    }

    private static string DescribeRecipients(IReadOnlyList<DeliveryRecipient>? recipients)
    {
        if (recipients == null || recipients.Count == 0) return "";
        var got = recipients.Where(r => r.Delivered).Select(r => r.Name).ToList();
        var waiting = recipients.Where(r => !r.Delivered).Select(r => r.Name).ToList();
        var lines = new List<string>();
        // In a very large room the full list would not fit in a tooltip: say how many, and name the ones still waiting first.
        if (recipients.Count > MaxNamedRecipients) lines.Add($"Received by {got.Count} of {recipients.Count}");
        else if (got.Count > 0) lines.Add("Received by: " + string.Join(", ", got));
        if (waiting.Count > 0)
            lines.Add("Not confirmed yet: " + string.Join(", ", waiting.Take(MaxNamedRecipients))
                + (waiting.Count > MaxNamedRecipients ? $" and {waiting.Count - MaxNamedRecipients} more" : ""));
        return string.Join("\n", lines);
    }

    private const int MaxNamedRecipients = 20;

    private string? _invite;

    public string? Invite
    {
        get => _invite;
        set { if (_invite == value) return; _invite = value; Raise(); Raise(nameof(HasInvite)); }
    }

    public bool HasInvite => Invite != null;

    private bool _isOwner;

    /// <summary>You made this room, so you can replace its key.</summary>
    public bool IsOwner
    {
        get => _isOwner;
        set { if (_isOwner == value) return; _isOwner = value; Raise(); }
    }

    public ObservableCollection<MessageVm> Messages { get; } = new();

    /// <summary>Every message of the conversation, including those of channels that are not on screen.</summary>
    public IEnumerable<MessageVm> AllMessages => Messages.Concat(_stored.Values.SelectMany(l => l));

    public int Unread
    {
        get => _unread;
        set
        {
            _unread = value;
            if (value == 0 && _mentioned) { _mentioned = false; Raise(nameof(Mentioned)); Raise(nameof(UnreadText)); }
            Raise(); Raise(nameof(Label)); Raise(nameof(HasUnread)); Raise(nameof(UnreadText));
        }
    }

    private bool _mentioned;

    /// <summary>Something unread names you or answers you.</summary>
    public bool Mentioned
    {
        get => _mentioned;
        private set { if (_mentioned == value) return; _mentioned = value; Raise(); Raise(nameof(Label)); Raise(nameof(UnreadText)); }
    }

    /// <summary>What the badge says: the number, and an @ when you are named or answered in it.</summary>
    public string UnreadText => Unread + (Mentioned ? " @" : "");

    public string Label => Unread > 0 ? $"{Title}  ({UnreadText})" : Title;

    // A date line goes above the first message of each day; only the inserted message and the one after it can change.
    private void MarkDays(int index)
    {
        for (int j = index; j <= index + 1 && j < Messages.Count; j++)
            Messages[j].ShowDay = j == 0 || Messages[j - 1].Time.LocalDateTime.Date != Messages[j].Time.LocalDateTime.Date;
    }

    /// <summary>Makes the messages match <paramref name="current"/>: those that are no longer there (deleted, or from someone banned) go, new ones come in. Lines without an id, such as notices, stay.</summary>
    public void Reload(IReadOnlyList<MessageVm> current)
    {
        var keep = current.Where(m => m.Id != null).Select(m => m.Id!).ToHashSet();
        for (int i = Messages.Count - 1; i >= 0; i--)
            if (Messages[i].Id is { } id && !keep.Contains(id)) { _ids.Remove(id); Messages.RemoveAt(i); }
        foreach (var m in current) Add(m);
        for (int i = 0; i < Messages.Count; i++) MarkDays(i);
    }

    // ---- channels ----

    // Every message of a room is kept by channel; Messages holds the ones of the channel on show.
    private readonly Dictionary<string, List<MessageVm>> _stored = new();
    private bool _showChannels, _canManageChannels;

    /// <summary>The room's text channels and voice channels, as buttons.</summary>
    public ObservableCollection<ChannelVm> Channels { get; } = new();

    /// <summary>The same channels split by kind, for the groups under the room in the list.</summary>
    public ObservableCollection<ChannelVm> TextChannels { get; } = new();
    public ObservableCollection<ChannelVm> VoiceChannels { get; } = new();

    private bool _channelsOpen = true;

    /// <summary>Whether the room's channels are unfolded under it.</summary>
    public bool ChannelsOpen
    {
        get => _channelsOpen;
        set { if (_channelsOpen == value) return; _channelsOpen = value; Raise(); Raise(nameof(ShowChannelList)); Raise(nameof(OpenGlyph)); }
    }

    public bool ShowChannelList => _showChannels && _channelsOpen;
    public string OpenGlyph => _channelsOpen ? "▲" : "▼";

    /// <summary>Rooms made with an owner have channels beyond the first; older rooms show none.</summary>
    public bool ShowChannels
    {
        get => _showChannels;
        set { if (_showChannels == value) return; _showChannels = value; Raise(); Raise(nameof(CanJoinVoice)); Raise(nameof(ShowChannelList)); }
    }

    public bool CanManageChannels
    {
        get => _canManageChannels;
        set { if (_canManageChannels == value) return; _canManageChannels = value; Raise(); }
    }

    public string CurrentChannel { get; private set; } = RoomChannels.DefaultText;

    /// <summary>A channel that was on show last time, to show once the room's channels are known (it may arrive after the room does).</summary>
    public string? PendingChannel { get; set; }
    public string CurrentChannelName => Channels.FirstOrDefault(c => c.IsText && c.Id == CurrentChannel)?.Name ?? "general";

    public void SelectChannel(string id)
    {
        if (!IsRoom) return;
        CurrentChannel = id;
        Messages.Clear();
        if (_stored.TryGetValue(id, out var list)) foreach (var m in list) Messages.Add(m);
        for (int i = 0; i < Messages.Count; i++) MarkDays(i);
        foreach (var c in Channels) c.Selected = c.IsText && c.Id == id;
        if (Channels.FirstOrDefault(c => c.IsText && c.Id == id) is { } shown)
        {
            Unread = Math.Max(0, Unread - shown.Unread);
            shown.Unread = 0;
        }
        Raise(nameof(CurrentChannel));
        Raise(nameof(CurrentChannelName));
    }

    /// <summary>The room was opened: what is on show is read, and what waits in other channels still counts.</summary>
    public void Opened()
    {
        foreach (var c in Channels.Where(c => c.Id == CurrentChannel)) c.Unread = 0;
        Unread = Channels.Where(c => c.IsText && c.Id != CurrentChannel).Sum(c => c.Unread);
    }

    public void NoteUnread(string? channel, bool mentioned = false)
    {
        Unread++;
        if (mentioned) Mentioned = true;
        if (channel != null && Channels.FirstOrDefault(c => c.IsText && c.Id == channel) is { } c) c.Unread++;
    }

    /// <summary>Makes the buttons match the room's channels, keeping the one on show unless it is gone.</summary>
    public void SetChannels(IReadOnlyList<ChannelInfo> infos)
    {
        var existing = Channels.ToDictionary(c => c.Id);
        for (int i = Channels.Count - 1; i >= 0; i--)
            if (infos.All(w => w.Id != Channels[i].Id)) Channels.RemoveAt(i);
        for (int i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            if (existing.TryGetValue(info.Id, out var vm) && Channels.Contains(vm))
            {
                vm.Name = info.Name;
                int at = Channels.IndexOf(vm);
                if (at != i) Channels.Move(at, i);
            }
            else Channels.Insert(i, vm = new ChannelVm(info));
            vm.Selected = vm.IsText && vm.Id == CurrentChannel;
        }
        if (!Channels.Any(c => c.IsText && c.Id == CurrentChannel)) SelectChannel(RoomChannels.DefaultText);
        Regroup(TextChannels, Channels.Where(c => c.IsText));
        Regroup(VoiceChannels, Channels.Where(c => c.IsVoice));
        Raise(nameof(CurrentChannelName));
    }

    private static void Regroup(ObservableCollection<ChannelVm> group, IEnumerable<ChannelVm> channels)
    {
        var wanted = channels.ToList();
        if (group.SequenceEqual(wanted)) return;
        group.Clear();
        foreach (var c in wanted) group.Add(c);
    }

    // Replies find what they answer by its key, whichever came first.
    private readonly Dictionary<string, MessageVm> _byKey = new();
    private readonly Dictionary<string, List<MessageVm>> _awaitingOriginal = new();

    /// <summary>The message with this key, if this conversation has it (in any channel).</summary>
    public MessageVm? FindByKey(string key) => _byKey.GetValueOrDefault(key);

    private void IndexForReplies(MessageVm message)
    {
        if (message.Key != null)
        {
            _byKey[message.Key] = message;
            if (_awaitingOriginal.Remove(message.Key, out var waiting))
                foreach (var reply in waiting) reply.SetQuote(message.Sender, message.Preview, message.IsMine);
        }
        if (message.ReplyKey is { } answers)
        {
            if (_byKey.TryGetValue(answers, out var original)) message.SetQuote(original.Sender, original.Preview, original.IsMine);
            else
            {
                message.SetQuote("", "an earlier message that is not here", false);
                if (!_awaitingOriginal.TryGetValue(answers, out var list)) _awaitingOriginal[answers] = list = new List<MessageVm>();
                list.Add(message);
            }
        }
    }

    public void Add(MessageVm message)
    {
        if (message.Id != null && !_ids.Add(message.Id)) return;
        IndexForReplies(message);
        if (message.Id != null && _delivery.TryGetValue(message.Id, out var status))
        {
            if (_deliveryDetail.TryGetValue(message.Id, out var detail)) message.DeliveryDetail = detail;
            message.Delivery = status;
        }
        if (IsRoom && message.Channel is { } channel)
        {
            if (!_stored.TryGetValue(channel, out var list)) _stored[channel] = list = new List<MessageVm>();
            int at = list.Count;
            while (at > 0 && list[at - 1].Time > message.Time) at--;
            list.Insert(at, message);
            if (channel != CurrentChannel) return;
        }
        int i = Messages.Count;
        while (i > 0 && Messages[i - 1].Time > message.Time) i--;
        Messages.Insert(i, message);
        MarkDays(i);
    }
}

/// <summary>A screen someone is sharing in a room.</summary>
public sealed class ShareVm : Observable
{
    private bool _watching;

    public ShareVm(string id, string name, bool isMine)
    {
        Id = id; Name = name; IsMine = isMine;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsMine { get; }
    public bool CanWatch => !IsMine;

    public bool Watching
    {
        get => _watching;
        set { if (_watching == value) return; _watching = value; Raise(); Raise(nameof(ButtonText)); }
    }

    public string ButtonText => _watching ? "Stop watching" : "Watch";
}

public sealed class VoiceMemberVm : Observable
{
    private bool _speaking, _muted, _deafened, _soundboardMuted;
    private string _name;

    public VoiceMemberVm(string id, string name, bool isSelf)
    {
        Id = id; _name = name; IsSelf = isSelf;
    }

    public string Id { get; }
    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; Raise(); Raise(nameof(Initial)); Raise(nameof(Avatar)); }
    }
    public bool IsSelf { get; }
    public string Initial => Avatars.Initial(Name);
    public IBrush Avatar => Avatars.BrushFor(Name);

    public bool Speaking
    {
        get => _speaking;
        set { if (_speaking == value) return; _speaking = value; Raise(); Raise(nameof(Ring)); }
    }

    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; Raise(); Raise(nameof(Status)); }
    }

    public bool Deafened
    {
        get => _deafened;
        set { if (_deafened == value) return; _deafened = value; Raise(); Raise(nameof(Status)); }
    }

    private string _where = "";

    /// <summary>The voice channel they are in, shown when the room has more than one.</summary>
    public string Where
    {
        get => _where;
        set { if (_where == value) return; _where = value; Raise(); }
    }

    public bool CanMuteSoundboard => !IsSelf;

    /// <summary>The account behind this device; the volume chosen for a person is kept by account.</summary>
    public string Account { get; set; } = "";

    public bool CanSetVolume => !IsSelf;

    private double _volume = 100;

    /// <summary>How loud this person sounds to you, in percent. Changing it (the slider) raises <see cref="VolumeEdited"/>.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 200));
            if (_volume == value) return;
            _volume = value;
            Raise(); Raise(nameof(VolumeText));
            VolumeEdited?.Invoke(this);
        }
    }

    public string VolumeText => $"{_volume:0}%";

    public event Action<VoiceMemberVm>? VolumeEdited;

    /// <summary>Sets the volume shown without telling anyone, for values read from the settings.</summary>
    public void ShowVolume(double percent)
    {
        percent = Math.Round(Math.Clamp(percent, 0, 200));
        if (_volume == percent) return;
        _volume = percent;
        Raise(nameof(Volume)); Raise(nameof(VolumeText));
    }

    public bool SoundboardMuted
    {
        get => _soundboardMuted;
        set { if (_soundboardMuted == value) return; _soundboardMuted = value; Raise(); Raise(nameof(SoundboardGlyph)); Raise(nameof(SoundboardTip)); }
    }

    public string SoundboardGlyph => _soundboardMuted ? "🔕" : "🔔";
    public string SoundboardTip => _soundboardMuted ? "Their soundboard sounds are muted. Click to unmute." : "Mute only their soundboard sounds";

    private bool _reconnecting;

    /// <summary>The connection to this person is lost and Yaparr is trying again.</summary>
    public bool Reconnecting
    {
        get => _reconnecting;
        set { if (_reconnecting == value) return; _reconnecting = value; Raise(); Raise(nameof(Status)); }
    }

    public string Status => (_deafened ? "🎧✕" : _muted ? "🎤✕" : "") + (_reconnecting ? (_deafened || _muted ? "  " : "") + "reconnecting…" : "");
    public IBrush Ring => _speaking ? new SolidColorBrush(Color.Parse("#46A758")) : Brushes.Transparent;
}

/// <summary>A text or voice channel of a room, shown as a button.</summary>
public sealed class ChannelVm : Observable
{
    private string _name;
    private int _unread, _people;
    private bool _selected, _mine;

    public ChannelVm(ChannelInfo info)
    {
        Id = info.Id; _name = info.Name; Kind = info.Kind; IsDefault = info.IsDefault;
    }

    public string Id { get; }
    public ChannelKind Kind { get; }
    public bool IsDefault { get; }
    public bool IsText => Kind == ChannelKind.Text;
    public bool IsVoice => Kind == ChannelKind.Voice;

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; Raise(); Raise(nameof(Label)); }
    }

    /// <summary>Messages waiting in a text channel you are not looking at.</summary>
    public int Unread
    {
        get => _unread;
        set { if (_unread == value) return; _unread = value; Raise(); Raise(nameof(Label)); }
    }

    /// <summary>People in a voice channel.</summary>
    public int People
    {
        get => _people;
        set { if (_people == value) return; _people = value; Raise(); Raise(nameof(Label)); }
    }

    /// <summary>The text channel on show.</summary>
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Raise(); Raise(nameof(Highlight)); }
    }

    /// <summary>The voice channel you are in.</summary>
    public bool Mine
    {
        get => _mine;
        set { if (_mine == value) return; _mine = value; Raise(); Raise(nameof(Highlight)); }
    }

    public bool Highlight => _selected || _mine;

    public string Label => (IsText ? "# " : "\U0001F50A ") + _name + (IsText ? (_unread > 0 ? $"  ({_unread})" : "") : (_people > 0 ? $"  ({_people})" : ""));
}
