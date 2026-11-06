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

    public MainView()
    {
        InitializeComponent();
        _screens = new Control[] { StartPanel, LinkPanel, ListPanel, ChatPanel };
        _settings = _data.LoadSettings();
        NameBox.Text = _settings.DisplayName ?? "";
        // Linking is for a phone that has no account yet.
        LinkStartButton.IsVisible = !_data.HasIdentity;
        AttachedToVisualTree += (_, _) => OnAttached();
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

    private bool AnyOverlay => AddPanel.IsVisible || PeoplePanel.IsVisible || ConfirmPanel.IsVisible;

    // One step back; false when there is nothing to go back to and the phone may leave the app.
    private bool GoBack()
    {
        if (ConfirmPanel.IsVisible) { ConfirmPanel.IsVisible = false; return true; }
        if (PeoplePanel.IsVisible) { PeoplePanel.IsVisible = false; return true; }
        if (AddPanel.IsVisible) { AddPanel.IsVisible = false; return true; }
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
        ListStatus.Text = "Connecting...";
        try
        {
            await session.StartAsync(name, _linkedEntries);
            ListStatus.Text = "";
            EmptyHint.IsVisible = session.Chats.Count == 0;
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
            LinkStartButton.IsVisible = false;
            // The rooms of the account come from the computer once this phone has connected to it.
            await BeginAsync(linked.DisplayName);
            ListStatus.Text = $"Linked: you are {linked.DisplayName} here too. Your rooms arrive once this phone has connected to your computer.";
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException)
        {
            LinkStatus.Text = ex.Message;
        }
        finally { LinkButton.IsEnabled = ScanButton.IsEnabled = true; }
    }

    // ---- the list ----

    private void ChatSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ChatList.SelectedItem is not Chat chat) return;
        ChatList.SelectedItem = null;
        OpenChat(chat);
    }

    private void AddClicked(object? sender, RoutedEventArgs e)
    {
        AddStatus.Text = "";
        AddPanel.IsVisible = true;
        RoomNameBox.Focus();
    }

    private void AddCloseClicked(object? sender, RoutedEventArgs e) => AddPanel.IsVisible = false;

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
            Add("Copy invite", CopyInviteClicked);
            Add("Leave the room", LeaveClicked);
        }
        else Add("Copy their ID", CopyIdClicked);
        return menu;
    }

    private void ChannelClicked(object? sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not Avalonia.Controls.Button { DataContext: ChannelChip chip }) return;
        _session?.SelectChannel(_current, chip.Id);
        ScrollToEnd();
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
        InputBox.Text = "";
        try { await _session.SendAsync(chat, text); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            chat.Status = "Could not send: " + ex.Message;
            InputBox.Text = text;
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

    private async void PersonSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (PeopleList.SelectedItem is not Person person || _session == null) return;
        PeopleList.SelectedItem = null;
        PeoplePanel.IsVisible = false;
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
    }

    private void ConfirmNoClicked(object? sender, RoutedEventArgs e) => ConfirmPanel.IsVisible = false;
}
