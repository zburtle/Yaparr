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
    private ChatNode? _node;
    private ConversationVm? _selected;

    public MainWindow()
    {
        InitializeComponent();
        ConversationList.ItemsSource = _conversations;
        Loaded += async (_, _) => await StartAsync();
        Closing += (_, _) => Shutdown();
    }

    private async Task StartAsync()
    {
        var args = Environment.GetCommandLineArgs();
        string? Arg(string flag) => Array.IndexOf(args, flag) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        var dataArg = Arg("--data");
        if (dataArg == null) AppData.MigrateLegacyDefault();
        _data = new AppData(dataArg ?? AppData.DefaultDirectory);
        _settings = _data.LoadSettings();

        var name = Arg("--name") ?? _settings.DisplayName
                   ?? PromptDialog.Ask(this, "Choose a display name (others will see it next to your messages):", Environment.UserName);
        if (string.IsNullOrWhiteSpace(name)) { Close(); return; }
        _settings.DisplayName = name;
        _data.SaveSettings(_settings);

        var identity = _data.LoadOrCreateIdentity();
        _node = new ChatNode(new ChatNodeOptions
        {
            DisplayName = name,
            Identity = identity,
            HistoryStore = new FileHistoryStore(_data.HistoryDirectory),
            Log = s => Trace.WriteLine("[yaparr] " + s),
        });
        _node.PeerConnected += _ => UI(UpdateStatus);
        _node.PeerDisconnected += _ => UI(UpdateStatus);
        _node.IncomingDirectChat += chat => _ = AddDirect(chat);

        Title = $"Yaparr — {name}";
        NameText.Text = name;
        IdText.Text = _node.Id.Value;
        IdText.ToolTip = _node.Id.Value;
        _node.Start();
        UpdateStatus();

        foreach (var priv in _settings.PrivateRooms.ToList())
        {
            try { await JoinPrivateAsync(priv.Label, RoomKey.Parse(priv.Key), persist: false); }
            catch (FormatException) {  }
        }
    }

    private void Shutdown()
    {
        _cts.Cancel();
        _node?.Dispose();
    }

    private void UI(Action action) => Dispatcher.BeginInvoke(action);

    private void UpdateStatus()
    {
        if (_node == null) return;
        int peers = _node.ConnectedPeers.Count;
        StatusText.Text = $"UDP port {_node.LocalPort}  ·  {peers} peer{(peers == 1 ? "" : "s")} connected" +
                          (peers == 0 ? "  ·  looking for peers (the first search can take a while)…" : "");
    }

    private async void PrivateRoom_Click(object sender, RoutedEventArgs e)
    {
        var label = PromptDialog.Ask(this, "A name for this private room (only you see it):");
        if (string.IsNullOrWhiteSpace(label)) return;
        var invite = PromptDialog.Ask(this, "Paste an invite to join an existing private room, or leave blank to create a new one:");
        if (invite == null) return;

        RoomKey key;
        try { key = invite.Length == 0 ? RoomKey.Generate() : RoomKey.Parse(invite); }
        catch (FormatException) { MessageBox.Show(this, "That doesn't look like a valid invite.", "Yaparr"); return; }
        await JoinPrivateAsync(label, key, persist: true);
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
        room.MemberJoined += p => UI(() => { RefreshMembers(conv, room); conv.Add(MessageVm.System($"{p.DisplayName} joined")); });
        room.MemberLeft += p => UI(() => { RefreshMembers(conv, room); conv.Add(MessageVm.System($"{p.DisplayName} left")); });

        foreach (var m in room.GetHistory()) conv.Add(ToVm(m));
        RefreshMembers(conv, room);
        _conversations.Add(conv);
        ConversationList.SelectedItem = conv;
        return conv;
    }

    private static void RefreshMembers(ConversationVm conv, ChatRoom room)
    {
        conv.Members.Clear();
        foreach (var m in room.Members.OrderBy(m => m.DisplayName)) conv.Members.Add(m.IsRelayed ? m.DisplayName + " (relayed)" : m.DisplayName);
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
            conv.Members.Add(chat.Peer?.DisplayName ?? chat.PeerId.Short);
            _conversations.Add(conv);
            if (_selected == null) ConversationList.SelectedItem = conv;
            tcs.SetResult();
        });
        return tcs.Task;
    }

    private MessageVm ToVm(ChatMessage m) =>
        new(m.Timestamp, m.From.DisplayName, m.Text, m.IsCatchUp, IsMine: _node != null && m.From.Id == _node.Id, Id: m.Id);

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
        MemberList.ItemsSource = _selected?.Members;
        HeaderText.Text = _selected?.Title ?? "Create or join a private room, or open a direct chat, to begin";
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
