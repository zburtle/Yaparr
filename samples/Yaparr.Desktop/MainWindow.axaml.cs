using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ConversationVm> _conversations = new();
    private readonly Dictionary<ChatRoom, ConversationVm> _roomConversations = new();
    private readonly Dictionary<PeerId, ConversationVm> _directConversations = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _linkTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private AppData _data = null!;
    private AppSettings _settings = new();
    private VpnController _vpn = null!;
    private FileLog _log = null!;
    private ChatNode? _node;
    private ConversationVm? _selected;
    private bool _started;

    public MainWindow()
    {
        InitializeComponent();
        ConversationList.ItemsSource = _conversations;
        VersionText.Text = "Yaparr v" + Updater.CurrentVersion;

        // The bar under the window shows only the latest alert, and clears itself after a while.
        var alertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        alertTimer.Tick += (_, _) => { alertTimer.Stop(); StatusText.Text = ""; };
        StatusText.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBlock.TextProperty) return;
            bool has = !string.IsNullOrEmpty(StatusText.Text);
            AlertBar.IsVisible = has;
            alertTimer.Stop();
            if (has) alertTimer.Start();
        };
        SelectOnRightClick(ConversationList);
        SelectOnRightClick(MessageList);
        Opened += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            _ = CheckForUpdateAsync();
            await StartAsync();
        };
        Closing += (_, _) => Shutdown();
    }

    private static void SelectOnRightClick(ListBox list) =>
        list.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(list).Properties.IsRightButtonPressed) return;
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is { } item) item.IsSelected = true;
        }, RoutingStrategies.Tunnel);

    private async void ExportProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var proceed = await Dialogs.ConfirmAsync(this, "Export profile",
            "The export contains your private identity key, your private room keys and your message history. " +
            "Anyone with the file can impersonate you and read your private rooms.\n\nKeep it somewhere safe. Continue?",
            "Continue", "Cancel");
        if (!proceed) return;

        var path = await Dialogs.PickSaveAsync(this, "Export profile",
            $"Yaparr-profile-{_settings.DisplayName}-{DateTime.Now:yyyyMMdd}.zip", Dialogs.Zip);
        if (path == null) return;

        try
        {
            _data.ExportProfile(path);
            StatusText.Text = "Profile exported to " + path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Export failed: " + ex.Message;
        }
    }

    private async void ImportProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var path = await Dialogs.PickOpenAsync(this, "Import profile", Dialogs.Zip);
        if (path == null) return;

        string staging;
        try { staging = _data.StageProfile(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, "Import profile", ex.Message);
            return;
        }

        var proceed = await Dialogs.ConfirmAsync(this, "Import profile",
            "Importing replaces your current identity, private rooms and message history with the ones in the file. " +
            "Your current profile will be lost unless you exported it first.\n\nYaparr will restart. Continue?",
            "Continue", "Cancel");
        if (!proceed)
        {
            Directory.Delete(staging, recursive: true);
            return;
        }

        Shutdown();
        try
        {
            _data.ApplyStagedProfile(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, "Import profile", "Import failed: " + ex.Message);
        }

        var args = Environment.GetCommandLineArgs().Skip(1).Select(a => $"\"{a}\"");
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe, string.Join(' ', args)) { UseShellExecute = false });
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e) => await CheckForUpdateAsync(manual: true);

    private async void OpenLog_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Launcher.LaunchFileInfoAsync(new FileInfo(_log.Path)))
                await Dialogs.MessageAsync(this, "Yaparr", "The log is at " + _log.Path);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await Dialogs.MessageAsync(this, "Yaparr", "The log is at " + _log.Path);
        }
    }

    private async void About_Click(object? sender, RoutedEventArgs e) =>
        await Dialogs.MessageAsync(this, "About Yaparr", $"Yaparr {Updater.CurrentVersion}\nServerless peer-to-peer chat over the BitTorrent DHT.");

    private async Task CheckForUpdateAsync(bool manual = false)
    {
        try
        {
            if (manual && Updater.Repository is null)
            {
                StatusText.Text = "Updates are not available in this build.";
                return;
            }

            var update = await Updater.CheckAsync(_cts.Token);
            if (update is null)
            {
                if (manual) StatusText.Text = $"Yaparr {Updater.CurrentVersion} is up to date.";
                return;
            }

            var notes = string.IsNullOrWhiteSpace(update.Notes) ? "" : $"\n\n{update.Notes.Trim()}";
            var answer = await Dialogs.ConfirmAsync(this, "Update available",
                $"Yaparr {update.Version} is available (you have {Updater.CurrentVersion}). Update now?{notes}");
            if (!answer) return;

            StatusText.Text = $"Downloading Yaparr {update.Version}…";
            var path = await Updater.DownloadAsync(update, _cts.Token);
            if (!await Updater.InstallAsync(path, _cts.Token))
            {
                StatusText.Text = "The update was not installed.";
                return;
            }
            if (!OperatingSystem.IsWindows() && Environment.ProcessPath is { } exe)
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "Update failed: " + ex.Message;
        }
    }

    private async Task StartAsync()
    {
        var args = Environment.GetCommandLineArgs();
        string? Arg(string flag) => Array.IndexOf(args, flag) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        var dataArg = Arg("--data");
        if (dataArg == null) AppData.MigrateLegacyDefault();
        _data = new AppData(dataArg ?? AppData.DefaultDirectory);
        _settings = _data.LoadSettings();
        _vpn = new VpnController(_data, _settings, () => _node);
        _log = new FileLog(Path.Combine(_data.Directory, "yaparr.log"));
        _log.Write($"Yaparr {Updater.CurrentVersion} starting");

        var name = Arg("--name") ?? _settings.DisplayName
                   ?? await PromptDialog.AskAsync(this, "Choose a display name (others will see it next to your messages):", Environment.UserName);
        if (string.IsNullOrWhiteSpace(name)) { Close(); return; }
        _settings.DisplayName = name;
        _data.SaveSettings(_settings);

        var identity = _data.LoadOrCreateIdentity();
        var traversal = TraversalOptions.Load(Path.Combine(_data.Directory, "network.json"));
        _node = new ChatNode(new ChatNodeOptions
        {
            DisplayName = name,
            Identity = identity,
            HistoryStore = new FileHistoryStore(_data.HistoryDirectory),
            Traversal = traversal,
            EnableLocalDiscovery = traversal.LocalDiscovery,
            PeerCachePath = Path.Combine(_data.Directory, "peers.json"),
            Dht = new Yaparr.Discovery.Dht.DhtOptions { NodeCachePath = Path.Combine(_data.Directory, "dht-nodes.txt") },
            Vpn = _vpn.LoadConfigIfEnabled(),
            Log = _log.Write,
        });
        _node.NetworkStatusChanged += _ => UI(UpdateStatus);
        if (OperatingSystem.IsWindows()) Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _linkTimer.Tick += (_, _) => RefreshPeers();
        _linkTimer.Start();
        _node.VpnStateChanged += _ => UI(UpdateStatus);
        _node.PeerConnected += p => UI(() => { Remember(p); RefreshPeers(); UpdateStatus(); });
        _node.PeerDisconnected += p => UI(() => { Remember(p); RefreshPeers(p.Id); UpdateStatus(); });
        _node.IncomingDirectChat += chat => _ = AddDirect(chat);

        Title = $"Yaparr — {name}";
        NameText.Text = name;
        ProfileInitial.Text = Avatars.Initial(name);
        ProfileAvatar.Background = Avatars.BrushFor(name);
        IdText.Text = _node.Id.Short;
        ProfileIdText.Text = _node.Id.Value;
        _node.Start();
        UpdateStatus();
        RefreshPeers();

        foreach (var priv in _settings.PrivateRooms.ToList())
        {
            try { await JoinPrivateAsync(priv.Label, RoomKey.Parse(priv.Key), persist: false); }
            catch (FormatException) { }
        }
    }

    private void Shutdown()
    {
        _linkTimer.Stop();
        if (OperatingSystem.IsWindows()) Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _cts.Cancel();
        var node = _node;
        _node = null;
        node?.Dispose();
    }

    private static void UI(Action action) => Dispatcher.UIThread.Post(action);

    private void UpdateStatus()
    {
        if (_node == null) return;
        int peers = _node.ConnectedPeers.Count;
        var vpn = _node.VpnState;
        var network = _node.NetworkStatus != NetworkStatus.Unknown ? _node.NetworkStatus.Describe() : null;

        ConnSummary.Text = peers == 0
            ? "Looking for peers…"
            : $"{peers} peer{(peers == 1 ? "" : "s")} connected" + (vpn == Yaparr.Vpn.VpnState.Connecting ? " · VPN connecting…" : "");

        var lines = new List<string> { $"{peers} peer{(peers == 1 ? "" : "s")} connected", $"UDP port {_node.LocalPort}" };
        if (network != null) lines.Add(network);
        lines.Add($"VPN: {vpn}");
        if (peers == 0) lines.Add("Looking for peers. The first search can take a while.");
        ConnectionText.Text = string.Join("\n", lines);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) _node?.NotifyNetworkChanged();
    }

    private async void Vpn_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        await new VpnDialog(_vpn).ShowDialog(this);
    }

    private async void Turn_Click(object? sender, RoutedEventArgs e)
    {
        await new TurnDialog(_node, Path.Combine(_data.Directory, "network.json")).ShowDialog(this);
    }

    private async void CreateRoom_Click(object? sender, RoutedEventArgs e)
    {
        var label = await PromptDialog.AskAsync(this, "A name for the new private room (only you see it):", "Private room");
        if (string.IsNullOrWhiteSpace(label)) return;
        await JoinPrivateAsync(label, RoomKey.Generate(), persist: true);
    }

    private async void JoinRoom_Click(object? sender, RoutedEventArgs e)
    {
        var initial = await ClipboardInviteAsync() ?? "";
        RoomKey key;
        while (true)
        {
            var invite = await PromptDialog.AskAsync(this, "Paste the invite you were sent:", initial);
            if (string.IsNullOrWhiteSpace(invite)) return;
            try { key = RoomKey.Parse(invite); break; }
            catch (FormatException)
            {
                await Dialogs.MessageAsync(this, "Yaparr", "That doesn't look like a valid invite. Check that you copied all of it.");
                initial = invite;
            }
        }

        if (_settings.PrivateRooms.Any(r => r.Key == key.ToString()))
        {
            await Dialogs.MessageAsync(this, "Yaparr", "You are already in that room.");
            return;
        }

        var label = await PromptDialog.AskAsync(this, "A name for this private room (only you see it):", "Private room");
        if (string.IsNullOrWhiteSpace(label)) return;
        await JoinPrivateAsync(label, key, persist: true);
    }

    private const string PrivatePrefix = "(private) ";

    private void MessageMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (MessageList.SelectedItem is not MessageVm { CanRetry: true }) e.Cancel = true;
    }

    private void ConversationMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (_selected is not { IsRoom: true }) e.Cancel = true;
    }

    private void RetryMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is { Retry: { } retry } && MessageList.SelectedItem is MessageVm { CanRetry: true, Id: { } id }) retry(id);
    }

    private async void CheckNetwork_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var dialog = new NetworkCheckDialog(_node);
        dialog.Show(this);
        await dialog.RunAsync();
    }

    private async void RenameRoom_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { IsRoom: true } conv) return;
        var current = conv.Title.StartsWith(PrivatePrefix) ? conv.Title[PrivatePrefix.Length..] : conv.Title;
        var label = await PromptDialog.AskAsync(this, "A new name for this room (only you see it):", current);
        if (string.IsNullOrWhiteSpace(label) || label == current) return;

        conv.Title = PrivatePrefix + label;
        HeaderText.Text = conv.Name;
        if (_settings.PrivateRooms.FirstOrDefault(r => r.Key == conv.Invite) is { } saved)
        {
            saved.Label = label;
            _data.SaveSettings(_settings);
        }
    }

    private async void LeaveRoom_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { IsRoom: true } conv) return;
        var answer = await Dialogs.ConfirmAsync(this, "Leave room",
            $"Leave \"{conv.Title}\"? You will stop receiving its messages. To come back you need the invite again.");
        if (!answer) return;

        var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
        if (room != null)
        {
            await room.LeaveAsync();
            _roomConversations.Remove(room);
        }
        _conversations.Remove(conv);
        if (_settings.PrivateRooms.RemoveAll(r => r.Key == conv.Invite) > 0) _data.SaveSettings(_settings);
        RefreshPeers();
        StatusText.Text = $"Left {conv.Title}.";
    }

    private async Task<string?> ClipboardInviteAsync()
    {
        var text = await Dialogs.ClipboardTextAsync(this);
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            RoomKey.Parse(text);
            return text;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task JoinPrivateAsync(string label, RoomKey key, bool persist)
    {
        if (_node == null) return;
        var room = await _node.JoinPrivateRoomAsync(key, label);
        if (persist)
        {
            _settings.PrivateRooms.Add(new PrivateRoomSetting { Label = label, Key = key.ToString() });
            _data.SaveSettings(_settings);
        }
        var conv = AddRoom(room, PrivatePrefix + label, invite: key.ToString());
        if (persist) conv.Add(MessageVm.System("Share the invite (button above) with people you want in this room. Anyone who has it can join."));
    }

    private ConversationVm AddRoom(ChatRoom room, string title, string? invite)
    {
        if (_roomConversations.TryGetValue(room, out var existing)) { ConversationList.SelectedItem = existing; return existing; }

        var conv = new ConversationVm(title, isRoom: true, room.SendAsync, room.Retry) { Invite = invite };
        _roomConversations[room] = conv;
        room.DeliveryChanged += s => UI(() => conv.ApplyDelivery(s));

        room.MessageReceived += m => UI(() => OnMessage(conv, ToVm(m)));
        room.MemberJoined += p => UI(() => { Remember(p); RefreshPeers(); conv.Add(MessageVm.System($"{p.DisplayName} joined")); });
        room.MemberLeft += p => UI(() => { Remember(p); RefreshPeers(p.Id); conv.Add(MessageVm.System($"{p.DisplayName} left")); });

        foreach (var m in room.GetHistory()) conv.Add(ToVm(m));
        _conversations.Add(conv);
        ConversationList.SelectedItem = conv;
        RefreshPeers();
        return conv;
    }

    private void Remember(PeerInfo peer)
    {
        if (_node == null || peer.Id == _node.Id) return;
        var known = _settings.KnownPeers.FirstOrDefault(k => k.Id == peer.Id.Value);
        if (known == null)
        {
            _settings.KnownPeers.Add(new KnownPeer { Id = peer.Id.Value, Name = peer.DisplayName, LastSeen = DateTimeOffset.Now });
            _data.SaveSettings(_settings);
        }
        else if (known.Name != peer.DisplayName)
        {
            known.Name = peer.DisplayName;
            _data.SaveSettings(_settings);
        }
    }

    private void RefreshPeers(PeerId? gone = null)
    {
        if (_node == null) return;

        var live = new Dictionary<string, PeerInfo>();
        foreach (var p in _node.ConnectedPeers) live[p.Id.Value] = p;
        foreach (var room in _roomConversations.Keys)
            foreach (var p in room.Members) live.TryAdd(p.Id.Value, p);
        if (gone is { } g) live.Remove(g.Value);
        live.Remove(_node.Id.Value);

        var now = DateTimeOffset.Now;
        bool changed = false;
        foreach (var known in _settings.KnownPeers)
        {
            if (live.ContainsKey(known.Id)) { known.LastSeen = now; }
        }
        if (gone is { } left && _settings.KnownPeers.FirstOrDefault(k => k.Id == left.Value) is { } k2)
        {
            k2.LastSeen = now;
            changed = true;
        }
        if (changed) _data.SaveSettings(_settings);

        var online = live.Values.Select(p => new PeerVm(p.Id.Value, p.DisplayName, true, now, p.IsRelayed, _node.GetLink(p.Id)));
        var offline = _settings.KnownPeers
            .Where(k => !live.ContainsKey(k.Id) && k.Id != _node.Id.Value)
            .Select(k => new PeerVm(k.Id, string.IsNullOrWhiteSpace(k.Name) ? k.Id[..8] : k.Name, false, k.LastSeen, false, _node.GetLink(new PeerId(k.Id))));
        var people = online.Concat(offline)
            .OrderByDescending(p => p.Online)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        PeopleList.ItemsSource = people;
        PeopleCount.Text = $"{people.Count(p => p.Online)} online";
        RefreshMembers();
    }

    private async void DirectChat_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var text = await PromptDialog.AskAsync(this, "Peer ID of the person to chat with (64 characters; they can copy it from their app):");
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.ToLowerInvariant();
        if (text.Length != 64 || !text.All(Uri.IsHexDigit)) { await Dialogs.MessageAsync(this, "Yaparr", "A peer ID is 64 hex characters."); return; }
        if (text == _node.Id.Value) { await Dialogs.MessageAsync(this, "Yaparr", "That's your own ID."); return; }

        var id = new PeerId(text);
        StatusText.Text = $"Looking for {id.Short}… this can take a minute if they just came online.";
        try
        {
            var chat = await _node.OpenDirectChatAsync(id, _cts.Token);
            await AddDirect(chat);
            UpdateStatus();
        }
        catch (OperationCanceledException) { }
    }

    private Task AddDirect(DirectChat chat)
    {
        var tcs = new TaskCompletionSource();
        ConversationVm conv;
        lock (_directConversations)
        {
            if (_directConversations.TryGetValue(chat.PeerId, out var existing))
            {
                UI(() => { ConversationList.SelectedItem = existing; tcs.SetResult(); });
                return tcs.Task;
            }
            var title = (chat.Peer?.DisplayName ?? chat.PeerId.Short) + " (direct)";
            conv = new ConversationVm(title, isRoom: false, chat.SendAsync, chat.Retry) { PeerId = chat.PeerId.Value };
            chat.DeliveryChanged += s => UI(() => conv.ApplyDelivery(s));
            _directConversations[chat.PeerId] = conv;
        }

        chat.MessageReceived += m => UI(() => OnMessage(conv, ToVm(m)));
        UI(() =>
        {
            if (chat.Peer != null) Remember(chat.Peer);
            _conversations.Add(conv);
            RefreshPeers();
            if (_selected == null) ConversationList.SelectedItem = conv;
            tcs.SetResult();
        });
        return tcs.Task;
    }

    private MessageVm ToVm(ChatMessage m)
    {
        Remember(m.From);
        return new(m.Timestamp, m.From.DisplayName, m.Text, m.IsCatchUp, isMine: _node != null && m.From.Id == _node.Id, id: m.Id);
    }

    private void OnMessage(ConversationVm conv, MessageVm message)
    {
        conv.Add(message);
        if (conv == _selected) ScrollToEnd();
        else conv.Unread++;
    }

    private void ScrollToEnd()
    {
        if (_selected is { Messages.Count: > 0 } conv)
            Dispatcher.UIThread.Post(() => MessageList.ScrollIntoView(conv.Messages[^1]), DispatcherPriority.Background);
    }

    private async void Send_Click(object? sender, RoutedEventArgs e) => await SendAsync();

    private async void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; await SendAsync(); }
    }

    private async Task SendAsync()
    {
        var conv = _selected;
        var text = InputBox.Text?.Trim() ?? "";
        if (conv == null || text.Length == 0 || _node == null) return;

        try
        {
            var id = await conv.Send(text);
            InputBox.Text = "";
            conv.Add(new MessageVm(DateTimeOffset.Now, _node.DisplayName, text, isMine: true, id: id));
            ScrollToEnd();
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void ConversationList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selected = ConversationList.SelectedItem as ConversationVm;
        bool has = _selected != null;
        InputBox.IsEnabled = SendButton.IsEnabled = has;
        MessageList.ItemsSource = _selected?.Messages;
        HeaderText.Text = _selected?.Name ?? "Welcome to Yaparr";
        HeaderSubtitle.Text = _selected?.Subtitle ?? "Serverless chat between friends";
        EmptyPanel.IsVisible = !has;
        DetailsPanel.DataContext = _selected;
        RoomToggle.IsEnabled = has;
        if (!has) RoomToggle.IsChecked = false;
        RefreshMembers();
        if (_selected != null)
        {
            _selected.Unread = 0;
            ScrollToEnd();
            InputBox.Focus();
        }
    }

    private async void CopyId_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        StatusText.Text = await Dialogs.CopyAsync(this, _node.Id.Value)
            ? "Your peer ID was copied to the clipboard."
            : "Couldn't copy: another program is using the clipboard. Try again.";
    }

    private void RefreshMembers()
    {
        if (_selected is not { IsRoom: true } conv) return;
        var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
        if (room == null) return;
        int others = room.Members.Count(m => m.Id != _node?.Id);
        conv.MembersText = others == 0 ? "Only you so far" : $"You and {others} other{(others == 1 ? "" : "s")} online";
    }

    private async void CopyPeerId_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected?.PeerId is not { } id) return;
        StatusText.Text = await Dialogs.CopyAsync(this, id)
            ? "Their peer ID was copied to the clipboard."
            : "Couldn't copy: another program is using the clipboard. Try again.";
    }

    private async void CopyInvite_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected?.Invite is not { } invite) return;
        StatusText.Text = await Dialogs.CopyAsync(this, invite)
            ? "Invite copied. Anyone who has it can join this private room, so share it carefully."
            : "Couldn't copy: another program is using the clipboard. Try again.";
    }
}
