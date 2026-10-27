using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Wpf;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ConversationVm> _conversations = new();
    private readonly Dictionary<ChatRoom, ConversationVm> _roomConversations = new();
    private readonly Dictionary<PeerId, ConversationVm> _directConversations = new();
    private readonly CancellationTokenSource _cts = new();
    private AppData _data = null!;
    private AppSettings _settings = new();
    private VpnController _vpn = null!;
    private FileLog _log = null!;
    private ChatNode? _node;
    private ConversationVm? _selected;

    public MainWindow()
    {
        InitializeComponent();
        ConversationList.ItemsSource = _conversations;
        VersionText.Text = "v" + Updater.CurrentVersion;
        Loaded += async (_, _) =>
        {
            _ = CheckForUpdateAsync();
            await StartAsync();
        };
        Closing += (_, _) => Shutdown();
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var proceed = MessageBox.Show(this,
            "The export contains your private identity key, your private room keys and your message history. " +
            "Anyone with the file can impersonate you and read your private rooms.\n\nKeep it somewhere safe. Continue?",
            "Export profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (proceed != MessageBoxResult.Yes) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export profile",
            FileName = $"Yaparr-profile-{_settings.DisplayName}-{DateTime.Now:yyyyMMdd}.zip",
            Filter = "Zip archive (*.zip)|*.zip",
            DefaultExt = ".zip",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _data.ExportProfile(dialog.FileName);
            StatusText.Text = "Profile exported to " + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Export failed: " + ex.Message;
        }
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import profile",
            Filter = "Zip archive (*.zip)|*.zip",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        string staging;
        try { staging = _data.StageProfile(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Import profile", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var proceed = MessageBox.Show(this,
            "Importing replaces your current identity, private rooms and message history with the ones in the file. " +
            "Your current profile will be lost unless you exported it first.\n\nYaparr will restart. Continue?",
            "Import profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (proceed != MessageBoxResult.Yes)
        {
            System.IO.Directory.Delete(staging, recursive: true);
            return;
        }

        Shutdown();
        try
        {
            _data.ApplyStagedProfile(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Import failed: " + ex.Message, "Import profile", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        var args = Environment.GetCommandLineArgs().Skip(1).Select(a => $"\"{a}\"");
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe, string.Join(' ', args)) { UseShellExecute = false });
        Application.Current.Shutdown();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(manual: true);

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_log.Path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            MessageBox.Show(this, "The log is at " + _log.Path, "Yaparr");
        }
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, $"Yaparr {Updater.CurrentVersion}\nServerless peer-to-peer chat over the BitTorrent DHT.",
            "About Yaparr", MessageBoxButton.OK, MessageBoxImage.Information);

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
            var answer = MessageBox.Show(this,
                $"Yaparr {update.Version} is available (you have {Updater.CurrentVersion}). Update now?{notes}",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;

            StatusText.Text = $"Downloading Yaparr {update.Version}…";
            var path = await Updater.DownloadAsync(update, _cts.Token);
            Updater.Install(path);
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
                   ?? PromptDialog.Ask(this, "Choose a display name (others will see it next to your messages):", Environment.UserName);
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
            Vpn = _vpn.LoadConfigIfEnabled(),
            Log = _log.Write,
        });
        _node.NetworkStatusChanged += _ => UI(UpdateStatus);
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _node.VpnStateChanged += _ => UI(UpdateStatus);
        _node.PeerConnected += p => UI(() => { Remember(p); RefreshPeers(); UpdateStatus(); });
        _node.PeerDisconnected += p => UI(() => { Remember(p); RefreshPeers(p.Id); UpdateStatus(); });
        _node.IncomingDirectChat += chat => _ = AddDirect(chat);

        Title = $"Yaparr — {name}";
        NameText.Text = name;
        IdText.Text = _node.Id.Value;
        IdText.ToolTip = _node.Id.Value;
        _node.Start();
        UpdateStatus();
        RefreshPeers();

        foreach (var priv in _settings.PrivateRooms.ToList())
        {
            try { await JoinPrivateAsync(priv.Label, RoomKey.Parse(priv.Key), persist: false); }
            catch (FormatException) {  }
        }
    }

    private void Shutdown()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _cts.Cancel();
        var node = _node;
        _node = null;
        node?.Dispose();
    }

    private void UI(Action action) => Dispatcher.BeginInvoke(action);

    private void UpdateStatus()
    {
        if (_node == null) return;
        int peers = _node.ConnectedPeers.Count;
        StatusText.Text = $"UDP port {_node.LocalPort}  ·  {peers} peer{(peers == 1 ? "" : "s")} connected" +
                          (peers == 0 ? "  ·  looking for peers (the first search can take a while)…" : "") +
                          (_node.VpnState == Yaparr.Vpn.VpnState.Connecting ? "  ·  VPN connecting…" : "") +
                          (_node.NetworkStatus != NetworkStatus.Unknown ? "  ·  " + _node.NetworkStatus.Describe() : "");
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) _node?.NotifyNetworkChanged();
    }

    private void Vpn_Click(object sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        new VpnDialog(_vpn) { Owner = this }.ShowDialog();
    }

    private void Turn_Click(object sender, RoutedEventArgs e)
    {
        new TurnDialog(_node, Path.Combine(_data.Directory, "network.json")) { Owner = this }.ShowDialog();
    }

    private async void CreateRoom_Click(object sender, RoutedEventArgs e)
    {
        var label = PromptDialog.Ask(this, "A name for the new private room (only you see it):", "Private room");
        if (string.IsNullOrWhiteSpace(label)) return;
        await JoinPrivateAsync(label, RoomKey.Generate(), persist: true);
    }

    private async void JoinRoom_Click(object sender, RoutedEventArgs e)
    {
        var initial = ClipboardInvite() ?? "";
        RoomKey key;
        while (true)
        {
            var invite = PromptDialog.Ask(this, "Paste the invite you were sent:", initial);
            if (string.IsNullOrWhiteSpace(invite)) return;
            try { key = RoomKey.Parse(invite); break; }
            catch (FormatException)
            {
                MessageBox.Show(this, "That doesn't look like a valid invite. Check that you copied all of it.", "Yaparr");
                initial = invite;
            }
        }

        if (_settings.PrivateRooms.Any(r => r.Key == key.ToString()))
        {
            MessageBox.Show(this, "You are already in that room.", "Yaparr");
            return;
        }

        var label = PromptDialog.Ask(this, "A name for this private room (only you see it):", "Private room");
        if (string.IsNullOrWhiteSpace(label)) return;
        await JoinPrivateAsync(label, key, persist: true);
    }

    private const string PrivatePrefix = "(private) ";

    private void ConversationList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not ListBoxItem) source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        if (source is ListBoxItem item) item.IsSelected = true;
    }

    private void ConversationList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_selected is not { IsRoom: true }) e.Handled = true;
    }

    private void RenameRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsRoom: true } conv) return;
        var current = conv.Title.StartsWith(PrivatePrefix) ? conv.Title[PrivatePrefix.Length..] : conv.Title;
        var label = PromptDialog.Ask(this, "A new name for this room (only you see it):", current);
        if (string.IsNullOrWhiteSpace(label) || label == current) return;

        conv.Title = PrivatePrefix + label;
        HeaderText.Text = conv.Title;
        if (_settings.PrivateRooms.FirstOrDefault(r => r.Key == conv.Invite) is { } saved)
        {
            saved.Label = label;
            _data.SaveSettings(_settings);
        }
    }

    private async void LeaveRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsRoom: true } conv) return;
        var answer = MessageBox.Show(this,
            $"Leave \"{conv.Title}\"? You will stop receiving its messages. To come back you need the invite again.",
            "Leave room", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

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

    private static string? ClipboardInvite()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null;
            if (text == null) return null;
            RoomKey.Parse(text);
            return text;
        }
        catch (Exception ex) when (ex is FormatException or System.Runtime.InteropServices.COMException)
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
        var conv = AddRoom(room, "(private) " + label, invite: key.ToString());
        if (persist) conv.Add(MessageVm.System("Share the invite (button above) with people you want in this room. Anyone who has it can join."));
    }

    private ConversationVm AddRoom(ChatRoom room, string title, string? invite)
    {
        if (_roomConversations.TryGetValue(room, out var existing)) { ConversationList.SelectedItem = existing; return existing; }

        var conv = new ConversationVm(title, isRoom: true, room.SendAsync) { Invite = invite };
        _roomConversations[room] = conv;

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

        var online = live.Values.Select(p => new PeerVm(p.Id.Value, p.DisplayName, true, now, p.IsRelayed));
        var offline = _settings.KnownPeers
            .Where(k => !live.ContainsKey(k.Id) && k.Id != _node.Id.Value)
            .Select(k => new PeerVm(k.Id, string.IsNullOrWhiteSpace(k.Name) ? k.Id[..8] : k.Name, false, k.LastSeen));
        var people = online.Concat(offline)
            .OrderByDescending(p => p.Online)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        PeopleList.ItemsSource = people;
        PeopleHeader.Text = $"People  ({people.Count(p => p.Online)} online)";
    }

    private async void DirectChat_Click(object sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var text = PromptDialog.Ask(this, "Peer ID of the person to chat with (64 characters; they can copy it from their app):");
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.ToLowerInvariant();
        if (text.Length != 64 || !text.All(Uri.IsHexDigit)) { MessageBox.Show(this, "A peer ID is 64 hex characters.", "Yaparr"); return; }
        if (text == _node.Id.Value) { MessageBox.Show(this, "That's your own ID.", "Yaparr"); return; }

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
            conv = new ConversationVm(title, isRoom: false, chat.SendAsync);
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
        return new(m.Timestamp, m.From.DisplayName, m.Text, m.IsCatchUp, IsMine: _node != null && m.From.Id == _node.Id, Id: m.Id);
    }

    private void OnMessage(ConversationVm conv, MessageVm message)
    {
        conv.Add(message);
        if (conv == _selected) ScrollToEnd();
        else conv.Unread++;
    }

    private void ScrollToEnd()
    {
        if (MessageList.Items.Count > 0) MessageList.ScrollIntoView(MessageList.Items[^1]);
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SendAsync(); }
    }

    private async Task SendAsync()
    {
        var conv = _selected;
        var text = InputBox.Text.Trim();
        if (conv == null || text.Length == 0 || _node == null) return;

        try
        {
            await conv.Send(text);
            InputBox.Clear();
            conv.Add(new MessageVm(DateTimeOffset.Now, _node.DisplayName, text, IsMine: true));
            ScrollToEnd();
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = ConversationList.SelectedItem as ConversationVm;
        bool has = _selected != null;
        InputBox.IsEnabled = SendButton.IsEnabled = has;
        MessageList.ItemsSource = _selected?.Messages;
        HeaderText.Text = _selected?.Title ?? "Create a private room, join one with an invite, or open a direct chat to begin";
        InviteButton.Visibility = _selected?.HasInvite == true ? Visibility.Visible : Visibility.Collapsed;
        if (_selected != null)
        {
            _selected.Unread = 0;
            ScrollToEnd();
            InputBox.Focus();
        }
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        StatusText.Text = TryCopy(_node.Id.Value)
            ? "Your peer ID was copied to the clipboard."
            : "Couldn't copy: another program is using the clipboard. Try again.";
    }

    private void CopyInvite_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.Invite is not { } invite) return;
        StatusText.Text = TryCopy(invite)
            ? "Invite copied. Anyone who has it can join this private room, so share it carefully."
            : "Couldn't copy: another program is using the clipboard. Try again.";
    }

    private static bool TryCopy(string text)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }
}
