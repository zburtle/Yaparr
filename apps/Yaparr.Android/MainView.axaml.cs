using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.AndroidApp;

/// <summary>
/// The whole app: first start (a name, or linking to a computer), the list of rooms and direct chats, and a chat. It only works
/// while the app is open: there are no notifications and no background service yet.
/// </summary>
public partial class MainView : UserControl
{
    private readonly AppData _data = new(AppData.DefaultDirectory);
    private readonly AppSettings _settings;
    private readonly HashSet<Chat> _watched = new();
    private readonly Control[] _screens;
    private Session? _session;
    private Chat? _current;
    private Func<Task>? _confirmed;
    private IReadOnlyList<AccountEntry> _linkedEntries = Array.Empty<AccountEntry>();
    private bool _linksReady, _handlingLinks;

    public MainView()
    {
        InitializeComponent();
        _screens = new Control[] { StartPanel, LinkPanel, ListPanel, ChatPanel, SettingsPanel };
        _settings = _data.LoadSettings();
        NameBox.Text = _settings.DisplayName ?? "";
        ApplyTheme();
        // Linking is for a phone that has no account yet.
        LinkStartButton.IsVisible = RestoreStartButton.IsVisible = !_data.HasIdentity;
        AttachedToVisualTree += (_, _) => OnAttached();
        LinkInbox.Arrived += () => _ = HandleLinksAsync();
        // Messages are not picked from the list: a press does something (a long press opens the actions), and nothing stays highlighted.
        Messages.SelectionChanged += (_, _) => Messages.SelectedItem = null;
        // A phone that has been used before opens straight onto its rooms.
        if (_data.HasIdentity && !string.IsNullOrWhiteSpace(_settings.DisplayName)) _ = BeginAsync(_settings.DisplayName);
    }

