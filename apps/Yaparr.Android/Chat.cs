using System.Collections.ObjectModel;
using Avalonia.Media;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

/// <summary>A text channel of a room, as a button over the messages.</summary>
public sealed class ChannelChip : Observable
{
    private bool _selected, _unread, _pinned, _muted;

    public ChannelChip(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Raise(); Raise(nameof(Fill)); Raise(nameof(Ink)); Raise(nameof(Fade)); }
    }

    public bool Unread
    {
        get => _unread;
        set { if (_unread == value) return; _unread = value; Raise(); Raise(nameof(Label)); }
    }

    /// <summary>Kept at the front of the room's channels (your choice, the same on all your devices).</summary>
    public bool Pinned
    {
        get => _pinned;
        set { if (_pinned == value) return; _pinned = value; Raise(); Raise(nameof(Label)); Raise(nameof(PinText)); Raise(nameof(Notes)); }
    }

    /// <summary>The category the admins put the channel in; empty for none.</summary>
    public string Category { get; set; } = "";

    /// <summary>No notifications and no count in the room for this channel (your choice).</summary>
    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; Raise(); Raise(nameof(Fade)); Raise(nameof(MuteText)); Raise(nameof(Notes)); }
    }

    /// <summary>The name, with a dot after it when something in the channel has not been read, and a pin before it when it is pinned.</summary>
    public string Label => (_pinned ? "\U0001F4CC " : "") + (_unread ? "# " + Name + " •" : "# " + Name);

    /// <summary>A muted channel is dimmed unless it is the one on show.</summary>
    public double Fade => _muted && !_selected ? 0.55 : 1;

    public string PinText => _pinned ? "Unpin" : "Pin";
    public string MuteText => _muted ? "Unmute" : "Mute";
    public string Notes => string.Join(", ", new[] { Category.Length > 0 ? Category : null, _pinned ? "pinned" : null, _muted ? "muted" : null }.OfType<string>());

    public IBrush Fill => Selected ? Theme.Brush("Accent") : Theme.Brush("BubbleOther");
    public IBrush Ink => Selected ? Brushes.White : Theme.Brush("Ink");
}

/// <summary>One room or direct chat in the list, and the messages of the one on show.</summary>
public sealed class Chat : Observable
{
    private readonly List<Line> _all = new();
    private readonly HashSet<string> _ids = new();
    private readonly Dictionary<string, Line> _byKey = new();
    private readonly Dictionary<string, int> _unread = new();
    private readonly Dictionary<string, int> _loud = new();   // of those, the ones that name you: a muted channel still counts these
    private List<ChannelInfo> _textChannels = new();
    private List<string> _categoryOrder = new();
    private string _title, _preview = "", _status = "", _syncText = "";
    private bool _syncing;
    private string _channel = RoomChannels.DefaultText;

    public Chat(string title, ChatRoom? room, DirectChat? direct, string? invite)
    {
        _title = title;
        Room = room;
        Direct = direct;
        Invite = invite;
    }

    // ---- scrolling back ----

    private Line? _marker;

    /// <summary>Where going back through the history is: nothing under way, earlier messages being got (a spinner is the first row of the list), or there are no more (the first row says so).</summary>
    public enum EarlierState { Idle, Loading, End }

    public EarlierState Earlier { get; private set; }

    /// <summary>True while older messages are being put at the top of the list, so the list is not scrolled down to the newest as it is for a new message.</summary>
    public bool AddingEarlier { get; set; }

    /// <summary>Earlier messages were asked for from the others and the answer is awaited, to be read when it comes.</summary>
    public bool WaitingForEarlier { get; set; }

    /// <summary>The messages shown, not counting the marker at the top.</summary>
    public int ShownMessages => Shown.Count(l => !l.IsMarker);

    /// <summary>The oldest message shown, which earlier ones are read before; null when there is none.</summary>
    public Line? OldestShown => Shown.FirstOrDefault(l => !l.IsMarker);

    /// <summary>Shows where going back is as the first row of the list (nothing for <see cref="EarlierState.Idle"/>).</summary>
    public void SetEarlier(EarlierState state)
    {
        bool wanted = state != EarlierState.Idle && ShownMessages > 0;
        if (state == Earlier && wanted == (_marker != null)) return;
        Earlier = state;
        AddingEarlier = true;   // the list is not scrolled to the end for this
        try
        {
            if (_marker != null) { Shown.Remove(_marker); _marker = null; }
            if (wanted)
            {
                _marker = Line.Marker(state == EarlierState.Loading ? Line.MarkerKind.Loading : Line.MarkerKind.End);
                Shown.Insert(0, _marker);
            }
        }
        finally { AddingEarlier = false; }
        Raise(nameof(Earlier));
    }

