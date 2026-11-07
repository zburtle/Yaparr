using System.Collections.ObjectModel;
using Avalonia.Threading;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.AndroidApp;

/// <summary>A person of a room, for the list of people.</summary>
public sealed record Person(string Name, string Id, string Role = "");

/// <summary>
/// The running app behind the screens: the chat node, and a <see cref="Chat"/> for each room and direct chat. It keeps the rooms
/// you joined in the settings (the same way the desktop app does, so the account sync between your devices finds them), brings
/// them back when the app starts, and counts what is unread. Everything that reaches the screens is passed to the UI thread.
/// </summary>
public sealed partial class Session : IDisposable
{
    private readonly AppData _data;
    private readonly AppSettings _settings;
    private readonly ReadMarks _marks;
    private readonly Dictionary<ChatRoom, Chat> _rooms = new();
    private readonly Dictionary<string, Chat> _directs = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DeviceSearch _deviceSearch = new();
    private string? _banner;
    private double? _bannerProgress;
    private ChatNode? _node;
    private Chat? _open;
    private bool _channelChoiceDirty;

    public Session(AppData data, AppSettings settings)
    {
        _data = data;
        _settings = settings;
        _marks = new ReadMarks(settings.ReadMarks);
        _saveTimer.Tick += (_, _) => Save();
    }

    /// <summary>The rooms and direct chats, the one with the newest message first.</summary>
    public ObservableCollection<Chat> Chats { get; } = new();

    /// <summary>Something worth telling the person (a person not found, a room left on another device).</summary>
    public event Action<string>? Notice;

    /// <summary>Who this phone is connected to, as a sentence; said again whenever it changes.</summary>
    public event Action<string>? ConnectionChanged;

    /// <summary>What the banner above the room list says now (null: hide it), and how far along it is (0 to 1, or null when that cannot be told).</summary>
    public event Action<string?, double?>? SyncChanged;

    /// <summary>This device was just linked to an account: say that it is looking for the device it was linked from.</summary>
    public void BeginDeviceSearch() { _deviceSearch.Begin(); UpdateSync(); }

    private void UpdateSync()
    {
        if (_node is not { } node) return;
        var states = new List<RoomSyncState>();
        foreach (var chat in Chats.ToList()) if (chat.IsRoom) states.Add(chat.UpdateSync());
        var banner = SyncStatus.Banner(_deviceSearch.Update(node), states, out var progress);
        if (banner == _banner && progress == _bannerProgress) return;
        _banner = banner;
        _bannerProgress = progress;
        SyncChanged?.Invoke(banner, progress);
    }

    private readonly Queue<string> _log = new();

    private void Log(string line)
    {
        lock (_log)
        {
            _log.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
            while (_log.Count > 300) _log.Dequeue();
        }
    }

