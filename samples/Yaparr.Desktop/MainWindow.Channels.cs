using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaparr.Chat;

namespace Yaparr.Desktop;

// A room's text and voice channels, shown as buttons under its name. A text channel button shows that channel's messages; a voice
// channel button joins it. The owner and the admins add, rename and remove channels.
public partial class MainWindow
{
    // Makes the buttons match the room: its channels, whether you may change them, and who is in each voice channel.
    private void RefreshChannels(ConversationVm conv, ChatRoom room)
    {
        conv.SetChannels(room.Channels);
        conv.ShowChannels = room.Owner != null;
        conv.CanManageChannels = room.MyRole >= RoomRole.Admin;
        conv.IsOwner = room.MyRole == RoomRole.Owner;
        // A voice channel that is removed while you are in it sends you out of voice.
        if (_voiceConv == conv && _voice is { } session && room.Channels.All(c => c.Id != session.ChannelId))
        {
            LeaveVoiceCore();
            StatusText.Text = "The voice channel you were in was removed.";
        }
        UpdateVoiceChannels(conv, room);
        if (conv == _selected) UpdateComposer();
    }

    // The people in each voice channel and the one you are in.
    private void UpdateVoiceChannels(ConversationVm conv, ChatRoom room)
    {
        var people = room.VoiceParticipants;
        string? mine = _voiceConv == conv ? _voice?.ChannelId : null;
        foreach (var channel in conv.Channels.Where(c => c.IsVoice))
        {
            channel.People = people.Count(p => p.Channel == channel.Id);
            channel.Mine = mine == channel.Id;
        }
    }

    // The message box says where a message will go.
    private void UpdateComposer()
    {
        InputBox.PlaceholderText = _selected is { IsRoom: true, ShowChannels: true } conv ? $"Message #{conv.CurrentChannelName}" : "Write a message";
    }

    private (ConversationVm Conv, ChatRoom Room)? OpenRoom()
    {
        if (_selected is not { IsRoom: true } conv) return null;
        var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
        return room == null ? null : (conv, room);
    }

    private void Channel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChannelVm channel } || OpenRoom() is not var (conv, room)) return;
        if (channel.IsVoice)
        {
            JoinVoiceIn(conv, room, channel.Id);
            return;
        }
        conv.SelectChannel(channel.Id);
        UpdateComposer();
        ScrollToEnd();
        InputBox.Focus();
    }

    private void Channel_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { DataContext: ChannelVm channel } button || OpenRoom() is not var (_, room) || room.MyRole < RoomRole.Admin) return;
        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += async (_, _) => await RenameChannelAsync(room, channel);
        var items = new List<Control> { rename };
        if (!channel.IsDefault)
        {
            var delete = new MenuItem { Header = "Delete" };
            delete.Click += async (_, _) => await DeleteChannelAsync(room, channel);
            items.Add(delete);
        }
        new ContextMenu { ItemsSource = items }.Open(button);
        e.Handled = true;
    }

    private async Task RenameChannelAsync(ChatRoom room, ChannelVm channel)
    {
        var name = await PromptDialog.AskAsync(this, "A new name for the channel:", channel.Name);
        if (string.IsNullOrWhiteSpace(name) || name == channel.Name) return;
        try { room.RenameChannel(channel.Id, name); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { StatusText.Text = ex.Message; }
    }

    private async Task DeleteChannelAsync(ChatRoom room, ChannelVm channel)
    {
        var text = channel.IsText
            ? $"Delete the channel \"{channel.Name}\" for everyone? Its messages stay on people's computers for a few days but are no longer shown."
            : $"Delete the voice channel \"{channel.Name}\" for everyone? Anyone in it is moved out.";
        if (!await Dialogs.ConfirmAsync(this, "Delete channel", text, "Delete", "Keep")) return;
        try { room.DeleteChannel(channel.Id); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { StatusText.Text = ex.Message; }
    }

    private void NewTextChannel_Click(object? sender, RoutedEventArgs e) => _ = NewChannelAsync(ChannelKind.Text);

    private void NewVoiceChannel_Click(object? sender, RoutedEventArgs e) => _ = NewChannelAsync(ChannelKind.Voice);

    private async Task NewChannelAsync(ChannelKind kind)
    {
        if (OpenRoom() is not var (_, room)) return;
        var name = await PromptDialog.AskAsync(this, kind == ChannelKind.Text ? "A name for the new text channel:" : "A name for the new voice channel:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { room.CreateChannel(name, kind); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { StatusText.Text = ex.Message; }
    }
}