    /// <summary>The chat was opened or another channel chosen: going back starts again (the marker is gone with the list).</summary>
    public void ResetEarlier()
    {
        WaitingForEarlier = false;
        SetEarlier(EarlierState.Idle);
    }

    public ChatRoom? Room { get; }
    public DirectChat? Direct { get; }

    /// <summary>The key that lets someone join the room; null for a direct chat.</summary>
    public string? Invite { get; }

    public bool IsRoom => Room != null;

    /// <summary>What a pin of this chat is kept under: the room's id, or the other person's id in a direct chat.</summary>
    public string? PinId => Room?.RoomId ?? Direct?.PeerId.Value;

    /// <summary>The chat with yourself ("Notes to self"): listed by its newest message like any chat (pinnable and movable too), but never left or blocked.</summary>
    public bool IsNotes => Direct?.IsNotes == true;

    private bool _isPinned;

    /// <summary>Pinned chats stay at the top of the list, in an order the person sets.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned == value) return; _isPinned = value; Raise(); }
    }

    /// <summary>The messages of the channel on show (all of them in a direct chat).</summary>
    public ObservableCollection<Line> Shown { get; } = new();

    public ObservableCollection<ChannelChip> Channels { get; } = new();

    /// <summary>More than one text channel: the buttons are worth showing.</summary>
    public bool ShowChannels => _textChannels.Count > 1;

    public string CurrentChannel => _channel;

    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value; Raise(); Raise(nameof(Initial)); Raise(nameof(Tint)); }
    }

    private string? Account => Direct?.PeerId.Value;

    public string Initial => AvatarBrushes.For(Account) != null ? "" : Title.Length == 0 ? "?" : new System.Globalization.StringInfo(Title).SubstringByTextElements(0, 1).ToUpperInvariant();

    public IBrush Tint => AvatarBrushes.For(Account) ?? Theme.TintFor(Title);

    /// <summary>The picture of the other person arrived or changed.</summary>
    public void AvatarChanged() { Raise(nameof(Initial)); Raise(nameof(Tint)); }

    /// <summary>The last message, for the list.</summary>
    public string Preview
    {
        get => _preview;
        private set { if (_preview == value) return; _preview = value; Raise(); Raise(nameof(ShowPreview)); Raise(nameof(HasSyncText)); }
    }

    // ---- voice ----

    private bool _inVoice, _muted, _deafened;
    private string _routeLabel = "Speaker";

    /// <summary>Everyone in the room's voice channel, you included.</summary>
    public ObservableCollection<VoiceMember> VoiceMembers { get; } = new();

    /// <summary>You are in the room's voice channel on this phone.</summary>
    public bool InVoice
    {
        get => _inVoice;
        set { if (_inVoice == value) return; _inVoice = value; Raise(); Raise(nameof(CanJoinVoice)); Raise(nameof(CanShareScreen)); Raise(nameof(ShowShareHint)); }
    }

    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; Raise(); Raise(nameof(MuteLabel)); }
    }

    public bool Deafened
    {
        get => _deafened;
        set { if (_deafened == value) return; _deafened = value; Raise(); Raise(nameof(DeafenLabel)); }
    }

    /// <summary>Where the sound goes now ("Speaker", "Phone", "Bluetooth", "Headset"): the button for choosing says it.</summary>
    public string RouteLabel
    {
        get => _routeLabel;
        set { if (_routeLabel == value) return; _routeLabel = value; Raise(); }
    }

    public string MuteLabel => _muted ? "Unmute" : "Mute";
    public string DeafenLabel => _deafened ? "Undeafen" : "Deafen";

    /// <summary>A room you are not in the call of can be joined.</summary>
    public bool CanJoinVoice => IsRoom && !_inVoice;

    private bool _inVoiceElsewhere, _sharing;

    /// <summary>Another of your devices is in this room's call and this phone is not.</summary>
    public bool InVoiceElsewhere
    {
        get => _inVoiceElsewhere;
        set { if (_inVoiceElsewhere == value) return; _inVoiceElsewhere = value; Raise(); Raise(nameof(CanShareScreen)); Raise(nameof(ShareHint)); }
    }

    /// <summary>This phone's screen is being shared with the room.</summary>
    public bool IsSharing
    {
        get => _sharing;
        set { if (_sharing == value) return; _sharing = value; Raise(); Raise(nameof(CanShareScreen)); Raise(nameof(ShareHint)); }
    }

    /// <summary>The phone's screen can be shared: Android 14 or newer, and one of your devices (this one or another) is in the room's call.</summary>
    public bool CanShareScreen => IsRoom && Session.CanShareHere && (_inVoice || _inVoiceElsewhere) && !_sharing;

    public bool ShowShareHint => CanShareScreen && !_inVoice;
    public string ShareHint => "Share this phone's screen in the call you are in on your other device.";

    public bool HasVoiceMembers => VoiceMembers.Count > 0;

    /// <summary>The screens shared in the voice channel this phone is in.</summary>
    public ObservableCollection<ShareItem> Shares { get; } = new();

    public bool HasShares => Shares.Count > 0;

    public void SetShares(IReadOnlyList<ShareItem> shares)
    {
        Shares.Clear();
        foreach (var share in shares) Shares.Add(share);
        Raise(nameof(HasShares));
    }

    /// <summary>The room's voice channels, each with who is in it.</summary>
    public ObservableCollection<VoiceChannelVm> VoiceChannels { get; } = new();

    /// <summary>The voice channel you are in on this phone.</summary>
    public string VoiceChannelId { get; set; } = RoomChannels.DefaultVoice;

    /// <summary>Puts the voice bar in line with who is in the room's voice channels now.</summary>
    public void SetVoice(IReadOnlyList<VoiceChannelVm> channels, IReadOnlyList<VoiceMember> mine)
    {
        // The rows are kept when only who is in them changed, so a button is not rebuilt under a finger.
        if (channels.Count == VoiceChannels.Count && channels.Select(c => c.Id).SequenceEqual(VoiceChannels.Select(c => c.Id)))
        {
            for (int i = 0; i < channels.Count; i++)
            {
                VoiceChannels[i].Name = channels[i].Name;
                VoiceChannels[i].MembersText = channels[i].MembersText;
                VoiceChannels[i].InCall = channels[i].InCall;
            }
        }
        else
        {
            VoiceChannels.Clear();
            foreach (var c in channels) VoiceChannels.Add(c);
        }
        VoiceMembers.Clear();
        foreach (var m in mine) VoiceMembers.Add(m);
        Raise(nameof(HasVoiceMembers));
    }

    /// <summary>What a room is doing to get going ("Finding members..."), empty when it is ready.</summary>
    public string SyncText
    {
        get => _syncText;
        private set { if (_syncText == value) return; _syncText = value; Raise(); Raise(nameof(HasSyncText)); Raise(nameof(ShowPreview)); }
    }

    // The latest message is always what the list shows; what the room is doing to get going is only said while there is no message to show.
    public bool HasSyncText => _syncText.Length > 0 && _preview.Length == 0;

    public bool ShowPreview => _preview.Length > 0 || _syncText.Length == 0;

    /// <summary>When the latest message was sent, short, for the list ("14:05", "Yesterday", "Mon", "Nov 3"); empty when there is none.</summary>
    public string LastTimeText => ListTime.Of(LastActivity, DateTimeOffset.Now);

    /// <summary>The room is working on it, so the list shows a progress bar.</summary>
    public bool IsSyncing
    {
        get => _syncing;
        private set { if (_syncing == value) return; _syncing = value; Raise(); }
    }

    /// <summary>Where the room is in getting going; call now and then. Direct chats have nothing to report.</summary>
    public RoomSyncState UpdateSync()
    {
        if (Room is not { } room) return RoomSyncState.Ready;
        var state = room.SyncState;
        SyncText = SyncStatus.RoomText(state);
        IsSyncing = SyncStatus.IsBusy(state);
        return state;
    }

    /// <summary>Who is here, or what is happening, under the title.</summary>
    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; Raise(); }
    }

    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>Your own choices for the room's channels (muted, pinned, hidden, order); set once the room is shown.</summary>
    public AppSettings? Prefs { get; set; }

    private bool ChannelMuted(string channel) => Prefs != null && Room != null && ChannelPrefs.Has(Prefs, Room.RoomId, channel, ChannelFlag.Muted);

    // A muted channel adds nothing to the room's count except the messages that name you.
    public int Unread => _unread.Sum(p => ChannelMuted(p.Key) ? _loud.GetValueOrDefault(p.Key) : p.Value);
    public bool HasUnread => Unread > 0;
    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();

    private void RaiseUnread()
    {
        Raise(nameof(Unread));
        Raise(nameof(HasUnread));
        Raise(nameof(UnreadText));
        foreach (var chip in EveryChannel) chip.Unread = _unread.GetValueOrDefault(chip.Id) > 0;
    }

    /// <summary>The text channels of the room, in order. The one on show stays on show if it still exists.</summary>
    public void SetChannels(IEnumerable<ChannelInfo> channels)
    {
        var everything = channels.ToList();
        _categoryOrder = RoomChannels.CategoriesOf(everything);
        var text = _textChannels = everything.Where(c => c.Kind == ChannelKind.Text).OrderBy(c => c.Order).ToList();
        EveryChannel = text.Select(c => new ChannelChip(c.Id, c.Name) { Category = c.Category, Selected = c.Id == _channel, Unread = _unread.GetValueOrDefault(c.Id) > 0 }).ToList();
        if (text.Count > 0 && text.All(c => c.Id != _channel)) SelectChannel(text[0].Id);
        Relayout();
        Raise(nameof(ShowChannels));
    }

    /// <summary>Every text channel of the room, in the room's order, with your choices on each (the row of channels leaves out the hidden ones).</summary>
    public List<ChannelChip> EveryChannel { get; private set; } = new();

    /// <summary>The categories of the room, in the order they are shown.</summary>
    public IReadOnlyList<string> Categories => _categoryOrder;

    /// <summary>Lays the row of channels out: the ones in no category, then each category's; in each group the pinned ones first, and the most recent first when you chose that.</summary>
    public void Relayout()
    {
        if (Room == null) return;
        var prefs = Prefs;
        var last = new Dictionary<string, long>();
        if (prefs != null && ChannelPrefs.RecentFirst(prefs, Room.RoomId))
            foreach (var line in _all)
            {
                var key = line.Channel ?? RoomChannels.DefaultText;
                long at = line.When.ToUnixTimeMilliseconds();
                if (at > last.GetValueOrDefault(key)) last[key] = at;
            }
        foreach (var chip in EveryChannel)
        {
            chip.Pinned = prefs != null && ChannelPrefs.Has(prefs, Room.RoomId, chip.Id, ChannelFlag.Pinned);
            chip.Muted = prefs != null && ChannelPrefs.Has(prefs, Room.RoomId, chip.Id, ChannelFlag.Muted);
        }
        var wanted = new List<ChannelChip>();
        if (prefs == null) wanted.AddRange(EveryChannel);
        else
        {
            List<ChannelChip> Group(string category) => ChannelPrefs.Arrange(prefs, Room.RoomId,
                EveryChannel.Where(c => string.Equals(c.Category, category, StringComparison.CurrentCultureIgnoreCase)), c => c.Id, c => last.GetValueOrDefault(c.Id));
            wanted.AddRange(Group(""));
            foreach (var name in _categoryOrder) wanted.AddRange(Group(name));
        }
        if (!Channels.SequenceEqual(wanted))
        {
            Channels.Clear();
            foreach (var chip in wanted) Channels.Add(chip);
        }
        RaiseUnread();
    }

    /// <summary>Shows another text channel of the room.</summary>
    public void SelectChannel(string id)
    {
        _channel = id;
        ResetEarlier();
        foreach (var chip in Channels) chip.Selected = chip.Id == id;
        Shown.Clear();
        foreach (var line in _all.Where(Belongs)) Shown.Add(line);
        for (int i = 0; i < Shown.Count; i++) MarkDaysAround(i);
        Raise(nameof(CurrentChannel));
    }

    // A date line above the first message of each day, as on the computer: the message at this place and the one after it are looked at again (markers are not messages).
    private void MarkDaysAround(int index)
    {
        for (int j = index; j <= index + 1 && j < Shown.Count; j++)
        {
            var line = Shown[j];
            if (line.IsMarker) continue;
            int before = j - 1;
            while (before >= 0 && Shown[before].IsMarker) before--;
            line.ShowDay = before < 0 || Shown[before].When.LocalDateTime.Date != line.When.LocalDateTime.Date;
        }
    }

    private bool Belongs(Line line) => !IsRoom || (line.Channel ?? RoomChannels.DefaultText) == _channel;

    /// <summary>Adds a message. False if it is already here (the same message from the history and then live).</summary>
    public bool Add(Line line)
    {
        if (!_ids.Add(line.Id)) return false;
        _byKey[line.Id] = line;
        Quote(line);
        // A message that came later than one that answers it (history arrives in no fixed order) fills in its quote now.
        foreach (var other in _all) if (other.ReplyKey == line.Id) Quote(other);
        // Messages come in order, except a late one from the history, which goes where its time says.
        int at = _all.Count;
        while (at > 0 && _all[at - 1].When > line.When) at--;
        _all.Insert(at, line);
        if (Belongs(line))
        {
            int shown = Shown.Count;
            while (shown > 0 && Shown[shown - 1].When > line.When) shown--;
            Shown.Insert(shown, line);
            MarkDaysAround(shown);
        }
        // A message stamped in the future (a clock that is ahead) counts as now, so it cannot keep its chat at the top.
        var when = line.When > DateTimeOffset.Now ? DateTimeOffset.Now : line.When;
        if (when >= LastActivity)
        {
            LastActivity = when;
            Raise(nameof(LastTimeText));
            Preview = (line.Mine ? "You: " : IsRoom ? line.Who + ": " : "") + line.PreviewText;
        }
        return true;
    }

    /// <summary>Takes a message off the screen (it was deleted). Its id stays known, so the same message arriving again is not shown again.</summary>
    public void Remove(Line line)
    {
        _all.Remove(line);
        int at = Shown.IndexOf(line);
        Shown.Remove(line);
        if (at >= 0) MarkDaysAround(Math.Max(0, at));
        if (_byKey.TryGetValue(line.Id, out var held) && ReferenceEquals(held, line)) _byKey.Remove(line.Id);
        // What the list of chats shows of this one follows the newest message that is left.
        if (_all.Count > 0 && _all[^1] is { } last && last.When <= LastActivity)
        {
            LastActivity = last.When > DateTimeOffset.Now ? DateTimeOffset.Now : last.When;
            Raise(nameof(LastTimeText));
            Preview = (last.Mine ? "You: " : IsRoom ? last.Who + ": " : "") + last.PreviewText;
        }
        else if (_all.Count == 0) { Preview = ""; Raise(nameof(LastTimeText)); }
    }

    /// <summary>Forgets all the messages, so the history can be put back as it is now (a message an admin deleted is then gone).</summary>
    public void ResetMessages()
    {
        _all.Clear();
        _ids.Clear();
        _byKey.Clear();
        _marker = null;
        Earlier = EarlierState.Idle;
        Shown.Clear();
    }

    /// <summary>Every message of the chat, in all its channels.</summary>
    public IEnumerable<Line> AllLines => _all;

    /// <summary>The message with this key, or null if this phone does not have it.</summary>
    public Line? FindByKey(string key) => _byKey.GetValueOrDefault(key);

    // A reply shows who it answers and the start of what they said.
    private void Quote(Line line)
    {
        if (line.ReplyKey is not { } key) return;
        if (_byKey.TryGetValue(key, out var original)) { line.ReplyWho = original.Mine ? "You" : original.Who; line.ReplyText = original.Preview; }
        else { line.ReplyWho = ""; line.ReplyText = "An earlier message"; }
    }

    /// <summary>Counts a message as unread in its channel.</summary>
    public void NoteUnread(string? channel, bool mentioned = false)
    {
        var key = channel ?? RoomChannels.DefaultText;
        _unread[key] = _unread.GetValueOrDefault(key) + 1;
        if (mentioned) _loud[key] = _loud.GetValueOrDefault(key) + 1;
        RaiseUnread();
    }

    /// <summary>The channel on show has been read.</summary>
    public void ClearUnread(string? channel = null)
    {
        var key = channel ?? (IsRoom ? _channel : RoomChannels.DefaultText);
        _loud.Remove(key);
        if (_unread.Remove(key)) RaiseUnread();
    }

    public void ClearAllUnread()
    {
        if (_unread.Count == 0) return;
        _unread.Clear();
        _loud.Clear();
        RaiseUnread();
    }
}
