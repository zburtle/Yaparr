using System.Collections.ObjectModel;
using Avalonia.Media;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

/// <summary>A text channel of a room, as a button over the messages.</summary>
public sealed class ChannelChip : Observable
{
    private bool _selected, _unread;

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
        set { if (_selected == value) return; _selected = value; Raise(); Raise(nameof(Fill)); Raise(nameof(Ink)); }
    }

    public bool Unread
    {
        get => _unread;
        set { if (_unread == value) return; _unread = value; Raise(); Raise(nameof(Label)); }
    }

    /// <summary>The name, with a dot after it when something in the channel has not been read.</summary>
    public string Label => _unread ? "# " + Name + " •" : "# " + Name;

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

    public ChatRoom? Room { get; }
    public DirectChat? Direct { get; }

    /// <summary>The key that lets someone join the room; null for a direct chat.</summary>
    public string? Invite { get; }

    public bool IsRoom => Room != null;

    /// <summary>The messages of the channel on show (all of them in a direct chat).</summary>
    public ObservableCollection<Line> Shown { get; } = new();

    public ObservableCollection<ChannelChip> Channels { get; } = new();

    /// <summary>More than one text channel: the buttons are worth showing.</summary>
    public bool ShowChannels => Channels.Count > 1;

    public string CurrentChannel => _channel;

    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value; Raise(); Raise(nameof(Initial)); Raise(nameof(Tint)); }
    }

    public string Initial => Title.Length == 0 ? "?" : new System.Globalization.StringInfo(Title).SubstringByTextElements(0, 1).ToUpperInvariant();

    public IBrush Tint => Theme.TintFor(Title);

    /// <summary>The last message, for the list.</summary>
    public string Preview
    {
        get => _preview;
        private set { if (_preview == value) return; _preview = value; Raise(); }
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
        set { if (_inVoice == value) return; _inVoice = value; Raise(); Raise(nameof(CanJoinVoice)); }
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

    public bool HasVoiceMembers => VoiceMembers.Count > 0;

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

    public bool HasSyncText => _syncText.Length > 0;

    public bool ShowPreview => _syncText.Length == 0;

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

    public int Unread => _unread.Values.Sum();
    public bool HasUnread => Unread > 0;
    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();

    private void RaiseUnread()
    {
        Raise(nameof(Unread));
        Raise(nameof(HasUnread));
        Raise(nameof(UnreadText));
        foreach (var chip in Channels) chip.Unread = _unread.GetValueOrDefault(chip.Id) > 0;
    }

    /// <summary>The text channels of the room, in order. The one on show stays on show if it still exists.</summary>
    public void SetChannels(IEnumerable<ChannelInfo> channels)
    {
        var text = channels.Where(c => c.Kind == ChannelKind.Text).OrderBy(c => c.Order).ToList();
        Channels.Clear();
        foreach (var c in text) Channels.Add(new ChannelChip(c.Id, c.Name) { Selected = c.Id == _channel, Unread = _unread.GetValueOrDefault(c.Id) > 0 });
        if (text.Count > 0 && text.All(c => c.Id != _channel)) SelectChannel(text[0].Id);
        Raise(nameof(ShowChannels));
    }

    /// <summary>Shows another text channel of the room.</summary>
    public void SelectChannel(string id)
    {
        _channel = id;
        foreach (var chip in Channels) chip.Selected = chip.Id == id;
        Shown.Clear();
        foreach (var line in _all.Where(Belongs)) Shown.Add(line);
        Raise(nameof(CurrentChannel));
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
        }
        if (line.When >= LastActivity)
        {
            LastActivity = line.When;
            Preview = (line.Mine ? "You: " : IsRoom ? line.Who + ": " : "") + line.PreviewText;
        }
        return true;
    }

    /// <summary>Forgets all the messages, so the history can be put back as it is now (a message an admin deleted is then gone).</summary>
    public void ResetMessages()
    {
        _all.Clear();
        _ids.Clear();
        _byKey.Clear();
        Shown.Clear();
    }

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
    public void NoteUnread(string? channel)
    {
        var key = channel ?? RoomChannels.DefaultText;
        _unread[key] = _unread.GetValueOrDefault(key) + 1;
        RaiseUnread();
    }

    /// <summary>The channel on show has been read.</summary>
    public void ClearUnread(string? channel = null)
    {
        var key = channel ?? (IsRoom ? _channel : RoomChannels.DefaultText);
        if (_unread.Remove(key)) RaiseUnread();
    }

    public void ClearAllUnread()
    {
        if (_unread.Count == 0) return;
        _unread.Clear();
        RaiseUnread();
    }
}
