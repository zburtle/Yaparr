using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaparr.Chat;
using Yaparr.Diagnostics;
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
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // The message box handles Ctrl+V itself (and marks it handled), so its own key handler never sees it: look on the way down.
        AddHandler(KeyDownEvent, OnPasteKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPushToTalkKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPushToTalkKey, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPushToTalkPointer, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPushToTalkPointer, RoutingStrategies.Tunnel);
        // A key held while this window loses the focus would never be seen coming up.
        Deactivated += (_, _) => { if (_voice != null && _pushToTalkHook == null) _voice.PushToTalkHeld = false; };
        ConversationList.ItemsSource = _conversations;
        ToolTip.SetTip(InfoIcon, "Yaparr v" + Updater.CurrentVersion);
        ToolTip.SetTip(PeersIcon, "Starting…");
        ToolTip.SetTip(NetworkIcon, "Checking the network…");
        ToolTip.SetTip(VpnIcon, "VPN: off");

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
        SelectOnRightClick(PeopleList);
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

    // A backup is a copy of everything Yaparr keeps for you. It is restored on a computer that has no account yet, from the
    // first-start screen. To use the same account on a second computer at the same time, link it instead.
    private async void BackupProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var proceed = await Dialogs.ConfirmAsync(this, "Back up profile",
            "A backup holds your private identity key, your room keys and your message history. " +
            "Anyone with the file can impersonate you and read your rooms, so keep it somewhere safe.\n\n" +
            "Restore it from the first-start screen if you lose this computer or move to a new one. " +
            "To use your account on a second computer at the same time, use Link another device instead: " +
            "two computers running the same backup would clash.\n\nContinue?",
            "Continue", "Cancel");
        if (!proceed) return;

        var path = await Dialogs.PickSaveAsync(this, "Back up profile",
            $"Yaparr-backup-{_settings.DisplayName}-{DateTime.Now:yyyyMMdd}.zip", Dialogs.Zip);
        if (path == null) return;

        try
        {
            _data.ExportProfile(path);
            StatusText.Text = "Backup saved to " + path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Backup failed: " + ex.Message;
        }
    }

    private bool _restarting;

    // Starts Yaparr again with the same arguments and closes this one.
    private void RestartApp()
    {
        _restarting = true;
        var args = Environment.GetCommandLineArgs().Skip(1).Select(a => $"\"{a}\"");
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe, string.Join(' ', args)) { UseShellExecute = false });
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e) => await CheckForUpdateAsync(manual: true);

    private async void OpenLicences_Click(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        try
        {
            if (!await Launcher.LaunchFileInfoAsync(new FileInfo(path)))
                await Dialogs.MessageAsync(this, "Yaparr", "The licences are in " + path);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await Dialogs.MessageAsync(this, "Yaparr", "The licences are in " + path);
        }
    }

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

    private async void OpenLogFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(_log.Folder)))
                await Dialogs.MessageAsync(this, "Yaparr", "The logs are in " + _log.Folder);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await Dialogs.MessageAsync(this, "Yaparr", "The logs are in " + _log.Folder);
        }
    }

    // Everything a bug report needs, on the clipboard in one go.
    private async void CopyDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) { StatusText.Text = "Yaparr is still starting."; return; }
        StatusText.Text = "Collecting diagnostics…";
        try
        {
            var text = await DiagnosticsReport.BuildAsync(_node, _settings, _log, _data.Directory, _cts.Token);
            StatusText.Text = await Dialogs.CopyAsync(this, text)
                ? "Diagnostics copied. Paste them into your bug report: addresses and ids are shortened and keys are left out."
                : "Couldn't copy: another program is using the clipboard. Try again.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Write("could not collect diagnostics: " + ex);
            StatusText.Text = "Couldn't collect the diagnostics: " + ex.Message;
        }
    }

    // Opens the bug report form on GitHub with the version and system filled in. The diagnostics are too long for a link, so they go
    // on the clipboard to be pasted into the form.
    private async void ReportProblem_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) { StatusText.Text = "Yaparr is still starting."; return; }
        StatusText.Text = "Collecting diagnostics…";
        try
        {
            var text = await DiagnosticsReport.BuildAsync(_node, _settings, _log, _data.Directory, _cts.Token);
            bool copied = await Dialogs.CopyAsync(this, text);
            var link = Updater.IssueFormUrl("bug_report.yml", Updater.CurrentVersion.ToString(), System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            if (!await Launcher.LaunchUriAsync(new Uri(link)))
            {
                await Dialogs.MessageAsync(this, "Report a problem", "Open " + link + " in a browser to report it.");
                return;
            }
            StatusText.Text = copied
                ? "The bug report is open in your browser. Paste the diagnostics (already copied) into its Diagnostics box and read them first."
                : "The bug report is open in your browser. The diagnostics could not be copied; use Help > Copy diagnostics and paste them in.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Write("could not start a problem report: " + ex);
            StatusText.Text = "Couldn't start the report: " + ex.Message;
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
                StatusText.Text = Updater.IsFlatpak
                    ? "Flatpak installs updates itself: use your software centre, or run \"flatpak update\"."
                    : "Updates are not available in this build.";
                return;
            }

            var update = await Updater.CheckAsync(_cts.Token);
            if (update is null)
            {
                if (manual) StatusText.Text = $"Yaparr {Updater.CurrentVersion} is up to date.";
                return;
            }

            var answer = await UpdateDialog.AskAsync(this, update.Version, Updater.CurrentVersion, update.Notes);
            if (!answer) return;

            // A hundred megabytes is a while: show how far it is, and let it be cancelled.
            var size = update.Size > 0 ? $" ({update.Size / 1_048_576} MB)" : "";
            var (path, error) = await DownloadDialog.RunAsync(this, $"Getting Yaparr {update.Version}",
                $"Downloading Yaparr {update.Version}{size}. It is installed when the download is done.",
                async (progress, ct) => await Updater.DownloadAsync(update, ct, progress));
            if (path == null)
            {
                if (error != null) throw new InvalidOperationException(error);
                StatusText.Text = "The update was cancelled.";
                return;
            }
            if (!await Updater.InstallAsync(path, update.Sha256!, _cts.Token))
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
            _log.Write("update failed: " + ex);
            StatusText.Text = "Update failed: " + ex.Message;
            if (manual) await Dialogs.MessageAsync(this, "Update failed", ex.Message);
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
        _log = new FileLog(Path.Combine(_data.Directory, "logs"), legacyFile: Path.Combine(_data.Directory, "yaparr.log"));
        _log.Write($"Yaparr {Updater.CurrentVersion} starting");
        Updater.Log = _log.Write;
        SetUpNotifications();
        SetUpReplies();

        if (Arg("--name") == null && _settings.DisplayName == null && !_data.HasIdentity) await OfferLinkAsync();
        if (_restarting) return;

        var name = Arg("--name") ?? _settings.DisplayName
                   ?? await PromptDialog.AskAsync(this, "Choose a display name (others will see it next to your messages):", Environment.UserName);
        if (string.IsNullOrWhiteSpace(name)) { Close(); return; }
        _settings.DisplayName = name;
        _data.SaveSettings(_settings);

        var identity = _identity = _data.LoadOrCreateIdentity();
        var traversal = TraversalOptions.Load(Path.Combine(_data.Directory, "network.json"));
        // Message history is kept sealed; the key lives in a file of its own that is sealed to this Windows user.
        var historyCipher = HistoryCipher.LoadOrCreate(Path.Combine(_data.Directory, "history.key"));
        _node = new ChatNode(new ChatNodeOptions
        {
            DisplayName = name,
            HistoryCipher = historyCipher,
            BlockedPeers = _settings.BlockedPeers.Select(b => new PeerId(b.Id)).ToList(),
            Identity = identity,
            AccountStatePath = _data.AccountStatePath,
            AccountEntries = _linkedEntries,
            HistoryStore = new FileHistoryStore(_data.HistoryDirectory, historyCipher),
            DirectHistoryDirectory = Path.Combine(_data.Directory, "direct"),
            Traversal = traversal,
            AttachmentDirectory = Path.Combine(_data.Directory, "attachments"),
            SoundboardDirectory = Path.Combine(_data.Directory, "soundboards"),
            RolesDirectory = Path.Combine(_data.Directory, "roles"),
            EnableLocalDiscovery = traversal.LocalDiscovery,
            PeerCachePath = Path.Combine(_data.Directory, "peers.json"),
            ClockOffsetsPath = Path.Combine(_data.Directory, "clocks.json"),
            Dht = new Yaparr.Discovery.Dht.DhtOptions { NodeCachePath = Path.Combine(_data.Directory, "dht-nodes.txt") },
            Vpn = _vpn.LoadConfigIfEnabled(),
            Log = _log.Write,
        });
        _node.NetworkStatusChanged += _ => UI(UpdateStatus);
        _node.AttachmentChanged += update => UI(() => OnAttachmentChanged(update));
        if (OperatingSystem.IsWindows()) Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _linkTimer.Tick += (_, _) => { RefreshPeers(); WatchConnection(); SaveMarks(); };
        SetUpConnectionWatch();
        _node.NetworkChanged += () => _call.NetworkChanged(Environment.TickCount64);
        _linkTimer.Start();
        _node.VpnStateChanged += _ => UI(UpdateStatus);
        _node.BlockChanged += (id, blocked) => UI(() => OnBlockChanged(id, blocked));
        _node.PeerRenamed += p => UI(() => OnPeerRenamed(p));
        _node.AccountLearned += _ => UI(ClaimOwnMessages);
        _node.SyncedRoomChanged += OnSyncedRoomChanged;
        _node.DisplayNameChanged += OnDisplayNameChanged;
        _node.PeerConnected += p => UI(() => { Remember(p); RefreshPeers(); UpdateStatus(); });
        _node.PeerDisconnected += p => UI(() => { Remember(p); RefreshPeers(p.Account); UpdateStatus(); });
        _node.IncomingDirectChat += chat => _ = AddDirect(chat);

        ShowOwnName(name);
        // The id to share is the account's, which is the same on every one of your devices.
        IdText.Text = _node.AccountId.Short;
        ProfileIdText.Text = _node.AccountId.Value;
        ReconcileFromAccount();
        _node.Start();
        UpdateStatus();
        RefreshPeers();
        foreach (var stored in _node.StoredDirectChats) _ = AddDirect(stored);

        // Opening each room as it comes back is not reading it: what was unread when the app was closed stays unread.
        _restoringRooms = true;
        try
        {
            foreach (var priv in _settings.PrivateRooms.ToList())
            {
                try { await JoinKeyedRoomAsync(priv.Label, RoomKey.Parse(priv.Key), persist: false); }
                catch (FormatException) { }
            }
        }
        finally { _restoringRooms = false; }
        // The room that ends up on show is the one being looked at.
        _selected?.Opened();
        MarkShownRead(_selected);
        if (!_settings.ConnectionGuideShown) await ShowConnectionGuideAsync();
    }

    private void Shutdown()
    {
        _linkTimer.Stop();
        MarkShownRead(_selected);
        SaveMarks();
        if (OperatingSystem.IsWindows()) Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        LeaveVoiceCore();
        CloseScreens();
        _cts.Cancel();
        var node = _node;
        _node = null;
        node?.Dispose();
    }

    private static void UI(Action action) => Dispatcher.UIThread.Post(action);

    private void UpdateStatus()
    {
        if (_node == null) return;
        WatchConnection();
        int peers = _node.ConnectedPeers.Count;
        var vpn = _node.VpnState;
        var network = _node.NetworkStatus != NetworkStatus.Unknown ? _node.NetworkStatus.Describe() : null;

        // Green when it is working, orange when it is not yet or only partly, grey when there is nothing to say.
        PeersIcon.Text = $"\U0001F465 {peers}";
        PeersIcon.Foreground = StatusBrush(peers > 0 ? "Good" : "Warn");
        ToolTip.SetTip(PeersIcon, peers == 0
            ? "No peers connected. Looking for peers; the first search can take a while."
            : $"{peers} peer{(peers == 1 ? "" : "s")} connected");

        NetworkIcon.Foreground = StatusBrush(network == null ? "Muted" : _node.NetworkStatus.Reachable ? "Good" : "Warn");
        ToolTip.SetTip(NetworkIcon, $"UDP port {_node.LocalPort}\n" + (network ?? "Checking the network…")
            + (network != null && !_node.NetworkStatus.Reachable ? "\nOther people may not be able to connect to you directly." : ""));

        VpnIcon.Foreground = StatusBrush(vpn switch
        {
            Yaparr.Vpn.VpnState.Connected => "Good",
            Yaparr.Vpn.VpnState.Connecting => "Warn",
            _ => "Muted",
        });
        ToolTip.SetTip(VpnIcon, "VPN: " + vpn switch
        {
            Yaparr.Vpn.VpnState.Connected => "connected",
            Yaparr.Vpn.VpnState.Connecting => "connecting…",
            _ => "off",
        });
    }

    // A colour of the current theme, by the name it has in App.axaml.
    private Avalonia.Media.IBrush StatusBrush(string key) =>
        this.TryGetResource(key, ActualThemeVariant, out var found) && found is Avalonia.Media.IBrush brush ? brush : Avalonia.Media.Brushes.Gray;

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
        var label = await PromptDialog.AskAsync(this, "A name for the new room (only you see it):", "Room");
        if (string.IsNullOrWhiteSpace(label)) return;
        // The invite names you as the owner, which is how everyone in the room knows who may appoint admins.
        if (_node == null) return;
        await JoinKeyedRoomAsync(label, RoomKey.Generate(_node.AccountId), persist: true);
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

        if (_settings.PrivateRooms.FirstOrDefault(r => r.Key == key.ToString() || RoomIdOf(r.Key) == key.RoomId) is { } have)
        {
            // The same room under a newer key (the owner replaced it): take the new key.
            if (key.Epoch > EpochOf(have.Key) && _node != null)
            {
                await _node.JoinRoomAsync(key, have.Label);
                StatusText.Text = "Your key for that room is now the new one.";
                return;
            }
            await Dialogs.MessageAsync(this, "Yaparr", "You are already in that room.");
            return;
        }

        var label = await PromptDialog.AskAsync(this, "A name for this room (only you see it):", "Room");
        if (string.IsNullOrWhiteSpace(label)) return;
        await JoinKeyedRoomAsync(label, key, persist: true);
    }

    private const string PrivatePrefix = "";

    private void ConversationMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (_selected is not { IsRoom: true }) e.Cancel = true;
    }

    private void RetryMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is { Retry: { } retry } && MessageList.SelectedItem is MessageVm { CanRetry: true, Id: { } id }) retry(id);
    }

    private async void ConnectionGuide_Click(object? sender, RoutedEventArgs e) => await ShowConnectionGuideAsync();

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

        await LeaveConversationAsync(conv);
        StatusText.Text = $"Left {conv.Title}.";
    }

    private async Task LeaveConversationAsync(ConversationVm conv)
    {
        var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
        if (room != null)
        {
            await room.LeaveAsync();
            _roomConversations.Remove(room);
        }
        _conversations.Remove(conv);
        if (_settings.PrivateRooms.RemoveAll(r => r.Key == conv.Invite) > 0) _data.SaveSettings(_settings);
        RefreshPeers();
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

    private async Task JoinKeyedRoomAsync(string label, RoomKey key, bool persist)
    {
        if (_node == null) return;
        var room = await _node.JoinRoomAsync(key, label);
        if (persist)
        {
            _settings.PrivateRooms.Add(new PrivateRoomSetting { Label = label, Key = key.ToString() });
            _data.SaveSettings(_settings);
        }
        var conv = AddRoom(room, PrivatePrefix + label, invite: key.ToString());
        if (persist) conv.Add(MessageVm.System("Share the invite (button above) with people you want in this room. Anyone who has it can join."));
        if (persist && key.Owner != null && key.Owner == _node.AccountId)
            conv.Add(MessageVm.System("You own this room, and that cannot be handed over or taken back. If a computer or phone you use Yaparr on is lost or stolen, whoever has it can act as you here, including appointing admins. If that happens, make a new room."));
    }

    private ConversationVm AddRoom(ChatRoom room, string title, string? invite)
    {
        if (_roomConversations.TryGetValue(room, out var existing)) { ConversationList.SelectedItem = existing; return existing; }

        ConversationVm conv = null!;
        // A message goes to the text channel on show; the first one needs no channel named.
        conv = new ConversationVm(title, isRoom: true,
            text => conv.CurrentChannel == RoomChannels.DefaultText ? room.SendAsync(text) : room.SendAsync(text, conv.CurrentChannel),
            room.Retry) { Invite = invite };
        _roomConversations[room] = conv;
        room.DeliveryChanged += s => UI(() => conv.ApplyDelivery(s, room.GetDeliveryDetails(s.MessageId)));

        room.MessageReceived += m => UI(() => OnMessage(conv, ToVm(m), announce: !m.IsCatchUp));
        room.VoiceChanged += () => UI(() => RefreshVoice(conv, room));
        room.VoiceTakenOver += _ => UI(() =>
        {
            if (_voiceConv == conv) LeaveVoiceCore();
            StatusText.Text = "You joined voice on another of your devices, so you left it here.";
        });
        room.MediaGroupsChanged += () => UI(() => RefreshShares(conv, room));
        room.MemberRenamed += p => UI(() => { Remember(p); RefreshPeers(); });
        room.MemberJoined += p => UI(() => { Remember(p); RefreshPeers(); });
        room.MemberLeft += p => UI(() => { Remember(p); RefreshPeers(p.Account); });
        room.RolesChanged += () => UI(() => { RefreshPeers(); RefreshMembers(); });
        room.ChannelsChanged += () => UI(() => RefreshChannels(conv, room));
        room.SecretChanged += (before, after) => UI(() => OnSecretChanged(conv, before, after));
        room.ModerationChanged += () => UI(() => { conv.Reload(room.GetHistory().Select(ToVm).ToList()); RefreshPeers(); RefreshMembers(); });

        if (_settings.RoomChannels.TryGetValue(room.RoomId, out var wantedChannel)) conv.PendingChannel = wantedChannel;
        RefreshChannels(conv, room);
        // What came while the app was shut counts as unread when it is newer than what was seen.
        foreach (var m in room.GetHistory())
        {
            var vm = ToVm(m);
            conv.Add(vm);
            CountIfUnread(conv, room, vm);
        }
        _conversations.Add(conv);
        ConversationList.SelectedItem = conv;
        RefreshPeers();
        return conv;
    }

    private void Remember(PeerInfo peer)
    {
        if (_node == null || _node.IsMyDevice(peer.Id)) return;
        var known = _settings.KnownPeers.FirstOrDefault(k => k.Id == peer.Account.Value);
        if (known == null)
        {
            _settings.KnownPeers.Add(new KnownPeer { Id = peer.Account.Value, Name = peer.DisplayName, LastSeen = DateTimeOffset.Now });
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

        // One entry per person, however many devices they are on; your own other devices are not listed.
        var live = new Dictionary<string, PeerInfo>();
        void Add(PeerInfo p)
        {
            if (_node.IsMyDevice(p.Id)) return;
            if (!live.TryGetValue(p.Account.Value, out var seen) || (seen.IsRelayed && !p.IsRelayed)) live[p.Account.Value] = p;
        }
        foreach (var p in _node.ConnectedPeers) Add(p);
        foreach (var room in _roomConversations.Keys)
            foreach (var p in room.Members) Add(p);
        if (gone is { } g && !_node.ConnectedPeers.Any(c => c.Account == g)) live.Remove(g.Value);

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

        var blocked = _settings.BlockedPeers.Select(b => b.Id).ToHashSet();

        // The list is the people of the room that is open. For a direct chat there is no list, and the panel is closed.
        var open = SelectedRoom;
        PeoplePanel.IsVisible = open != null;
        if (open == null)
        {
            PeopleList.ItemsSource = null;
            PeopleCount.Text = "";
            RefreshMembers();
            return;
        }

        // One entry per person, however many devices they are on; your own other devices are not listed.
        var here = new Dictionary<string, PeerInfo>();
        foreach (var p in open.Members)
        {
            if (_node.IsMyDevice(p.Id)) continue;
            if (!here.TryGetValue(p.Account.Value, out var seen) || (seen.IsRelayed && !p.IsRelayed)) here[p.Account.Value] = p;
        }
        if (gone is { } away && !_node.ConnectedPeers.Any(c => c.Account == away)) here.Remove(away.Value);

        // Roles belong to a room, so the list shows them for the room that is open.
        RoomRole RoleOf(string id) => open.RoleOf(new PeerId(id));
        var seenHere = open.People;
        var online = here.Values.Where(p => !blocked.Contains(p.Account.Value))
            .Select(p => new PeerVm(p.Account.Value, p.DisplayName, true, now, p.IsRelayed, _node.GetLink(p.Account), Role: RoleOf(p.Account.Value), BannedHere: open.IsBanned(p.Account)));
        var offline = seenHere
            .Where(k => !here.ContainsKey(k.Account.Value) && !blocked.Contains(k.Account.Value))
            .Select(k => new PeerVm(k.Account.Value, string.IsNullOrWhiteSpace(k.Name) ? k.Account.Value[..8] : k.Name, false, k.LastSeen, false, _node.GetLink(k.Account), Role: RoleOf(k.Account.Value), BannedHere: open.IsBanned(k.Account)));
        // Someone you blocked stays in the list of the rooms you met them in, marked as blocked, so that they can be unblocked.
        var metHere = seenHere.Select(k => k.Account.Value).ToHashSet();
        var shut = _settings.BlockedPeers.Where(b => metHere.Contains(b.Id))
            .Select(b => new PeerVm(b.Id, string.IsNullOrWhiteSpace(b.Name) ? b.Id[..8] : b.Name, false, b.LastSeen, Blocked: true));
        // Someone banned here whom this device never met still needs a line, so that they can be unbanned.
        var listed = online.Concat(offline).Select(p => p.Id).ToHashSet();
        var banned = open.Banned.Where(b => !listed.Contains(b.Account.Value) && !blocked.Contains(b.Account.Value))
            .Select(b => new PeerVm(b.Account.Value, b.Name, false, now, BannedHere: true));
        var people = online.Concat(offline).Concat(banned).Concat(shut)
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
        if (text == _node.AccountId.Value || text == _node.Id.Value) { await Dialogs.MessageAsync(this, "Yaparr", "That's your own ID."); return; }

        await OpenDirectAsync(new PeerId(text));
    }

    private async void DirectChatPerson_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null || PeopleList.SelectedItem is not PeerVm person) return;
        await OpenDirectAsync(new PeerId(person.Id));
    }

    private async Task OpenDirectAsync(PeerId id)
    {
        if (_node == null) return;
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
            var title = chat.DisplayName + " (direct)";
            conv = new ConversationVm(title, isRoom: false, chat.SendAsync, chat.Retry) { PeerId = chat.PeerId.Value };
            chat.DeliveryChanged += s => UI(() => conv.ApplyDelivery(s, chat.GetDeliveryDetails(s.MessageId)));
            // Another of your devices read it: nothing here is new any more.
            chat.ReadChanged += () => UI(() => conv.Unread = 0);
            _directConversations[chat.PeerId] = conv;
        }

        var kept = chat.History;
        chat.MessageReceived += m => UI(() => OnMessage(conv, ToVm(m), announce: !m.IsCatchUp));
        UI(() =>
        {
            foreach (var m in kept) conv.Add(ToVm(m));
            if (chat.Peer != null) Remember(chat.Peer);
            _conversations.Add(conv);
            RefreshPeers();
            if (_selected == null) ConversationList.SelectedItem = conv;
            tcs.SetResult();
        });
        return tcs.Task;
    }

    // A message from one of your devices can arrive before this device knows that the device is yours; it moves to your side once it does.
    private void ClaimOwnMessages()
    {
        if (_node == null) return;
        foreach (var conv in _conversations)
            foreach (var vm in conv.AllMessages)
                if (!vm.IsMine && vm.Source is { } m && _node.IsMyDevice(m.From.Id))
                    vm.ClaimAsMine(_node.Id != m.From.Id ? "from your other device " + m.From.Id.Short : "");
    }

    private MessageVm ToVm(ChatMessage m)
    {
        Remember(m.From);
        bool mine = _node != null && _node.IsMyDevice(m.From.Id);
        // A message you sent from another of your devices says which one.
        var parts = m.Parts;
        var vm = new MessageVm(m.Timestamp, m.From.DisplayName, parts.Body, m.IsCatchUp, isMine: mine, id: m.Id, attachment: m.Attachment)
        {
            Source = m, Key = m.Key, ReplyKey = parts.ReplyTo, Mentions = parts.Mentions,
            MentionsMe = _node != null && NamesMe(m),
            Channel = m.Channel, DeviceNote = mine && _node!.Id != m.From.Id ? "from your other device " + m.From.Id.Short : "",
        };
        if (m.Attachment != null) TrackAttachment(vm);
        return vm;
    }

    // Named by name, or by @everyone from someone who may call everyone.
    private bool NamesMe(ChatMessage m) =>
        _node != null && _roomConversations.Keys.FirstOrDefault(r => r.Name == m.Room) is { } room
            ? room.Names(m, _node.AccountId)
            : _node != null && m.Parts.Mentions.Any(x => x.Account == _node.AccountId);

    private readonly Dictionary<string, List<MessageVm>> _attachmentVms = new();

    private void TrackAttachment(MessageVm vm)
    {
        var hash = vm.Attachment!.Hash;
        if (!_attachmentVms.TryGetValue(hash, out var list)) _attachmentVms[hash] = list = new List<MessageVm>();
        list.Add(vm);
        _ = LoadAttachmentAsync(vm, force: false);
    }

    private async Task LoadAttachmentAsync(MessageVm vm, bool force)
    {
        if (_node == null || vm.Source == null || vm.Attachment is not { } a) return;
        if (!_node.HasAttachment(a.Hash))
        {
            var state = await _node.FetchAttachmentAsync(vm.Source, force);
            if (state != AttachmentState.Ready) { UI(() => vm.Apply(new AttachmentUpdate(a.Hash, state, 0, a.Size))); return; }
        }
        await ShowAttachmentAsync(vm, a.Hash);
    }

    private async Task ShowAttachmentAsync(MessageVm vm, string hash)
    {
        var node = _node;
        var bytes = await Task.Run(() => node?.ReadAttachment(hash));
        if (bytes != null) UI(() => vm.SetImage(bytes));
    }

    private void OnAttachmentChanged(AttachmentUpdate update)
    {
        if (!_attachmentVms.TryGetValue(update.Hash, out var list)) return;
        foreach (var vm in list.ToList())
        {
            if (update.State == AttachmentState.Ready) _ = ShowAttachmentAsync(vm, update.Hash);
            else vm.Apply(update);
        }
    }

    private void Download_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MessageVm vm }) return;
        vm.Apply(new AttachmentUpdate(vm.Attachment!.Hash, AttachmentState.Fetching, 0, vm.Attachment.Size));
        _ = LoadAttachmentAsync(vm, force: true);
    }

    private async void Attach_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Send a picture or GIF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Pictures") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp" } } },
        });
        if (files.Count > 0) await SendFileAsync(files[0]);
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, int limit)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        {
            if (ms.Length + n > limit) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private async Task SendPictureAsync(byte[] original, string fileName)
    {
        var conv = _selected;
        if (conv == null || _node == null) return;
        try
        {
            var prepared = await Task.Run(() => ImageTools.Prepare(original, fileName));
            var attachment = _node.PrepareAttachment(prepared.Bytes, prepared.FileName, prepared.Thumbnail);
            var caption = InputBox.Text?.Trim() ?? "";
            var reply = _replyTo;
            var id = await conv.Send(attachment.ToText(caption, reply?.Key));
            InputBox.Text = "";
            ClearReply();
            var vm = new MessageVm(DateTimeOffset.Now, _node.DisplayName, caption, isMine: true, id: id, attachment: attachment)
            {
                Channel = conv.IsRoom ? conv.CurrentChannel : null,
                Key = ChatMessage.KeyOf(_node.Id, id), ReplyKey = reply?.Key,
            };
            vm.SetImage(prepared.Bytes);
            conv.Add(vm);
            ScrollToEnd();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // A picture that could not be sent is worth a message that cannot be missed, not just a line in the status bar.
            StatusText.Text = ex.Message;
            await Dialogs.MessageAsync(this, "Yaparr", ex.Message);
        }
    }

    // Messages that arrive while catching up on history are shown but never announced.
    private void OnMessage(ConversationVm conv, MessageVm message, bool announce)
    {
        conv.Add(message);
        // A message in another channel of the open room waits like one in another room.
        bool viewing = conv == _selected && (message.Channel == null || message.Channel == conv.CurrentChannel);
        if (viewing)
        {
            ScrollToEnd();
            MarkRead(conv);
            if (message.Channel is { } shownChannel && RoomFor(conv) is { } shownRoom) MarkChannelRead(shownRoom, shownChannel, message.Time.ToUnixTimeMilliseconds());
        }
        else if (!message.IsMine) conv.NoteUnread(message.Channel, message.Highlight);
        // What you wrote on another device needs no announcing.
        if (announce && !message.IsMine) NotifyMessage(conv, message);
    }

    // A direct conversation you are looking at is read on all of your devices.
    private void MarkRead(ConversationVm conv)
    {
        if (_node != null && conv.IsDirect && conv.PeerId is { } peer)
            _node.GetDirectChat(new PeerId(peer)).MarkRead();
    }

    private void ScrollToEnd()
    {
        if (_selected is { Messages.Count: > 0 } conv)
            Dispatcher.UIThread.Post(() => MessageList.ScrollIntoView(conv.Messages[^1]), DispatcherPriority.Background);
    }

    private async void Send_Click(object? sender, RoutedEventArgs e) => await SendAsync();

    private static readonly string[] PictureExtensions = { ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".webp", ".bmp" };

    private static bool IsPictureName(string? name) =>
        name != null && PictureExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    private async Task SendFileAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            var bytes = await ReadLimitedAsync(stream, 40 * 1024 * 1024);
            if (bytes == null) { StatusText.Text = "That file is too big to send as a picture."; return; }
            await SendPictureAsync(bytes, file.Name);
        }
        catch (IOException ex) { StatusText.Text = ex.Message; }
        catch (UnauthorizedAccessException ex) { StatusText.Text = ex.Message; }
    }

    private bool _pasting;

    // Ctrl+V sends a picture when that is what was copied (a picture file, a screenshot, a GIF, an image from a web page) and pastes text
    // otherwise. Some programs put a link or a caption next to the picture; that still counts as a picture, but a copied cell or a
    // paragraph of text is text.
    private async Task PasteAsync(bool inMessageBox)
    {
        if (_pasting || Clipboard is not { } clipboard || _selected == null) return;
        _pasting = true;
        try
        {
            var text = await clipboard.TryGetTextAsync();
            bool plainText = !string.IsNullOrWhiteSpace(text) && !LooksLikeLink(text);
            if (!plainText && await PastePictureAsync(clipboard)) return;
            if (inMessageBox && !string.IsNullOrEmpty(text)) InputBox.Paste();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Most often another program is holding the clipboard for a moment.
            StatusText.Text = "Couldn't paste: " + ex.Message;
            if (inMessageBox) { try { InputBox.Paste(); } catch (Exception) { } }
        }
        finally { _pasting = false; }
    }

    private static bool LooksLikeLink(string? text)
    {
        var line = text?.Trim();
        if (string.IsNullOrEmpty(line) || line.Contains('\n') || line.Contains('\t') || line.Length > 2000) return false;
        return line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || IsPictureName(line);
    }

    // True if there was a picture to send, or a reason to tell the user why not.
    private async Task<bool> PastePictureAsync(IClipboard clipboard)
    {
        var files = (await clipboard.TryGetFilesAsync())?.OfType<IStorageFile>().ToList();
        if (files is { Count: > 0 })
        {
            if (files.FirstOrDefault(f => IsPictureName(f.Name)) is { } file) { await SendFileAsync(file); return true; }
            await Dialogs.MessageAsync(this, "Yaparr", "Only pictures can be sent in a message: PNG, JPEG, GIF, WebP or BMP.");
            return true;
        }

        using var data = await clipboard.TryGetDataAsync();
        if (data != null)
        {
            // Some programs put the GIF itself on the clipboard next to a still picture of it.
            if (await ReadRawAsync(data, "GIF", "image/gif") is { } gif) { await SendPictureAsync(gif, "pasted.gif"); return true; }

            // A browser's Copy image of a GIF leaves only the first frame as a bitmap, plus the page's HTML, which names the GIF.
            if (await ReadRawAsync(data, "HTML Format", "text/html") is { } html
                && ClipboardPictures.FirstImageSource(System.Text.Encoding.UTF8.GetString(html)) is { } src)
            {
                if (ClipboardPictures.IsGifDataUrl(src) && ClipboardPictures.FromDataUrl(src, Attachment.MaxBytes) is { } inline)
                {
                    await SendPictureAsync(inline, "pasted.gif");
                    return true;
                }
                if (ClipboardPictures.IsGifAddress(src, out var address))
                {
                    StatusText.Text = "Getting the GIF from " + address.Host + "…";
                    var fetched = await ClipboardPictures.DownloadAsync(address, Attachment.MaxBytes, _cts.Token);
                    if (fetched != null) { StatusText.Text = ""; await SendPictureAsync(fetched, Path.GetFileName(address.AbsolutePath) is { Length: > 0 } name ? name : "pasted.gif"); return true; }
                    StatusText.Text = "Couldn't get the GIF from " + address.Host + ", so the still picture is sent instead.";
                }
            }
        }

        if (await clipboard.TryGetBitmapAsync() is { } bitmap)
        {
            using (bitmap)
            {
                using var ms = new MemoryStream();
                bitmap.Save(ms, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                await SendPictureAsync(ms.ToArray(), "pasted.png");
            }
            return true;
        }
        return false;
    }

    private static async Task<byte[]?> ReadRawAsync(Avalonia.Input.IAsyncDataTransfer data, params string[] formats)
    {
        foreach (var item in data.Items)
            foreach (var format in formats)
            {
                object? raw;
                try { raw = await item.TryGetRawAsync(Avalonia.Input.DataFormat.CreateBytesPlatformFormat(format)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
                switch (raw)
                {
                    case byte[] bytes: return bytes;
                    case ReadOnlyMemory<byte> memory: return memory.ToArray();
                    case Stream stream: await using (stream) return await ReadLimitedAsync(stream, 40 * 1024 * 1024);
                }
            }
        return null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _selected != null && e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().Any(f => IsPictureName(f.Name)) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().FirstOrDefault(f => IsPictureName(f.Name)) is { } file)
            await SendFileAsync(file);
    }

    // Ctrl+V (Command+V on a Mac, Shift+Insert) pastes a picture from anywhere in the window except another text box.
    private void OnPasteKey(object? sender, KeyEventArgs e)
    {
        bool paste = (e.Key == Key.V && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta) || (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift);
        if (!paste || !InputBox.IsEnabled) return;
        if (FocusManager?.GetFocusedElement() is TextBox focused && focused != InputBox) return;
        // Handled here, since the text box would otherwise paste its own copy of the text next to the picture.
        bool inMessageBox = FocusManager?.GetFocusedElement() == InputBox;
        e.Handled = true;
        _ = PasteAsync(inMessageBox);
    }

    private async void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (HandleComposerKey(e)) { e.Handled = true; return; }
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; await SendAsync(); }
    }

    private async Task SendAsync()
    {
        var conv = _selected;
        var text = InputBox.Text?.Trim() ?? "";
        if (conv == null || text.Length == 0 || _node == null) return;

        try
        {
            var reply = _replyTo;
            var mentions = conv.IsRoom ? ActiveMentions(text) : new List<Mention>();
            var id = await conv.Send(MessageEnvelope.Wrap(text, reply?.Key, mentions));
            InputBox.Text = "";
            ClearReply();
            _chosenMentions.Clear();
            conv.Add(new MessageVm(DateTimeOffset.Now, _node.DisplayName, text, isMine: true, id: id)
            {
                Channel = conv.IsRoom ? conv.CurrentChannel : null,
                Key = ChatMessage.KeyOf(_node.Id, id), ReplyKey = reply?.Key, Mentions = mentions,
            });
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
        ResetComposerExtras();
        UpdateInlineScreen();
        bool has = _selected != null;
        InputBox.IsEnabled = SendButton.IsEnabled = AttachButton.IsEnabled = has;
        MessageList.ItemsSource = _selected?.Messages;
        HeaderText.Text = _selected?.Name ?? "Welcome to Yaparr";
        HeaderSubtitle.Text = _selected?.Subtitle ?? "Serverless chat between friends";
        EmptyPanel.IsVisible = !has;
        DetailsPanel.DataContext = _selected;
        VoicePanel.DataContext = _selected;
        VoiceBar.DataContext = _selected;
        RoomToggle.IsEnabled = has;
        if (!has) RoomToggle.IsChecked = false;
        RefreshMembers();
        RefreshPeers();
        UpdateHeaderSubtitle();
        if (_selected != null)
        {
            if (!_restoringRooms)
            {
                _selected.Opened();
                MarkShownRead(_selected);
            }
            UpdateComposer();
            MarkRead(_selected);
            ScrollToEnd();
            InputBox.Focus();
        }
    }

    private async void CopyId_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        StatusText.Text = await Dialogs.CopyAsync(this, _node.AccountId.Value)
            ? "Your peer ID was copied to the clipboard."
            : "Couldn't copy: another program is using the clipboard. Try again.";
    }

    private void RefreshMembers()
    {
        if (_selected is not { IsRoom: true } conv) return;
        var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
        if (room == null) return;
        int others = room.Members.Where(m => _node?.IsMyDevice(m.Id) != true).Select(m => m.Account).Distinct().Count();
        conv.MembersText = (others == 0 ? "Only you so far" : $"You and {others} other{(others == 1 ? "" : "s")} online")
            + (room.MyRole switch { RoomRole.Owner => "  ·  you own this room", RoomRole.Admin => "  ·  you are an admin", _ => "" });
        UpdateHeaderSubtitle();
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
            ? "Invite copied. Anyone who has it can join this room, so share it carefully."
            : "Couldn't copy: another program is using the clipboard. Try again.";
    }
}
