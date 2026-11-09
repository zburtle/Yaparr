using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// Yaparr links: yaparr://join/<invite> and yaparr://chat/<peer id>, which the operating system opens in this app, and the
// https://yaparr.com/join#<invite> and /chat#<peer id> forms that are shared. A link never acts on its own: the person is
// asked first, because joining a room or starting a chat lets the other side see their IP address.
public partial class MainWindow
{
    private const int MaxWaitingLinks = 4;
    private readonly Queue<DeepLink> _waitingLinks = new();
    private LinkChannel? _links;
    private bool _linksReady, _handlingLink;

    // Called once the account is known. Links that arrive before the rooms are back wait until they are.
    private void StartLinks()
    {
        if (Program.StartupLink is { } first) _waitingLinks.Enqueue(first);
        _links = LinkChannel.Listen(LinkChannel.NameFor(_data.Directory), line =>
        {
            if (DeepLink.Parse(line) is { } link) UI(() => { QueueLink(link); });
        });
    }

    private void QueueLink(DeepLink link)
    {
        if (_waitingLinks.Count < MaxWaitingLinks) _waitingLinks.Enqueue(link);
        if (_linksReady) _ = HandleWaitingLinksAsync();
    }

    private async Task HandleWaitingLinksAsync()
    {
        if (_handlingLink) return;
        _handlingLink = true;
        try
        {
            while (_waitingLinks.Count > 0 && _node != null)
            {
                var link = _waitingLinks.Dequeue();
                ShowFromTray();
                if (link.Kind == DeepLinkKind.Join) await JoinWithKeyAsync(RoomKey.Parse(link.Value), fromLink: true);
                else await ChatFromLinkAsync(new PeerId(link.Value));
            }
        }
        finally { _handlingLink = false; }
    }

    // An invite as pasted: the bare key, or a link that carries it.
    private static RoomKey ParseInvite(string text) =>
        DeepLink.Parse(text) is { Kind: DeepLinkKind.Join } link ? RoomKey.Parse(link.Value) : RoomKey.Parse(text.Trim());

    private ConversationVm? FindRoom(RoomKey key) =>
        _roomConversations.Values.FirstOrDefault(c => c.Invite != null && RoomIdOf(c.Invite) == key.RoomId);

    // Joins the room a key opens, after asking when it came from a link. The same room under a newer key just takes the key.
    private async Task JoinWithKeyAsync(RoomKey key, bool fromLink)
    {
        if (_settings.PrivateRooms.FirstOrDefault(r => r.Key == key.ToString() || RoomIdOf(r.Key) == key.RoomId) is { } have)
        {
            if (key.Epoch > EpochOf(have.Key) && _node != null)
            {
                if (fromLink && !await Dialogs.ConfirmAsync(this, "New room key",
                        $"This link has a newer key for \"{have.Label}\", which you are already in. Use it?", "Use it", "Cancel"))
                    return;
                await _node.JoinRoomAsync(key, have.Label);
                StatusText.Text = "Your key for that room is now the new one.";
                return;
            }
            if (fromLink)
            {
                if (FindRoom(key) is { } conv) SelectConversation(conv);
                StatusText.Text = "You are already in that room.";
            }
            else await Dialogs.MessageAsync(this, "Yaparr", "You are already in that room.");
            return;
        }

        if (fromLink && !await Dialogs.ConfirmAsync(this, "Join room",
                "A link is asking you to join a room.\n\nOnce you join, the other members can see your IP address, and anyone with the link can join too. Only join if you trust whoever sent it.",
                "Join", "Cancel"))
            return;

        var label = await PromptDialog.AskAsync(this, "A name for this room (only you see it):", "Room");
        if (string.IsNullOrWhiteSpace(label)) return;
        await JoinKeyedRoomAsync(label, key, persist: true);
    }

    private async Task ChatFromLinkAsync(PeerId id)
    {
        if (_node == null) return;
        if (id.Value == _node.AccountId.Value || id.Value == _node.Id.Value)
        {
            StatusText.Text = "That link is for your own ID.";
            return;
        }
        ConversationVm? existing;
        lock (_directConversations) _directConversations.TryGetValue(id, out existing);
        if (existing != null) { SelectConversation(existing); return; }

        if (!await Dialogs.ConfirmAsync(this, "Start a chat",
                $"A link is asking you to start a direct chat with {id.Short}…\n\nThey will be able to see your IP address. Only continue if you trust whoever sent it.",
                "Start chat", "Cancel"))
            return;
        await OpenDirectAsync(id);
    }

    // The room key on show copies when it is clicked, like the peer ID at the top left.
    private void RoomKey_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left) CopyInvite_Click(sender, new RoutedEventArgs());
    }

    private async void CopyInviteLink_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected?.Invite is not { } invite) return;
        StatusText.Text = await Dialogs.CopyAsync(this, DeepLink.RoomLink(invite))
            ? "Invite link copied. Anyone who has it can join this room, so share it carefully."
            : "Couldn't copy: another program is using the clipboard. Try again.";
    }

    private async void CopyChatLink_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected?.PeerId is not { } id) return;
        StatusText.Text = await Dialogs.CopyAsync(this, DeepLink.ChatLink(id))
            ? "A link to chat with them was copied to the clipboard."
            : "Couldn't copy: another program is using the clipboard. Try again.";
    }
}