    /// <summary>What to send along with a report of a problem: who the phone is, who it is connected to, and what the node has been doing.</summary>
    public string Diagnostics()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("Yaparr Android " + (typeof(Session).Assembly.GetName().Version?.ToString() ?? "?"));
        if (_node is { } node)
        {
            text.AppendLine($"account {node.AccountId.Short}, this device {node.Id.Short}");
            text.AppendLine($"rooms {_rooms.Count}, direct chats {_directs.Count}, connected to {node.ConnectedPeers.Count}:");
            foreach (var p in node.ConnectedPeers)
                text.AppendLine($"  {p.DisplayName} {p.Id.Short}{(node.IsMyDevice(p.Id) ? " (your device)" : "")}{(p.IsRelayed ? " (relayed)" : "")} {p.EndPoint}");
        }
        else text.AppendLine("not started");
        text.AppendLine("--- log ---");
        lock (_log) foreach (var line in _log) text.AppendLine(line);
        return text.ToString();
    }

    private void SayConnection()
    {
        if (_node is not { } node) return;
        var peers = node.ConnectedPeers;
        int mine = peers.Count(p => node.IsMyDevice(p.Id));
        string text = peers.Count == 0
            ? "Not connected to anyone yet. Looking for your devices and people..."
            : $"Connected to {peers.Count} device{(peers.Count == 1 ? "" : "s")}" + (mine > 0 ? $", {mine} of them yours" : "");
        ConnectionChanged?.Invoke(text);
    }

    public bool Started => _node != null;

    public string AccountId => _node?.AccountId.Value ?? "";

    private static void UI(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>Starts the node and brings back the rooms and direct chats. <paramref name="linked"/> is what a device just linked to an account brings along.</summary>
    public async Task StartAsync(string name, IReadOnlyList<AccountEntry> linked)
    {
        if (_node != null) return;
        var identity = _data.LoadOrCreateIdentity();
        var traversal = TraversalOptions.Load(Path.Combine(_data.Directory, "network.json"));
        var historyCipher = HistoryCipher.LoadOrCreate(Path.Combine(_data.Directory, "history.key"));
        var node = _node = new ChatNode(new ChatNodeOptions
        {
            DisplayName = name,
            HistoryCipher = historyCipher,
            BlockedPeers = _settings.BlockedPeers.Select(b => new PeerId(b.Id)).ToList(),
            Identity = identity,
            AccountStatePath = _data.AccountStatePath,
            AccountEntries = linked,
            HistoryStore = new FileHistoryStore(_data.HistoryDirectory, historyCipher),
            DirectHistoryDirectory = Path.Combine(_data.Directory, "direct"),
            AttachmentDirectory = Path.Combine(_data.Directory, "attachments"),
            Traversal = traversal,
            RolesDirectory = Path.Combine(_data.Directory, "roles"),
            EnableLocalDiscovery = traversal.LocalDiscovery,
            PeerCachePath = Path.Combine(_data.Directory, "peers.json"),
            ClockOffsetsPath = Path.Combine(_data.Directory, "clocks.json"),
            Dht = new Yaparr.Discovery.Dht.DhtOptions { NodeCachePath = Path.Combine(_data.Directory, "dht-nodes.txt") },
            Log = Log,
        });
        node.AttachmentChanged += update => UI(() => OnAttachmentChanged(update));
        node.BlockChanged += (id, blocked) => UI(() => OnBlockChanged(id, blocked));
        node.SyncedRoomChanged += OnSyncedRoomChanged;
        node.DisplayNameChanged += OnDisplayNameChanged;
        node.IncomingDirectChat += chat => UI(() => AddDirect(chat));
        node.PeerConnected += _ => UI(SayConnection);
        node.PeerDisconnected += _ => UI(SayConnection);
        ReconcileFromAccount();
        node.Start();
        _saveTimer.Start();
        _syncTimer.Tick += (_, _) => UpdateSync();
        _syncTimer.Start();
        SayConnection();

        foreach (var stored in node.StoredDirectChats) AddDirect(stored);
        foreach (var priv in _settings.PrivateRooms.ToList())
        {
            try { await JoinKeyedRoomAsync(priv.Label, RoomKey.Parse(priv.Key), persist: false); }
            catch (FormatException) { }
        }
    }

    // Rooms that other devices of the account joined are rooms here too.
    private void ReconcileFromAccount()
    {
        if (_node == null) return;
        bool changed = false;
        foreach (var room in _node.SyncedRooms)
        {
            if (_settings.PrivateRooms.Any(r => r.Key == room.Key)) continue;
            // The same room under a newer key (its owner replaced the key on another device): keep the newer one.
            var same = RoomIdOf(room.Key) is { } id ? _settings.PrivateRooms.FirstOrDefault(r => RoomIdOf(r.Key) == id) : null;
            if (same != null) { if (EpochOf(room.Key) > EpochOf(same.Key)) { same.Key = room.Key; changed = true; } }
            else { _settings.PrivateRooms.Add(new PrivateRoomSetting { Label = room.Name, Key = room.Key }); changed = true; }
        }
        if (changed) _data.SaveSettings(_settings);
    }

    private static string? RoomIdOf(string key)
    {
        try { return RoomKey.Parse(key).RoomId; }
        catch (FormatException) { return null; }
    }

    private static int EpochOf(string key)
    {
        try { return RoomKey.Parse(key).Epoch; }
        catch (FormatException) { return 0; }
    }

    // ---- rooms ----

    /// <summary>The room an invite is for, if you are in it already (under this key or an older or newer one).</summary>
    public Chat? FindRoom(RoomKey key) =>
        _rooms.Values.FirstOrDefault(c => c.Invite != null && RoomIdOf(c.Invite) == key.RoomId);

    /// <summary>The direct chat with a person, if one is open.</summary>
    public Chat? FindDirect(PeerId person) => _directs.GetValueOrDefault(person.Value);

    /// <summary>Makes a new room that you own and opens it.</summary>
    public Task<Chat> NewRoomAsync(string label) =>
        JoinKeyedRoomAsync(label, RoomKey.Generate(_node!.AccountId), persist: true);

    /// <summary>Joins the room an invite is for, or opens it if you are in it already.</summary>
    public async Task<Chat> JoinWithInviteAsync(string label, string invite)
    {
        var key = RoomKey.Parse(invite);
        if (_settings.PrivateRooms.FirstOrDefault(r => r.Key == key.ToString() || RoomIdOf(r.Key) == key.RoomId) is { } have
            && _rooms.Values.FirstOrDefault(c => c.Invite == have.Key) is { } existing)
            return existing;
        return await JoinKeyedRoomAsync(label, key, persist: true);
    }

    private async Task<Chat> JoinKeyedRoomAsync(string label, RoomKey key, bool persist)
    {
        var room = await _node!.JoinRoomAsync(key, label);
        if (_rooms.TryGetValue(room, out var existing)) return existing;
        if (persist)
        {
            _settings.PrivateRooms.Add(new PrivateRoomSetting { Label = label, Key = key.ToString() });
            _data.SaveSettings(_settings);
        }
        return AddRoom(room, label, key.ToString());
    }

    private Chat AddRoom(ChatRoom room, string label, string invite)
    {
        var chat = new Chat(label, room, null, invite);
        _rooms[room] = chat;
        room.MessageReceived += m => UI(() => OnMessage(chat, m));
        room.ReactionsChanged += _ => UI(() => ScheduleReactions(chat));
        room.ChannelsChanged += () => UI(() => RefreshChannels(chat, room));
        room.MemberJoined += _ => UI(() => ShowMembers(chat, room));
        room.MemberLeft += _ => UI(() => ShowMembers(chat, room));
        room.MemberRenamed += _ => UI(() => ShowMembers(chat, room));
        room.ModerationChanged += () => UI(() => Reload(chat, room));
        room.VoiceChanged += () => UI(() => RefreshVoice(chat));
        room.SoundsChanged += () => UI(() => SoundsChanged?.Invoke(chat));
        room.GifsChanged += () => UI(() => GifsChanged?.Invoke(chat));
        room.ChannelsChanged += () => UI(() => RefreshVoice(chat));
        room.VoiceChannelRemoved += id => UI(() => { if (_callChat == chat && chat.VoiceChannelId == id) LeaveVoice(); });

        foreach (var m in room.GetHistory()) AddHistoryLine(chat, m, room.RoomId);
        RefreshReactions(chat);
        RefreshChannels(chat, room);
        RefreshVoice(chat);
        if (_settings.RoomChannels.TryGetValue(room.RoomId, out var wanted) && chat.Channels.Any(c => c.Id == wanted)) chat.SelectChannel(wanted);
        ShowMembers(chat, room);
        Chats.Add(chat);
        Resort(chat);
        return chat;
    }

    private void RefreshChannels(Chat chat, ChatRoom room) => chat.SetChannels(room.Channels);

    // ---- voice ----

    private VoiceCall? _call;
    private Chat? _callChat;
    private readonly HashSet<string> _speaking = new();

    /// <summary>The room you are in voice in, or null.</summary>
    public Chat? VoiceChat => _callChat;

    /// <summary>Joins the room's voice channel on this phone, leaving the one you are in. Throws if the microphone cannot be opened.</summary>
    public void JoinVoice(Chat chat, Android.Content.Context context, string channelId = RoomChannels.DefaultVoice)
    {
        if (chat.Room is not { } room || _node == null) return;
        if (_callChat == chat && chat.VoiceChannelId == channelId) return;
        LeaveVoice();
        var call = new VoiceCall(context, room, channelId, _settings);
        call.Interrupted += () => UI(() =>
        {
            if (_callChat is { } c) { c.Muted = true; Notice?.Invoke("Another app is using the audio, so your microphone is muted."); }
        });
        call.Session.SpeakingChanged += (id, speaking) => UI(() => OnSpeaking(id.Value, speaking));
        call.Session.SoundboardsMuted = _settings.SoundboardsMuted;
        call.Session.SoundboardVolume = Math.Clamp(_settings.SoundboardVolume, 0, 200) / 100f;
        _call = call;
        _callChat = chat;
        chat.VoiceChannelId = channelId;
        chat.InVoice = true;
        chat.Muted = chat.Deafened = false;
        chat.RouteLabel = AudioRoutes.Label(call.Route);
        RefreshVoice(chat);
    }

    // ---- soundboard ----

    /// <summary>The room's soundboard changed (a sound was added, removed or finished arriving).</summary>
    public event Action<Chat>? SoundsChanged;

    /// <summary>The room's GIF shelf changed (a picture was added, removed or finished arriving).</summary>
    public event Action<Chat>? GifsChanged;

    public bool IsMine(GifInfo gif) => _node != null && gif.Owner == _node.Id;

    /// <summary>You can remove a picture you added; an owner or admin can take any down.</summary>
    public bool CanRemoveGif(Chat chat, GifInfo gif) => IsMine(gif) || chat.Room is { MyRole: >= RoomRole.Admin };

    public void RemoveGif(Chat chat, GifInfo gif)
    {
        if (chat.Room is not { } room) return;
        if (IsMine(gif)) room.RemoveGif(gif.Id); else room.DeleteGif(gif.Id);
    }

    public IReadOnlyList<SoundInfo> SoundsOf(Chat chat) => chat.Room?.Sounds ?? Array.Empty<SoundInfo>();

    public bool IsMine(SoundInfo sound) => _node != null && sound.Owner == _node.AccountId;

    /// <summary>You can remove a sound you added; an owner or admin can remove any.</summary>
    public bool CanRemoveSound(Chat chat, SoundInfo sound) => IsMine(sound) || chat.Room is { MyRole: >= RoomRole.Admin };

    /// <summary>Plays the sound into the voice channel, for everyone in it. False if you are not in voice, are deafened, or the sound has not finished arriving.</summary>
    public bool PlaySound(Chat chat, string id) => _callChat == chat && _call != null && !_call.Session.Deafened && chat.Room is { } room && room.PlaySound(id);

    /// <summary>Makes a sound from the first 20 seconds of an audio file (WAV, MP3 or OGG) and adds it to the soundboard. Throws <see cref="ArgumentException"/> with a message for the person.</summary>
    public async Task AddSoundAsync(Chat chat, byte[] file, string name)
    {
        if (chat.Room is not { } room) return;
        var clip = await Task.Run(() => AudioFiles.ToClip(file));
        room.AddSound(name, clip);
    }

    public void RemoveSound(Chat chat, SoundInfo sound)
    {
        if (chat.Room is not { } room) return;
        if (IsMine(sound)) room.RemoveSound(sound.Id); else room.DeleteSound(sound.Id);
    }

    /// <summary>Whether everybody's soundboard sounds are silenced for you.</summary>
    public bool SoundsSilenced => _settings.SoundboardsMuted;

    public void SilenceSounds(bool silenced)
    {
        _settings.SoundboardsMuted = silenced;
        _data.SaveSettings(_settings);
        if (_call != null) _call.Session.SoundboardsMuted = silenced;
    }

    /// <summary>Looks at the network again and explains in plain words what works and what does not (the same check as on the computer).</summary>
    public Task<NetworkCheckReport> CheckNetworkAsync() =>
        _node?.CheckNetworkAsync() ?? throw new InvalidOperationException("Yaparr has not finished starting yet.");

    /// <summary>A voice setting changed: the call that is going takes it at once.</summary>
    public void ApplyVoiceSettings()
    {
        if (_call != null) VoiceCall.Apply(_call.Session, _settings);
    }

    /// <summary>A new name for you, on this phone and your other devices. Throws if it is empty.</summary>
    public void Rename(string name)
    {
        _node?.Rename(name);
        _settings.DisplayName = name.Trim();
        _data.SaveSettings(_settings);
    }

    public void LeaveVoice()
    {
        var call = _call;
        var chat = _callChat;
        _call = null;
        _callChat = null;
        _speaking.Clear();
        call?.Dispose();
        if (chat != null)
        {
            chat.InVoice = false;
            chat.Muted = chat.Deafened = false;
            RefreshVoice(chat);
        }
    }

    public void ToggleMute()
    {
        if (_call == null || _callChat == null) return;
        _call.Session.Muted = !_call.Session.Muted;
        _callChat.Muted = _call.Session.Muted;
        RefreshVoice(_callChat);
    }

    public void ToggleDeafen()
    {
        if (_call == null || _callChat == null) return;
        _call.Session.Deafened = !_call.Session.Deafened;
        _callChat.Deafened = _call.Session.Deafened;
        RefreshVoice(_callChat);
    }

    /// <summary>Where the sound can go now, and which of those it goes to.</summary>
    public IReadOnlyList<(AudioRoute Route, string Label, bool Selected)> VoiceRoutes() =>
        _call == null ? Array.Empty<(AudioRoute, string, bool)>()
            : _call.AvailableRoutes().Select(r => (r, AudioRoutes.Label(r), r == _call.Route)).ToList();

    public void SelectRoute(AudioRoute route)
    {
        if (_call == null || _callChat == null) return;
        _call.Select(route);
        _callChat.RouteLabel = AudioRoutes.Label(_call.Route);
    }

    private void OnSpeaking(string id, bool speaking)
    {
        if (speaking) _speaking.Add(id); else _speaking.Remove(id);
        if (_callChat != null) foreach (var m in _callChat.VoiceMembers) m.Speaking = _speaking.Contains(m.Id);
    }

    /// <summary>Shows who is in the room's voice channel.</summary>
    private void RefreshVoice(Chat chat)
    {
        if (chat.Room is not { } room) return;
        var participants = room.VoiceParticipants;
        bool inCall = _callChat == chat;
        // The people in the channel you are in are shown as buttons that light up when they talk; the other channels just name who is there.
        var mine = new List<VoiceMember>();
        if (inCall)
            foreach (var p in participants.Where(p => p.Channel == chat.VoiceChannelId))
                mine.Add(new VoiceMember(p.Peer.Id.Value, p.Peer.DisplayName, p.IsSelf) { Muted = p.Muted, Deafened = p.Deafened, Speaking = _speaking.Contains(p.Peer.Id.Value) });
        var channels = new List<VoiceChannelVm>();
        foreach (var c in room.Channels.Where(c => c.Kind == ChannelKind.Voice).OrderBy(c => c.Order))
        {
            var names = participants.Where(p => p.Channel == c.Id).Select(p => p.IsSelf ? "You" : p.Peer.DisplayName).ToList();
            channels.Add(new VoiceChannelVm(c.Id, c.Name) { MembersText = names.Count == 0 ? "No one" : string.Join(", ", names), InCall = inCall && chat.VoiceChannelId == c.Id });
        }
        chat.SetVoice(channels, mine);
    }

    /// <summary>Puts the line under the title back to who is here, after something else was said there.</summary>
    public void RefreshStatus(Chat chat)
    {
        if (chat.Room is { } room) ShowMembers(chat, room);
        else chat.Status = "Direct chat";
    }

    private void ShowMembers(Chat chat, ChatRoom room)
    {
        int others = PeopleOf(chat).Count;
        chat.Status = others == 0 ? "Just you here" : others == 1 ? "1 other here" : others + " others here";
    }

    // Moderation removed a message: show the history as it is now.
    private void Reload(Chat chat, ChatRoom room)
    {
        var keep = chat.CurrentChannel;
        chat.ResetMessages();
        foreach (var m in room.GetHistory()) AddHistoryLine(chat, m, room.RoomId, counted: false);
        RefreshReactions(chat);
        chat.SelectChannel(keep);
    }

    /// <summary>The people of a room other than you, one entry for each person however many devices they have.</summary>
    public IReadOnlyList<Person> PeopleOf(Chat chat)
    {
        if (_node == null || chat.Room == null) return Array.Empty<Person>();
        var people = new Dictionary<string, Person>();
        foreach (var p in chat.Room.Members)
        {
            if (_node.IsMyDevice(p.Id)) continue;
            people.TryAdd(p.Account.Value, new Person(p.DisplayName, p.Account.Value, RoleText(chat.Room.RoleOf(p.Account))));
        }
        return people.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---- roles, bans and blocks ----

    private static string RoleText(RoomRole role) => role switch { RoomRole.Owner => "Owner", RoomRole.Admin => "Admin", _ => "" };

    /// <summary>The person who wrote a message, with their role in the room.</summary>
    public Person SenderOf(Chat chat, ChatMessage m) =>
        new(m.From.DisplayName, m.From.Account.Value, chat.Room is { } room ? RoleText(room.RoleOf(m.From.Account)) : "");

    private static bool IsAdminRole(string role) => role == "Admin";

    /// <summary>Only the owner can appoint and remove admins (not another owner, and not themselves).</summary>
    public bool CanSetAdmin(Chat chat, Person person) =>
        chat.Room is { MyRole: RoomRole.Owner } && _node != null && person.Id != _node.AccountId.Value && person.Role != "Owner";

    public void SetAdmin(Chat chat, Person person, bool admin) => chat.Room?.SetAdmin(new PeerId(person.Id), admin);

    public bool IsAdmin(Person person) => IsAdminRole(person.Role);

    /// <summary>An owner or admin can ban an ordinary member (not an admin or the owner, and not themselves).</summary>
    public bool CanBan(Chat chat, Person person) =>
        chat.Room is { MyRole: >= RoomRole.Admin } && _node != null && person.Id != _node.AccountId.Value && person.Role.Length == 0;

    public void Ban(Chat chat, Person person) => chat.Room?.Ban(new PeerId(person.Id), person.Name);

    public void Unban(Chat chat, string accountId) => chat.Room?.Unban(new PeerId(accountId));

    /// <summary>The people banned from the room (anyone can see them, an admin can lift the ban).</summary>
    public IReadOnlyList<Person> BannedPeople(Chat chat) =>
        chat.Room?.Banned.Select(b => new Person(b.Name, b.Account.Value)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList() ?? new List<Person>();

    /// <summary>A block is yours alone: you stop seeing and hearing them, in every room, on all your devices.</summary>
    public void Block(string accountId, string name)
    {
        if (_node == null) return;
        if (_settings.BlockedPeers.All(b => b.Id != accountId))
            _settings.BlockedPeers.Add(new KnownPeer { Id = accountId, Name = name, LastSeen = DateTimeOffset.Now });
        _data.SaveSettings(_settings);
        _node.Block(new PeerId(accountId));
    }

    public void Unblock(string accountId) => _node?.Unblock(new PeerId(accountId));

    public IReadOnlyList<Person> BlockedPeople() =>
        _node?.BlockedPeers.Select(p => new Person(_settings.BlockedPeers.FirstOrDefault(b => b.Id == p.Value)?.Name is { Length: > 0 } n ? n : "Unknown name", p.Value))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList() ?? new List<Person>();

    // The node reports every change, so the saved list follows it.
    private void OnBlockChanged(PeerId id, bool blocked)
    {
        if (!blocked && _settings.BlockedPeers.RemoveAll(b => b.Id == id.Value) > 0) _data.SaveSettings(_settings);
    }

    /// <summary>Whether you can delete this message for everyone: an owner or admin, in a room.</summary>
    public bool CanDelete(Chat chat, Line line) => chat.Room is { MyRole: >= RoomRole.Admin } && line.Source != null;

    /// <summary>Deletes a message for everyone in the room (owners and admins only).</summary>
    public void DeleteMessage(Chat chat, Line line)
    {
        if (chat.Room is { } room && line.Source != null) room.DeleteMessage(line.Source);
    }

    /// <summary>The people who can be named in the chat: everyone in the room but you.</summary>
    public IReadOnlyList<Person> Mentionable(Chat chat) => PeopleOf(chat);

    /// <summary>A new name for the room, for you only: others see the name they gave it. Kept with the room.</summary>
    public void RenameRoom(Chat chat, string label)
    {
        label = label.Trim();
        if (label.Length == 0 || chat.Room == null) return;
        chat.Title = label;
        if (_settings.PrivateRooms.FirstOrDefault(r => r.Key == chat.Invite) is { } saved)
        {
            saved.Label = label;
            _data.SaveSettings(_settings);
        }
    }

    /// <summary>Leaves the room here and on your other devices.</summary>
    public async Task LeaveAsync(Chat chat)
    {
        if (chat.Room is { } room)
        {
            try { await room.LeaveAsync(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { Notice?.Invoke("Could not leave cleanly: " + ex.Message); }
            _rooms.Remove(room);
            if (_settings.PrivateRooms.RemoveAll(r => r.Key == chat.Invite) > 0) _data.SaveSettings(_settings);
        }
        else if (chat.Direct is { } direct)
        {
            _directs.Remove(direct.PeerId.Value);
        }
        if (_open == chat) _open = null;
        Chats.Remove(chat);
    }

    // Another of your devices joined or left a room.
    private void OnSyncedRoomChanged(SyncedRoom room, bool joined) => UI(async () =>
    {
        try
        {
            if (joined)
            {
                if (_rooms.Values.Any(c => c.Invite == room.Key || (c.Room != null && c.Room.RoomId == RoomIdOf(room.Key)))) return;
                if (!_settings.PrivateRooms.Any(r => r.Key == room.Key)) { _settings.PrivateRooms.Add(new PrivateRoomSetting { Label = room.Name, Key = room.Key }); _data.SaveSettings(_settings); }
                await JoinKeyedRoomAsync(room.Name, RoomKey.Parse(room.Key), persist: false);
                Notice?.Invoke($"Joined \"{room.Name}\" because you joined it on another device.");
            }
            else if (_rooms.Values.FirstOrDefault(c => c.Invite == room.Key) is { } chat)
            {
                await LeaveAsync(chat);
                Notice?.Invoke($"Left \"{room.Name}\" because you left it on another device.");
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { }
    });

    private void OnDisplayNameChanged(string name) => UI(() =>
    {
        _settings.DisplayName = name;
        _data.SaveSettings(_settings);
    });

    // ---- direct chats ----

    /// <summary>Opens a chat with a person, finding them first. Can take a minute if they only just came online.</summary>
    public async Task<Chat> OpenDirectAsync(PeerId person)
    {
        var chat = await _node!.OpenDirectChatAsync(person, _cts.Token);
        return AddDirect(chat);
    }

    private Chat AddDirect(DirectChat direct)
    {
        if (_directs.TryGetValue(direct.PeerId.Value, out var existing)) return existing;
        var chat = new Chat(direct.DisplayName, null, direct, null);
        _directs[direct.PeerId.Value] = chat;
        direct.MessageReceived += m => UI(() => OnMessage(chat, m));
        direct.ReactionsChanged += _ => UI(() => ScheduleReactions(chat));
        // Another of your devices read it: nothing here is new any more.
        direct.ReadChanged += () => UI(chat.ClearAllUnread);
        var readUpTo = direct.ReadUpTo;
        foreach (var m in direct.History)
        {
            var line = ToLine(m);
            if (chat.Add(line) && !line.Mine && (readUpTo == null || m.Timestamp > readUpTo)) chat.NoteUnread(null);
        }
        RefreshReactions(chat);
        chat.Status = "Direct chat";
        Chats.Add(chat);
        Resort(chat);
        return chat;
    }

    // ---- messages ----

    private Line ToLine(ChatMessage m)
    {
        bool mine = _node != null && _node.IsMyDevice(m.From.Id);
        var parts = m.Parts;
        var line = new Line(m.Key, m.From.DisplayName, m.Attachment != null ? parts.Body : m.Body, m.Timestamp, mine, m.Channel, m)
        {
            ReplyKey = parts.ReplyTo, Mentions = parts.Mentions, Card = parts.Preview,
            MentionsMe = _node != null && !mine && (m.Room != null && _rooms.Keys.FirstOrDefault(r => r.Name == m.Room) is { } named
                ? named.Names(m, _node.AccountId)
                : parts.Mentions.Any(x => x.Account == _node.AccountId)),
        };
        return line;
    }

    // A message of the history that came while the app was shut: unread when it is newer than what was seen in its channel.
    private void AddHistoryLine(Chat chat, ChatMessage m, string roomId, bool counted = true)
    {
        var line = ToLine(m);
        if (!chat.Add(line)) return;
        TrackPicture(line);
        if (!counted || line.Mine || m.Channel is not { } channel) return;
        if (_marks.IsUnread(roomId, channel, m.Timestamp.ToUnixTimeMilliseconds())) chat.NoteUnread(channel);
    }

    private void OnMessage(Chat chat, ChatMessage m)
    {
        var line = ToLine(m);
        if (!chat.Add(line)) return;
        TrackPicture(line);
        bool shown = _open == chat && (!chat.IsRoom || (m.Channel ?? RoomChannels.DefaultText) == chat.CurrentChannel);
        if (shown) MarkRead(chat);
        else if (!line.Mine) chat.NoteUnread(m.Channel);
        Resort(chat);
    }

    // ---- pictures ----

    private readonly Dictionary<string, List<Line>> _pictureLines = new();

    // A picture is shown as soon as it is on the phone; one that is not is fetched from whoever has it (or waits for a tap when it is big).
    private void TrackPicture(Line line)
    {
        if (line.Attachment is not { } a) return;
        if (!_pictureLines.TryGetValue(a.Hash, out var list)) _pictureLines[a.Hash] = list = new List<Line>();
        list.Add(line);
        line.PictureStatus = "Loading picture\u2026";
        _ = LoadPictureAsync(line, force: false);
    }

    private async Task LoadPictureAsync(Line line, bool force)
    {
        if (_node is not { } node || line.Source == null || line.Attachment is not { } a) return;
        try
        {
            if (!node.HasAttachment(a.Hash))
            {
                var state = await node.FetchAttachmentAsync(line.Source, force);
                if (state != AttachmentState.Ready) { UI(() => ApplyState(line, new AttachmentUpdate(a.Hash, state, 0, a.Size))); return; }
            }
            await ShowPictureAsync(line, a.Hash);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("picture: " + ex.Message);
            UI(() => line.PictureStatus = "Couldn't load the picture. Tap to try again.");
        }
    }

    // The picture is decoded at about the width the list shows it, so a screenful of pictures stays small.
    private async Task ShowPictureAsync(Line line, string hash)
    {
        var node = _node;
        byte[]? animation = null;
        var image = await Task.Run(() =>
        {
            var bytes = node?.ReadAttachment(hash);
            if (bytes == null) return null;
            if (ImageSniffer.TryRead(bytes, out var info) && info.Mime == "image/gif" && info.Frames > 1) animation = bytes;
            try
            {
                using var ms = new MemoryStream(bytes);
                return (Avalonia.Media.Imaging.Bitmap?)Avalonia.Media.Imaging.Bitmap.DecodeToWidth(ms, 720);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { return null; }
        });
        UI(() =>
        {
            if (image != null) { line.Picture = image; line.Animation = animation; }
            else line.PictureStatus = "Couldn't show the picture.";
        });
    }

    private void OnAttachmentChanged(AttachmentUpdate update)
    {
        if (!_pictureLines.TryGetValue(update.Hash, out var list)) return;
        foreach (var line in list.ToList())
        {
            if (update.State == AttachmentState.Ready) _ = ShowPictureAsync(line, update.Hash);
            else ApplyState(line, update);
        }
    }

    private static void ApplyState(Line line, AttachmentUpdate update)
    {
        if (line.HasPicture) return;
        line.PictureStatus = update.State switch
        {
            AttachmentState.Fetching => update.Total > 0 && update.Received > 0 ? $"Downloading {update.Received * 100 / update.Total}%" : "Downloading\u2026",
            AttachmentState.NeedsConfirmation => $"Tap to download ({Size(line.Attachment?.Size ?? update.Total)})",
            AttachmentState.Unavailable => "Not available right now. Tap to try again.",
            _ => "Loading picture\u2026",
        };
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    /// <summary>A picture that is not here: fetch it now, whatever its size (a tap on it).</summary>
    public void DownloadPicture(Line line)
    {
        if (line.HasPicture || line.Attachment == null) return;
        line.PictureStatus = "Downloading\u2026";
        _ = LoadPictureAsync(line, force: true);
    }

    /// <summary>The picture as it was sent, for showing big or saving; null if it is not on the phone.</summary>
    private GifShelf? _shelf;

    /// <summary>The GIF shelf: pictures kept on this phone to send again.</summary>
    internal GifShelf Shelf => _shelf ??= new GifShelf(_data.ShelfDirectory);

    public byte[]? PictureBytes(Line line) => line.Attachment is { } a ? _node?.ReadAttachment(a.Hash) : null;

    /// <summary>Shrinks a picture for sending (also dropping camera and location details), sends it with <paramref name="caption"/> to the chat, and shows it.</summary>
    public async Task SendPictureAsync(Chat chat, byte[] original, string fileName, string caption, string? replyKey = null)
    {
        if (_node is not { } node) return;
        var prepared = await Task.Run(() => ImageTools.Prepare(original, fileName));
        var attachment = node.PrepareAttachment(prepared.Bytes, prepared.FileName, prepared.Thumbnail);
        var text = attachment.ToText(caption, replyKey);
        string id;
        string? channel = null;
        if (chat.Room is { } room)
        {
            channel = chat.CurrentChannel;
            id = channel == RoomChannels.DefaultText ? await room.SendAsync(text) : await room.SendAsync(text, channel);
        }
        else id = await chat.Direct!.SendAsync(text);
        var line = new Line(ChatMessage.KeyOf(node.Id, id), node.DisplayName, caption, DateTimeOffset.Now, mine: true, channel, OwnMessage(chat, id, text, channel, attachment)) { Attachment = attachment, ReplyKey = replyKey };
        if (chat.Add(line))
        {
            if (!_pictureLines.TryGetValue(attachment.Hash, out var list)) _pictureLines[attachment.Hash] = list = new List<Line>();
            list.Add(line);
            await ShowPictureAsync(line, attachment.Hash);
        }
        Resort(chat);
    }

    // A message just sent from this phone, as the library would hand it back: what deleting it needs.
    private ChatMessage OwnMessage(Chat chat, string id, string text, string? channel, Attachment? attachment) =>
        new(new PeerInfo(_node!.Id, _node.DisplayName, null, AccountId: _node.AccountId), chat.Room?.Name, id, text, DateTimeOffset.Now, Attachment: attachment, Channel: channel);

    /// <summary>Sends a message to the chat, in the channel on show.</summary>
    public async Task SendAsync(Chat chat, string text, string? replyKey = null, IReadOnlyCollection<Mention>? mentions = null)
    {
        if (_node == null) return;
        string id;
        string? channel = null;
        var preview = await PreviewForAsync(chat, text);
        if (chat.Room is { } room)
        {
            channel = chat.CurrentChannel;
            id = await room.SendAsync(text, channel == RoomChannels.DefaultText ? null : channel, replyKey, mentions, preview);
        }
        else id = await chat.Direct!.SendAsync(text, replyKey, preview);
        chat.Add(new Line(ChatMessage.KeyOf(_node.Id, id), _node.DisplayName, text, DateTimeOffset.Now, mine: true, channel, OwnMessage(chat, id, text, channel, null)) { ReplyKey = replyKey, Mentions = mentions?.ToList() ?? new List<Mention>(), Card = preview });
        Resort(chat);
    }

    private LinkPreviewFetcher? _previewFetcher;

    // The card for the first web link in the words, or null: no link, previews are off, or the page could not be read in a few seconds.
    // The message is signed with its card, so the card has to be ready before it goes.
    private async Task<LinkPreview?> PreviewForAsync(Chat chat, string text)
    {
        if (!_settings.LinkPreviews || LinkPreview.FirstLink(text) is not { } link) return null;
        var before = chat.Status;
        chat.Status = "Getting a preview of the link\u2026";
        try
        {
            _previewFetcher ??= new LinkPreviewFetcher();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            return await _previewFetcher.FetchAsync(link, cts.Token);
        }
        catch (OperationCanceledException) { return null; }
        finally { chat.Status = before; }
    }

    // The chat with the newest message goes first.
    private void Resort(Chat chat)
    {
        int from = Chats.IndexOf(chat);
        if (from < 0) return;
        int to = from;
        while (to > 0 && Chats[to - 1].LastActivity < chat.LastActivity) to--;
        if (to != from) Chats.Move(from, to);
    }

    // ---- what is on show ----

    /// <summary>The chat has been opened: what was unread in the channel on show is read.</summary>
    public void Open(Chat chat)
    {
        _open = chat;
        MarkRead(chat);
    }

    public void Close()
    {
        _open = null;
        Save();
    }

    /// <summary>Shows another text channel of the open room.</summary>
    public void SelectChannel(Chat chat, string channel)
    {
        chat.SelectChannel(channel);
        if (chat.Room is { } room && !(_settings.RoomChannels.TryGetValue(room.RoomId, out var have) && have == channel))
        {
            _settings.RoomChannels[room.RoomId] = channel;
            _channelChoiceDirty = true;
        }
        MarkRead(chat);
    }

    private void MarkRead(Chat chat)
    {
        chat.ClearUnread();
        if (chat.Room is { } room) _marks.Mark(room.RoomId, chat.CurrentChannel);
        else chat.Direct?.MarkRead();
    }

    /// <summary>Writes what has been read and the channel on show to the settings.</summary>
    public void Save()
    {
        if (!_marks.Dirty && !_channelChoiceDirty) return;
        _marks.Saved();
        _channelChoiceDirty = false;
        try { _data.SaveSettings(_settings); }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        _syncTimer.Stop();
        LeaveVoice();
        Save();
        _cts.Cancel();
        _node?.Dispose();
        _node = null;
        _rooms.Clear();
        _directs.Clear();
        Chats.Clear();
    }
}