    private void OnAttached()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        // The on-screen keyboard covers the bottom of the app, so the message box is lifted above it.
        if (top.InputPane is { } pane)
            pane.StateChanged += (_, e) => Root.Margin = new Avalonia.Thickness(0, 0, 0, e.NewState == InputPaneState.Open ? e.EndRect.Height : 0);
        // The phone's back button goes one step back inside the app instead of leaving it.
        top.BackRequested += (_, e) => e.Handled = GoBack();
    }

    private void ShowOnly(Control screen)
    {
        foreach (var s in _screens) s.IsVisible = s == screen;
    }

    private bool AnyOverlay => AddPanel.IsVisible || PeoplePanel.IsVisible || ConfirmPanel.IsVisible || RoutePanel.IsVisible || PicturePanel.IsVisible || ActionPanel.IsVisible || BlockedPanel.IsVisible || NetworkPanel.IsVisible;

    // One step back; false when there is nothing to go back to and the phone may leave the app.
    private bool GoBack()
    {
        if (PromptPanel.IsVisible) { PromptPanel.IsVisible = false; return true; }
        if (BlockedPanel.IsVisible) { BlockedPanel.IsVisible = false; return true; }
        if (NetworkPanel.IsVisible) { NetworkPanel.IsVisible = false; return true; }
        if (ActionPanel.IsVisible) { ActionPanel.IsVisible = false; return true; }
        if (PicturePanel.IsVisible) { PicturePanel.IsVisible = false; return true; }
        if (RoutePanel.IsVisible) { RoutePanel.IsVisible = false; return true; }
        if (ConfirmPanel.IsVisible) { ConfirmPanel.IsVisible = false; return true; }
        if (PeoplePanel.IsVisible) { PeoplePanel.IsVisible = false; return true; }
        if (AddPanel.IsVisible) { AddPanel.IsVisible = false; return true; }
        if (SettingsPanel.IsVisible) { SettingsBackClicked(null, new RoutedEventArgs()); return true; }
        if (ChatPanel.IsVisible) { BackToList(); return true; }
        if (LinkPanel.IsVisible) { LinkBackClicked(null, new RoutedEventArgs()); return true; }
        return false;
    }

    // ---- first start ----

    private async void StartClicked(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) { StartStatus.Text = "Type your name first."; return; }
        _settings.DisplayName = name;
        _data.SaveSettings(_settings);
        await BeginAsync(name);
    }

    private async Task BeginAsync(string name)
    {
        ShowOnly(ListPanel);
        var session = _session = new Session(_data, _settings);
        ChatList.ItemsSource = session.Chats;
        session.Chats.CollectionChanged += (_, _) => EmptyHint.IsVisible = session.Started && session.Chats.Count == 0;
        session.Notice += text => ListStatus.Text = text;
        session.ConnectionChanged += text => ListStatus.Text = text;
        session.SyncChanged += (text, progress) =>
        {
            SyncBanner.IsVisible = text != null;
            SyncText.Text = text ?? "";
            SyncBar.IsIndeterminate = progress == null;
            SyncBar.Value = progress ?? 0;
        };
        ListStatus.Text = "Connecting...";
        try
        {
            await session.StartAsync(name, _linkedEntries);
            EmptyHint.IsVisible = session.Chats.Count == 0;
            _linksReady = true;
            await HandleLinksAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ListStatus.Text = "Could not start: " + ex.Message;
        }
    }

    // ---- linking to a computer ----

    private void LinkStartClicked(object? sender, RoutedEventArgs e)
    {
        ShowOnly(LinkPanel);
        LinkStatus.Text = "";
    }

    private void LinkBackClicked(object? sender, RoutedEventArgs e) => ShowOnly(StartPanel);

    private async void ScanClicked(object? sender, RoutedEventArgs e)
    {
        if (App.Scanner == null) { LinkStatus.Text = "Scanning is not available here. Paste the code and passphrase instead."; return; }
        LinkStatus.Text = "";
        string? text;
        try { text = await App.Scanner.ScanAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LinkStatus.Text = "Could not scan: " + ex.Message;
            return;
        }
        if (text == null) return;
        if (!LinkQr.TryParse(text, out var code, out var passphrase))
        {
            LinkStatus.Text = "That is not a Yaparr link code.";
            return;
        }
        CodeBox.Text = code;
        PassphraseBox.Text = passphrase;
        await LinkAsync(code, passphrase);
    }

    private async void LinkClicked(object? sender, RoutedEventArgs e) =>
        await LinkAsync((CodeBox.Text ?? "").Trim(), (PassphraseBox.Text ?? "").Trim());

    private async Task LinkAsync(string code, string passphrase)
    {
        if (code.Length == 0 || passphrase.Length == 0) { LinkStatus.Text = "Enter the code and the passphrase."; return; }
        LinkButton.IsEnabled = ScanButton.IsEnabled = false;
        LinkStatus.Text = "Linking...";
        try
        {
            var linked = await Task.Run(() => DeviceLink.Open(code, passphrase));
            _data.SaveIdentity(linked.Identity);
            linked.Identity.Dispose();
            _settings.DisplayName = linked.DisplayName;
            _data.SaveSettings(_settings);
            _linkedEntries = linked.Entries;
            NameBox.Text = linked.DisplayName;
            CodeBox.Text = PassphraseBox.Text = "";
            LinkStartButton.IsVisible = RestoreStartButton.IsVisible = false;
            // The rooms of the account come from the computer once this phone has connected to it.
            await BeginAsync(linked.DisplayName);
            _session?.BeginDeviceSearch();
            ListStatus.Text = $"Linked: you are {linked.DisplayName} here too.";
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException)
        {
            LinkStatus.Text = ex.Message;
        }
        finally { LinkButton.IsEnabled = ScanButton.IsEnabled = true; }
    }

    // ---- links ----

    // The links that were opened in the app, one at a time: the next waits until the question the last one asked is answered. A link
    // only asks, and the person decides.
    private async Task HandleLinksAsync()
    {
        if (!_linksReady || _handlingLinks || _session is not { Started: true } session) return;
        _handlingLinks = true;
        try
        {
            while (!AnyOverlay && LinkInbox.Take() is { } link)
            {
                if (link.Kind == DeepLinkKind.Join) HandleJoinLink(session, link.Value);
                else HandleChatLink(session, link.Value);
            }
        }
        finally { _handlingLinks = false; }
        await Task.CompletedTask;
    }

    private void HandleJoinLink(Session session, string invite)
    {
        RoomKey key;
        try { key = RoomKey.Parse(invite); }
        catch (FormatException) { return; }
        // Already in the room: just show it.
        if (session.FindRoom(key) is { } have) { OpenChat(have); return; }
        // Not yet: the room is named and confirmed on the sheet that joins rooms.
        if (_current != null) BackToList();
        RoomNameBox.Text = "";
        RoomInviteBox.Text = invite;
        AddStatus.Text = "A link is asking you to join a room. Once you join, the other members can see your IP address, and anyone with the link can join too. " +
                         "Only join if you trust whoever sent it. Name the room (only you see the name), then tap Make or join the room.";
        AddPanel.IsVisible = true;
        RoomNameBox.Focus();
    }

    private void HandleChatLink(Session session, string id)
    {
        if (id == session.AccountId) { ListStatus.Text = "That link is for your own ID."; return; }
        var person = new PeerId(id);
        if (session.FindDirect(person) is { } existing) { OpenChat(existing); return; }
        if (_current != null) BackToList();
        Ask($"A link is asking you to start a direct chat with {person.Short}... They will be able to see your IP address. Only continue if you trust whoever sent it.", "Start the chat", async () =>
        {
            try
            {
                ListStatus.Text = "Looking for them. This can take a minute if they just came online...";
                OpenChat(await session.OpenDirectAsync(person));
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or TimeoutException)
            {
                ListStatus.Text = "Could not reach them: " + ex.Message;
            }
        });
    }

    // ---- the list ----

    private void ChatSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ChatList.SelectedItem is not Chat chat) return;
        ChatList.SelectedItem = null;
        OpenChat(chat);
    }

    // ---- settings ----

    private bool _loadingSettings;

    private void ApplyTheme()
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = _settings.Theme switch { "Light" => Avalonia.Styling.ThemeVariant.Light, "Dark" => Avalonia.Styling.ThemeVariant.Dark, _ => Avalonia.Styling.ThemeVariant.Default };
    }

    private void ShowTheme()
    {
        foreach (var button in new[] { ThemeSystem, ThemeLight, ThemeDark })
        {
            bool on = (string?)button.Tag == _settings.Theme;
            button.Background = on ? Yaparr.AndroidApp.Theme.Brush("Accent") : Avalonia.Media.Brushes.Transparent;
            button.Foreground = on ? Avalonia.Media.Brushes.White : Yaparr.AndroidApp.Theme.Brush("Ink");
        }
    }

    private void SettingsClicked(object? sender, RoutedEventArgs e)
    {
        _loadingSettings = true;
        SettingsName.Text = _settings.DisplayName ?? "";
        SettingsNote.Text = "";
        AlwaysOnCheck.IsChecked = _settings.VoiceAlwaysOn;
        SensitivitySlider.Value = _settings.VoiceSensitivity;
        VolumeSlider.Value = _settings.VoiceOutputVolume;
        AutoVolumeCheck.IsChecked = _settings.VoiceAutomaticVolume;
        VersionText.Text = "Yaparr " + AppVersion();
        _loadingSettings = false;
        ShowTheme();
        ShowOnly(SettingsPanel);
    }

    private static string AppVersion()
    {
        try
        {
            var context = Android.App.Application.Context;
            return context.PackageManager?.GetPackageInfo(context.PackageName ?? "", 0)?.VersionName ?? "";
        }
        catch (Exception ex) when (ex is Android.Content.PM.PackageManager.NameNotFoundException or Java.Lang.RuntimeException) { return ""; }
    }

    private void SettingsBackClicked(object? sender, RoutedEventArgs e)
    {
        _data.SaveSettings(_settings);
        ShowOnly(ListPanel);
    }

    private void SaveNameClicked(object? sender, RoutedEventArgs e)
    {
        var name = (SettingsName.Text ?? "").Trim();
        if (name.Length == 0) { SettingsNote.Text = "Enter a name."; return; }
        try
        {
            if (_session != null) _session.Rename(name);
            else { _settings.DisplayName = name; _data.SaveSettings(_settings); }
            SettingsNote.Text = "Saved.";
        }
        catch (ArgumentException ex) { SettingsNote.Text = ex.Message; }
    }

    private void ThemeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Button { Tag: string theme }) return;
        _settings.Theme = theme;
        _data.SaveSettings(_settings);
        ApplyTheme();
        ShowTheme();
    }

    private void VoiceSettingChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.VoiceAlwaysOn = AlwaysOnCheck.IsChecked == true;
        _settings.VoiceAutomaticVolume = AutoVolumeCheck.IsChecked == true;
        _data.SaveSettings(_settings);
        _session?.ApplyVoiceSettings();
    }

    // A slider makes many changes a second: they are kept in memory and a call hears them at once; the file is written when the finger lifts.
    private void VoiceSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.VoiceSensitivity = (int)Math.Round(SensitivitySlider.Value);
        _settings.VoiceOutputVolume = (int)Math.Round(VolumeSlider.Value);
        _session?.ApplyVoiceSettings();
    }

    private void VoiceSliderReleased(object? sender, PointerCaptureLostEventArgs e) => _data.SaveSettings(_settings);

    // ---- backup and restore ----

    // A backup is a copy of everything Yaparr keeps for you; Android's "save as" asks where to put it.
    private void BackupClicked(object? sender, RoutedEventArgs e)
    {
        if (MainActivity.Instance is not { } activity) return;
        Ask("A backup holds your private identity key, your room keys and your message history. Anyone with the file can pretend to be you and read your rooms, so keep it somewhere safe.\n\n" +
            "To bring it back, choose Restore a backup on the first screen of a phone with no account. To use your account on a second device at the same time, link it instead: two devices running the same backup would clash.",
            "Back up", async () =>
            {
                string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yaparr-backup-" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    _data.SaveSettings(_settings);
                    await Task.Run(() => _data.ExportProfile(temp));
                    var bytes = await Task.Run(() => File.ReadAllBytes(temp));
                    var name = $"Yaparr-backup-{_settings.DisplayName}-{DateTime.Now:yyyyMMdd}.zip";
                    SettingsNote.Text = await activity.SaveFileAsync(bytes, name, "application/zip") ? "Backup saved." : "Backup not saved.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
                {
                    SettingsNote.Text = "Backup failed: " + ex.Message;
                }
                finally { try { File.Delete(temp); } catch (IOException) { } }
            });
    }

    // On the first screen only, where there is no account yet, so nothing is replaced.
    private async void RestoreClicked(object? sender, RoutedEventArgs e)
    {
        if (MainActivity.Instance is not { } activity || _data.HasIdentity) return;
        var path = await activity.PickFileAsync("application/zip");
        if (path == null) { StartStatus.Text = "No backup chosen."; return; }
        try
        {
            string staging;
            try { staging = await Task.Run(() => _data.StageProfile(path)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                StartStatus.Text = ex.Message;
                return;
            }
            try { await Task.Run(() => _data.ApplyStagedProfile(staging)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StartStatus.Text = "Restoring failed: " + ex.Message;
                return;
            }
            StartStatus.Text = "Your account is back. Yaparr is starting again.";
            await Task.Delay(1200);
            activity.RestartApp();
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    // ---- the connection check ----

    private NetworkCheckReport? _networkReport;
    private bool _checkingNetwork;

    private async void NetworkClicked(object? sender, RoutedEventArgs e)
    {
        NetworkPanel.IsVisible = true;
        await RunNetworkCheckAsync();
    }

    private async void NetworkAgainClicked(object? sender, RoutedEventArgs e) => await RunNetworkCheckAsync();

    private async Task RunNetworkCheckAsync()
    {
        if (_checkingNetwork) return;
        _checkingNetwork = true;
        NetworkAgain.IsEnabled = NetworkCopy.IsEnabled = false;
        NetworkSummary.Text = "Checking\u2026";
        NetworkList.Children.Clear();
        try
        {
            if (_session == null) throw new InvalidOperationException("Yaparr has not started yet.");
            _networkReport = await _session.CheckNetworkAsync();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException)
        {
            _networkReport = null;
            NetworkSummary.Text = "The check could not finish: " + ex.Message;
            NetworkAgain.IsEnabled = true;
            _checkingNetwork = false;
            return;
        }
        NetworkSummary.Text = _networkReport.Summary;
        foreach (var item in _networkReport.Items)
        {
            var color = item.Level switch
            {
                CheckLevel.Ok => Avalonia.Media.Brushes.SeaGreen,
                CheckLevel.Info => Avalonia.Media.Brushes.Gray,
                CheckLevel.Warning => Avalonia.Media.Brushes.DarkOrange,
                _ => Avalonia.Media.Brushes.Firebrick,
            };
            var glyph = item.Level switch { CheckLevel.Ok => "\u2713", CheckLevel.Info => "\u2022", CheckLevel.Warning => "!", _ => "\u2715" };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
            row.Children.Add(new TextBlock { Text = glyph, Foreground = color, FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 16, Width = 18, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = item.Title, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Yaparr.AndroidApp.Theme.Brush("Ink") });
            text.Children.Add(new TextBlock { Text = item.Detail, FontSize = 13, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Yaparr.AndroidApp.Theme.Brush("Muted") });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            NetworkList.Children.Add(row);
        }
        NetworkAgain.IsEnabled = NetworkCopy.IsEnabled = true;
        _checkingNetwork = false;
    }

    private async void NetworkCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (_networkReport == null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var lines = _networkReport.Items.Select(i => $"[{i.Level}] {i.Title}: {i.Detail}");
        await clipboard.SetTextAsync(_networkReport.Summary + Environment.NewLine + string.Join(Environment.NewLine, lines));
        NetworkSummary.Text = _networkReport.Summary + "  (copied)";
    }

    private void NetworkCloseClicked(object? sender, RoutedEventArgs e) => NetworkPanel.IsVisible = false;

    private async void DiagnosticsClicked(object? sender, RoutedEventArgs e)
    {
        if (_session == null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(_session.Diagnostics());
        var note = "Copied who this phone is connected to and what it has been doing. Paste it where you are reporting the problem.";
        ListStatus.Text = note;
        SettingsNote.Text = note;
    }

    private void AddClicked(object? sender, RoutedEventArgs e)
    {
        AddStatus.Text = "";
        AddPanel.IsVisible = true;
        RoomNameBox.Focus();
    }

    private void AddCloseClicked(object? sender, RoutedEventArgs e)
    {
        AddPanel.IsVisible = false;
        _ = HandleLinksAsync();
    }

    private void OverlayBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only a press on the dimmed background closes it, not one on the card.
        if (e.Source == sender && sender is Control c) c.IsVisible = false;
    }

    private async void RoomGoClicked(object? sender, RoutedEventArgs e)
    {
        if (_session is not { Started: true } session) { AddStatus.Text = "Still connecting. Try again in a moment."; return; }
        var label = (RoomNameBox.Text ?? "").Trim();
        var invite = (RoomInviteBox.Text ?? "").Trim();
        // A link that carries the invite works as well as the bare key.
        if (DeepLink.Parse(invite) is { Kind: DeepLinkKind.Join } pasted) invite = pasted.Value;
        if (label.Length == 0) { AddStatus.Text = "Give the room a name."; return; }
        AddStatus.Text = invite.Length > 0 ? "Joining..." : "Making the room...";
        try
        {
            var chat = invite.Length > 0 ? await session.JoinWithInviteAsync(label, invite) : await session.NewRoomAsync(label);
            RoomNameBox.Text = RoomInviteBox.Text = "";
            AddPanel.IsVisible = false;
            OpenChat(chat);
        }
        catch (FormatException) { AddStatus.Text = "That invite does not look right."; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { AddStatus.Text = "Could not join: " + ex.Message; }
    }

    private async void PersonGoClicked(object? sender, RoutedEventArgs e)
    {
        if (_session is not { Started: true } session) { AddStatus.Text = "Still connecting. Try again in a moment."; return; }
        var text = (PersonBox.Text ?? "").Trim().ToLowerInvariant();
        if (text.Length != 64 || !text.All(Uri.IsHexDigit)) { AddStatus.Text = "An ID is 64 letters and digits (0-9, a-f)."; return; }
        if (text == session.AccountId) { AddStatus.Text = "That is your own ID."; return; }
        AddStatus.Text = "Looking for them. This can take a minute if they just came online...";
        try
        {
            var chat = await session.OpenDirectAsync(new PeerId(text));
            PersonBox.Text = "";
            AddPanel.IsVisible = false;
            OpenChat(chat);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or TimeoutException)
        {
            AddStatus.Text = "Could not reach them: " + ex.Message;
        }
    }

    // ---- a chat ----

    private void OpenChat(Chat chat)
    {
        if (_session == null) return;
        _current = chat;
        _session.Open(chat);
        ChatPanel.DataContext = chat;
        MenuButton.Flyout = BuildMenu(chat);
        // New messages scroll the list down, once for each chat.
        if (_watched.Add(chat)) chat.Shown.CollectionChanged += (_, _) => { if (_current == chat) ScrollToEnd(); };
        ShowOnly(ChatPanel);
        ScrollToEnd();
    }

    private void BackToList()
    {
        _session?.Close();
        _current = null;
        ClearReply();
        _named.Clear();
        MentionBar.IsVisible = false;
        ChatPanel.DataContext = null;
        ShowOnly(ListPanel);
    }

    private void BackClicked(object? sender, RoutedEventArgs e) => BackToList();

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        if (_current is { Shown.Count: > 0 } chat) Messages.ScrollIntoView(chat.Shown.Count - 1);
    });

    private MenuFlyout BuildMenu(Chat chat)
    {
        var menu = new MenuFlyout();
        void Add(string header, EventHandler<RoutedEventArgs> click)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            menu.Items.Add(item);
        }
        if (chat.IsRoom)
        {
            Add("People", PeopleClicked);
            Add("Rename the room", RenameClicked);
            Add("Blocked and banned", BlockedClicked);
            Add("Copy invite link", CopyInviteLinkClicked);
            Add("Copy invite", CopyInviteClicked);
            Add("Leave the room", LeaveClicked);
        }
        else
        {
            Add("Copy chat link", CopyChatLinkClicked);
            Add("Copy their ID", CopyIdClicked);
            Add("Block this person", BlockDirectClicked);
            Add("Blocked and banned", BlockedClicked);
        }
        return menu;
    }

    private void ChannelClicked(object? sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not Avalonia.Controls.Button { DataContext: ChannelChip chip }) return;
        _session?.SelectChannel(_current, chip.Id);
        ScrollToEnd();
    }

    // ---- voice ----

    private async void JoinVoiceClicked(object? sender, RoutedEventArgs e)
    {
        string channel = (sender as Control)?.DataContext is VoiceChannelVm vm ? vm.Id : RoomChannels.DefaultVoice;
        if (_current == null || _session == null || MainActivity.Instance is not { } activity) return;
        var chat = _current;
        // The microphone is asked for the first time someone joins; without it there is no call.
        if (!await activity.RequestMicrophoneAsync())
        {
            ListStatus.Text = "Voice needs the microphone. Allow it for Yaparr in the phone's settings to join a voice channel.";
            chat.Status = "Microphone not allowed";
            return;
        }
        try { _session.JoinVoice(chat, activity, channel); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            chat.Status = "Couldn't open the microphone or speaker";
            ListStatus.Text = $"Couldn't join voice: {ex.Message}";
        }
    }

    private void MuteClicked(object? sender, RoutedEventArgs e) => _session?.ToggleMute();

    private void LeaveVoiceClicked(object? sender, RoutedEventArgs e) => _session?.LeaveVoice();

    private void DeafenClicked(object? sender, RoutedEventArgs e) => _session?.ToggleDeafen();

    // Where the sound goes: a small window in the middle, as in Android's own call screen, with the one in use marked.
    private void RouteClicked(object? sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        var routes = _session.VoiceRoutes();
        if (routes.Count == 0) return;
        RouteList.Children.Clear();
        foreach (var (route, label, selected) in routes)
        {
            var item = new Avalonia.Controls.Button
            {
                Content = (selected ? "\u25CF   " : "\u25CB   ") + label,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Avalonia.Thickness(14, 14),
                Background = Avalonia.Media.Brushes.Transparent,
                FontWeight = selected ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal,
                Foreground = Yaparr.AndroidApp.Theme.Brush(selected ? "Accent" : "Ink"),
            };
            item.Click += (_, _) => { _session?.SelectRoute(route); RoutePanel.IsVisible = false; };
            RouteList.Children.Add(item);
        }
        RoutePanel.IsVisible = true;
    }

    // ---- pictures ----

    private Line? _bigLine;

    // ---- replies, mentions and the actions of a message ----

    private Line? _replyTo;
    private readonly Dictionary<string, string> _named = new();   // account -> the name as it was put in the text

    private void StartReply(Line line)
    {
        _replyTo = line;
        ReplyBarTitle.Text = line.Mine ? "Replying to yourself" : "Replying to " + line.Who;
        ReplyBarText.Text = line.Preview;
        ReplyBar.IsVisible = true;
        InputBox.Focus();
    }

    private void ClearReply()
    {
        _replyTo = null;
        ReplyBar.IsVisible = false;
    }

    private void CancelReplyClicked(object? sender, RoutedEventArgs e) => ClearReply();

    // A long press on a message: reply, copy, and for an admin delete.
    private void MessageHeld(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started || sender is not Control { DataContext: Line line } || _session == null || _current is not { } chat) return;
        e.Handled = true;
        ActionTitle.Text = line.Mine ? "Your message" : line.Who;
        ActionList.Children.Clear();
        AddAction("Reply", () => StartReply(line));
        if (line.Text.Length > 0) AddAction("Copy text", async () => await CopyAsync(line.Text, "Copied"));
        if (_session.CanDelete(chat, line))
            AddAction("Delete for everyone", () => Ask("Delete this message for everyone in the room?", "Delete", () =>
            {
                _session?.DeleteMessage(chat, line);
                return Task.CompletedTask;
            }), danger: true);
        if (!line.Mine && line.Source is { } source)
        {
            var who = _session.SenderOf(chat, source);
            if (chat.IsRoom && _session.CanBan(chat, who))
                AddAction("Ban sender", () => Ask($"Ban {who.Name} from this room? Everyone's Yaparr stops showing their messages and voice.", "Ban", () =>
                {
                    try { _session.Ban(chat, who); ShowChatNote(chat, who.Name + " is banned"); }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { ShowChatNote(chat, ex.Message); }
                    return Task.CompletedTask;
                }), danger: true);
            AddAction("Block sender", () => AskBlock(who.Id, who.Name), danger: true);
        }
        ActionPanel.IsVisible = true;
    }

    private void AddAction(string label, Action action, bool danger = false)
    {
        var item = new Avalonia.Controls.Button
        {
            Content = label,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Padding = new Avalonia.Thickness(16, 14),
            Background = Avalonia.Media.Brushes.Transparent,
            Foreground = Yaparr.AndroidApp.Theme.Brush(danger ? "Bad" : "Ink"),
        };
        item.Click += (_, _) => { ActionPanel.IsVisible = false; action(); };
        ActionList.Children.Add(item);
    }

    // A tap on the quote in a reply goes to the message it answers, in its channel.
    private void QuoteTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: Line { ReplyKey: { } key } } || _current is not { } chat || _session == null) return;
        e.Handled = true;
        if (chat.FindByKey(key) is not { } original) { chat.Status = "That message is not on this phone"; return; }
        if (original.Channel is { } channel && channel != chat.CurrentChannel) _session.SelectChannel(chat, channel);
        if (chat.Shown.Contains(original)) Messages.ScrollIntoView(original);
    }

    // While an @ and some letters end the text, the people they could mean are offered.
    private void InputChanged(object? sender, TextChangedEventArgs e)
    {
        if (_current is not { IsRoom: true } chat || _session == null) { MentionBar.IsVisible = false; return; }
        var text = InputBox.Text ?? "";
        int caret = Math.Min(InputBox.CaretIndex, text.Length);
        int start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (start >= caret || text[start] != '@') { MentionBar.IsVisible = false; return; }
        var prefix = text[(start + 1)..caret];
        var people = _session.Mentionable(chat).Where(p => p.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)).Take(12).ToList();
        MentionList.ItemsSource = people;
        MentionBar.IsVisible = people.Count > 0;
    }

    private void MentionChosen(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: Person person }) return;
        var text = InputBox.Text ?? "";
        int caret = Math.Min(InputBox.CaretIndex, text.Length);
        int start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (start >= caret || text[start] != '@') return;
        var tag = "@" + person.Name + " ";
        InputBox.Text = text[..start] + tag + text[caret..];
        InputBox.CaretIndex = start + tag.Length;
        _named[person.Id] = person.Name;
        MentionBar.IsVisible = false;
    }

    // The people whose @name is still in the text, as the library wants them.
    private List<Mention> NamedPeople(string text) =>
        _named.Where(kv => text.Contains("@" + kv.Value, StringComparison.Ordinal))
            .Select(kv => new Mention(new Yaparr.Identity.PeerId(kv.Key), kv.Value)).ToList();

    // The + button: pick a picture with the system's chooser and send it, with what is typed as its caption.
    private async void AttachClicked(object? sender, RoutedEventArgs e)
    {
        if (_current == null || _session == null || MainActivity.Instance is not { } activity) return;
        var chat = _current;
        var picked = await activity.PickPictureAsync();
        if (picked is not { } file) return;
        var caption = (InputBox.Text ?? "").Trim();
        try
        {
            var reply = _replyTo;
            await _session.SendPictureAsync(chat, file.Bytes, file.Name, caption, reply?.Id);
            InputBox.Text = "";
            ClearReply();
            ScrollToEnd();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ShowChatNote(chat, ex.Message);
        }
    }

    // A line under the title for a few seconds, then who is here again.
    private async void ShowChatNote(Chat chat, string text)
    {
        chat.Status = text;
        await Task.Delay(5000);
        if (chat.Status == text) _session?.RefreshStatus(chat);
    }

    // A tap on a picture: big if it is here, otherwise fetch it.
    private void PictureTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: Line line } || _session == null) return;
        if (!line.HasPicture) { _session.DownloadPicture(line); return; }
        var bytes = _session.PictureBytes(line);
        if (bytes == null) return;
        try
        {
            using var ms = new MemoryStream(bytes);
            BigPicture.Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(ms, 1600);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { return; }
        _bigLine = line;
        PicturePanel.IsVisible = true;
    }

    private void PictureCloseClicked(object? sender, RoutedEventArgs e) => PicturePanel.IsVisible = false;

    // Save: the system asks where, so the app needs no permission for the phone's storage.
    private async void PictureSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_bigLine is not { Attachment: { } a } line || _session == null || MainActivity.Instance is not { } activity) return;
        var bytes = _session.PictureBytes(line);
        if (bytes == null) return;
        bool saved = await activity.SaveFileAsync(bytes, a.FileName, a.Mime);
        if (saved && _current != null) ShowChatNote(_current, "Picture saved");
    }

    private async void SendClicked(object? sender, RoutedEventArgs e) => await SendAsync();

    private async void InputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SendAsync();
    }

    private async Task SendAsync()
    {
        var text = (InputBox.Text ?? "").Trim();
        if (text.Length == 0 || _current is not { } chat || _session == null) return;
        var reply = _replyTo;
        var mentions = NamedPeople(text);
        InputBox.Text = "";
        ClearReply();
        try { await _session.SendAsync(chat, text, reply?.Id, mentions); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            chat.Status = "Could not send: " + ex.Message;
            InputBox.Text = text;
            if (reply != null) StartReply(reply);
        }
    }

    private async Task CopyAsync(string text, string done)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || _current == null) return;
        await clipboard.SetTextAsync(text);
        _current.Status = done;
    }

    private async void CopyInviteClicked(object? sender, RoutedEventArgs e)
    {
        if (_current?.Invite is { } invite) await CopyAsync(invite, "Invite copied. Share it only with people you want in the room.");
    }

    private async void CopyInviteLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (_current?.Invite is { } invite) await CopyAsync(DeepLink.RoomLink(invite), "Invite link copied. Anyone who has it can join the room, so share it carefully.");
    }

    private async void CopyChatLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (_current?.Direct is { } direct) await CopyAsync(DeepLink.ChatLink(direct.PeerId.Value), "A link to chat with them was copied.");
    }

    private async void CopyIdClicked(object? sender, RoutedEventArgs e)
    {
        if (_current?.Direct is { } direct) await CopyAsync(direct.PeerId.Value, "Their ID was copied.");
    }

    // ---- the people of a room ----

    private void PeopleClicked(object? sender, RoutedEventArgs e)
    {
        if (_current == null || _session == null) return;
        PeopleList.ItemsSource = _session.PeopleOf(_current);
        PeoplePanel.IsVisible = true;
    }

    private void PeopleCloseClicked(object? sender, RoutedEventArgs e) => PeoplePanel.IsVisible = false;

    // A tap on a person in the list: write to them, and what an owner or admin can do about them.
    private void PersonSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (PeopleList.SelectedItem is not Person person || _session == null || _current is not { } chat) return;
        PeopleList.SelectedItem = null;
        PeoplePanel.IsVisible = false;
        ActionTitle.Text = person.Role.Length > 0 ? person.Name + " (" + person.Role.ToLowerInvariant() + ")" : person.Name;
        ActionList.Children.Clear();
        AddAction("Message", () => _ = OpenPersonAsync(person));
        if (_session.CanSetAdmin(chat, person))
        {
            bool admin = _session.IsAdmin(person);
            AddAction(admin ? "Remove as admin" : "Make admin", () =>
            {
                try { _session.SetAdmin(chat, person, !admin); ShowChatNote(chat, admin ? person.Name + " is no longer an admin" : person.Name + " is now an admin"); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { ShowChatNote(chat, ex.Message); }
            });
        }
        if (_session.CanBan(chat, person))
            AddAction("Ban from this room", () => Ask($"Ban {person.Name} from this room? Everyone's Yaparr stops showing their messages and voice. They still hold the invite, so they can come back under a new identity. You can unban them from Blocked and banned.", "Ban", () =>
            {
                try { _session.Ban(chat, person); ShowChatNote(chat, person.Name + " is banned"); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { ShowChatNote(chat, ex.Message); }
                return Task.CompletedTask;
            }), danger: true);
        AddAction("Block", () => AskBlock(person.Id, person.Name), danger: true);
        ActionPanel.IsVisible = true;
    }

    private async Task OpenPersonAsync(Person person)
    {
        if (_session == null) return;
        var from = _current;
        if (from != null) from.Status = $"Looking for {person.Name}. This can take a minute if they just came online...";
        try
        {
            var chat = await _session.OpenDirectAsync(new PeerId(person.Id));
            OpenChat(chat);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or TimeoutException)
        {
            if (from != null) from.Status = "Could not reach them: " + ex.Message;
        }
        finally { if (from != null && from != _current) _session.RefreshStatus(from); }
    }

    // ---- a line of text to answer, and renaming a room ----

    private Action<string>? _prompted;

    private void AskText(string title, string initial, Action<string> done)
    {
        PromptTitle.Text = title;
        PromptBox.Text = initial;
        _prompted = done;
        PromptPanel.IsVisible = true;
        PromptBox.Focus();
        PromptBox.SelectAll();
    }

    private void PromptOkClicked(object? sender, RoutedEventArgs e)
    {
        PromptPanel.IsVisible = false;
        var done = _prompted;
        _prompted = null;
        var text = (PromptBox.Text ?? "").Trim();
        if (text.Length > 0) done?.Invoke(text);
    }

    private void PromptCancelClicked(object? sender, RoutedEventArgs e)
    {
        PromptPanel.IsVisible = false;
        _prompted = null;
    }

    // A new name for the room, for you only; the others keep the name they gave it.
    private void RenameClicked(object? sender, RoutedEventArgs e)
    {
        if (_current is not { IsRoom: true } chat || _session == null) return;
        AskText("A new name for this room (only you see it):", chat.Title, label => _session?.RenameRoom(chat, label));
    }

    // ---- blocking ----

    private void AskBlock(string accountId, string name) =>
        Ask($"Block {name}? You will no longer see their messages or hear them, they disappear from your lists, and your connection to them is closed. They are not told, and everyone else still sees them. You can unblock them from Blocked and banned.", "Block", () =>
        {
            try
            {
                _session?.Block(accountId, name);
                if (_current != null) ShowChatNote(_current, name + " is blocked");
            }
            catch (ArgumentException ex) { if (_current != null) ShowChatNote(_current, ex.Message); }
            return Task.CompletedTask;
        });

    private void BlockDirectClicked(object? sender, RoutedEventArgs e)
    {
        if (_current?.Direct is { } direct) AskBlock(direct.PeerId.Value, _current.Title);
    }

    // Who is blocked (by you) and who is banned from this room, each with a button to lift it.
    private void BlockedClicked(object? sender, RoutedEventArgs e) => ShowBlocked();

    private void ShowBlocked()
    {
        if (_session == null) return;
        BlockedList.Children.Clear();
        void Header(string text) => BlockedList.Children.Add(new TextBlock { Text = text, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Avalonia.Thickness(4, 10, 4, 4), Foreground = Yaparr.AndroidApp.Theme.Brush("Muted") });
        void Row(Person person, string button, Action lift)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(new TextBlock { Text = person.Name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Avalonia.Thickness(4, 0), Foreground = Yaparr.AndroidApp.Theme.Brush("Ink") });
            var lifter = new Avalonia.Controls.Button { Content = button, Padding = new Avalonia.Thickness(14, 8) };
            Grid.SetColumn(lifter, 1);
            lifter.Click += (_, _) => { lift(); ShowBlocked(); };
            grid.Children.Add(lifter);
            BlockedList.Children.Add(grid);
        }
        Header("Blocked by you");
        var blocked = _session.BlockedPeople();
        if (blocked.Count == 0) BlockedList.Children.Add(new TextBlock { Text = "No one.", Margin = new Avalonia.Thickness(4), Foreground = Yaparr.AndroidApp.Theme.Brush("Muted") });
        foreach (var person in blocked) Row(person, "Unblock", () => _session.Unblock(person.Id));
        if (_current is { IsRoom: true } chat)
        {
            Header("Banned from this room");
            var banned = _session.BannedPeople(chat);
            if (banned.Count == 0) BlockedList.Children.Add(new TextBlock { Text = "No one.", Margin = new Avalonia.Thickness(4), Foreground = Yaparr.AndroidApp.Theme.Brush("Muted") });
            foreach (var person in banned)
            {
                if (chat.Room is { MyRole: >= RoomRole.Admin }) Row(person, "Unban", () => { try { _session.Unban(chat, person.Id); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { ShowChatNote(chat, ex.Message); } });
                else BlockedList.Children.Add(new TextBlock { Text = person.Name, Margin = new Avalonia.Thickness(4, 4), Foreground = Yaparr.AndroidApp.Theme.Brush("Ink") });
            }
        }
        BlockedPanel.IsVisible = true;
    }

    private void BlockedCloseClicked(object? sender, RoutedEventArgs e) => BlockedPanel.IsVisible = false;

    // ---- leaving a room ----

    private void LeaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_current is not { IsRoom: true } chat) return;
        Ask($"Leave \"{chat.Title}\"? You leave it on your other devices too. You can join again with an invite.", "Leave the room", async () =>
        {
            if (_session != null) await _session.LeaveAsync(chat);
            BackToList();
        });
    }

    private void Ask(string text, string yes, Func<Task> action)
    {
        ConfirmText.Text = text;
        ConfirmYes.Content = yes;
        _confirmed = action;
        ConfirmPanel.IsVisible = true;
    }

    private async void ConfirmYesClicked(object? sender, RoutedEventArgs e)
    {
        ConfirmPanel.IsVisible = false;
        var action = _confirmed;
        _confirmed = null;
        if (action != null) await action();
        _ = HandleLinksAsync();
    }

    private void ConfirmNoClicked(object? sender, RoutedEventArgs e)
    {
        ConfirmPanel.IsVisible = false;
        _ = HandleLinksAsync();
    }
}
