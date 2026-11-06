using System.Collections.ObjectModel;
using Avalonia.Threading;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.AndroidApp;

/// <summary>A person of a room, for the list of people.</summary>
public sealed record Person(string Name, string Id);

/// <summary>
/// The running app behind the screens: the chat node, and a <see cref="Chat"/> for each room and direct chat. It keeps the rooms
/// you joined in the settings (the same way the desktop app does, so the account sync between your devices finds them), brings
/// them back when the app starts, and counts what is unread. Everything that reaches the screens is passed to the UI thread.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly AppData _data;
    private readonly AppSettings _settings;
    private readonly ReadMarks _marks;
    private readonly Dictionary<ChatRoom, Chat> _rooms = new();
    private readonly Dictionary<string, Chat> _directs = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(10) };
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
            Traversal = traversal,
            RolesDirectory = Path.Combine(_data.Directory, "roles"),
            EnableLocalDiscovery = traversal.LocalDiscovery,
            PeerCachePath = Path.Combine(_data.Directory, "peers.json"),
            ClockOffsetsPath = Path.Combine(_data.Directory, "clocks.json"),
            Dht = new Yaparr.Discovery.Dht.DhtOptions { NodeCachePath = Path.Combine(_data.Directory, "dht-nodes.txt") },
            Log = Log,
        });
        node.SyncedRoomChanged += OnSyncedRoomChanged;
        node.DisplayNameChanged += OnDisplayNameChanged;
        node.IncomingDirectChat += chat => UI(() => AddDirect(chat));
        node.PeerConnected += _ => UI(SayConnection);
        node.PeerDisconnected += _ => UI(SayConnection);
        ReconcileFromAccount();
        node.Start();
        _saveTimer.Start();
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
        room.ChannelsChanged += () => UI(() => RefreshChannels(chat, room));
        room.MemberJoined += _ => UI(() => ShowMembers(chat, room));
        room.MemberLeft += _ => UI(() => ShowMembers(chat, room));
        room.MemberRenamed += _ => UI(() => ShowMembers(chat, room));
        room.ModerationChanged += () => UI(() => Reload(chat, room));

        foreach (var m in room.GetHistory()) AddHistoryLine(chat, m, room.RoomId);
        RefreshChannels(chat, room);
        if (_settings.RoomChannels.TryGetValue(room.RoomId, out var wanted) && chat.Channels.Any(c => c.Id == wanted)) chat.SelectChannel(wanted);
        ShowMembers(chat, room);
        Chats.Add(chat);
        Resort(chat);
        return chat;
    }

    private void RefreshChannels(Chat chat, ChatRoom room) => chat.SetChannels(room.Channels);

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
        foreach (var m in room.GetHistory()) AddHistoryLine(chat, m, room.RoomId, counted: false);
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
            people.TryAdd(p.Account.Value, new Person(p.DisplayName, p.Account.Value));
        }
        return people.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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
        // Another of your devices read it: nothing here is new any more.
        direct.ReadChanged += () => UI(chat.ClearAllUnread);
        var readUpTo = direct.ReadUpTo;
        foreach (var m in direct.History)
        {
            var line = ToLine(m);
            if (chat.Add(line) && !line.Mine && (readUpTo == null || m.Timestamp > readUpTo)) chat.NoteUnread(null);
        }
        chat.Status = "Direct chat";
        Chats.Add(chat);
        Resort(chat);
        return chat;
    }

    // ---- messages ----

    private Line ToLine(ChatMessage m)
    {
        bool mine = _node != null && _node.IsMyDevice(m.From.Id);
        var body = m.Body;
        if (m.Attachment != null && body.Length == 0) body = "[picture]";
        return new Line(m.Key, m.From.DisplayName, body, m.Timestamp, mine, m.Channel);
    }

    // A message of the history that came while the app was shut: unread when it is newer than what was seen in its channel.
    private void AddHistoryLine(Chat chat, ChatMessage m, string roomId, bool counted = true)
    {
        var line = ToLine(m);
        if (!chat.Add(line) || !counted || line.Mine || m.Channel is not { } channel) return;
        if (_marks.IsUnread(roomId, channel, m.Timestamp.ToUnixTimeMilliseconds())) chat.NoteUnread(channel);
    }

    private void OnMessage(Chat chat, ChatMessage m)
    {
        var line = ToLine(m);
        if (!chat.Add(line)) return;
        bool shown = _open == chat && (!chat.IsRoom || (m.Channel ?? RoomChannels.DefaultText) == chat.CurrentChannel);
        if (shown) MarkRead(chat);
        else if (!line.Mine) chat.NoteUnread(m.Channel);
        Resort(chat);
    }

    /// <summary>Sends a message to the chat, in the channel on show.</summary>
    public async Task SendAsync(Chat chat, string text)
    {
        if (_node == null) return;
        string id;
        string? channel = null;
        if (chat.Room is { } room)
        {
            channel = chat.CurrentChannel;
            id = channel == RoomChannels.DefaultText ? await room.SendAsync(text) : await room.SendAsync(text, channel);
        }
        else id = await chat.Direct!.SendAsync(text);
        chat.Add(new Line(ChatMessage.KeyOf(_node.Id, id), _node.DisplayName, text, DateTimeOffset.Now, mine: true, channel));
        Resort(chat);
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
        Save();
        _cts.Cancel();
        _node?.Dispose();
        _node = null;
        _rooms.Clear();
        _directs.Clear();
        Chats.Clear();
    }
}
