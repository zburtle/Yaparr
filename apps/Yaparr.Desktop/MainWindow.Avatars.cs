using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Yaparr.AppCore;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// Profile pictures: choosing yours, and showing everyone's when they arrive.
public partial class MainWindow
{
    // A picture arrived or changed: the lists and the messages of that person show it.
    private void OnAvatarChanged(PeerId account)
    {
        if (_node == null) return;
        if (account == _node.AccountId) ShowOwnName(_node.DisplayName);
        RefreshPeers();
        if (_directConversations.TryGetValue(account, out var direct)) direct.AvatarChanged();
        string id = account.Value;
        foreach (var conv in _roomConversations.Values.Concat(_directConversations.Values))
            foreach (var message in conv.AllMessages)
                if (message.Account == id || message.IsMine && account == _node.AccountId) message.AvatarChanged();
    }

    // A click on your picture at the top left chooses a new one.
    private async void ProfileAvatar_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || _node == null) return;
        e.Handled = true;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose your profile picture",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Pictures") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp" } } },
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            var bytes = await ReadLimitedAsync(stream, 40 * 1024 * 1024);
            if (bytes == null) { StatusText.Text = "That file is too big to use as a picture."; return; }
            _node.SetAvatar(ImageTools.Avatar(bytes));
            StatusText.Text = "Your profile picture is set. The people you are connected to will see it.";
        }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; }
        catch (IOException ex) { StatusText.Text = "Could not read that file: " + ex.Message; }
    }

    private void RemoveAvatar_Click(object? sender, RoutedEventArgs e)
    {
        if (_node?.MyAvatar == null) return;
        _node.SetAvatar(null);
        StatusText.Text = "Your profile picture is removed.";
    }
}
