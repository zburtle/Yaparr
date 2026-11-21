using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.AppCore;
using Yaparr.Chat.Files;

namespace Yaparr.AndroidApp;

// Files of any size (docs/file-sharing.md): the + button offers a picture or a file; a file someone offered shows as a card with Download.
public sealed partial class MainView
{
    // The + button: a small menu, since there are now two things to attach.
    private void AttachClicked(object? sender, RoutedEventArgs e)
    {
        if (_current == null || _session == null || sender is not Control anchor) return;
        var menu = new ContextMenu();
        var picture = new MenuItem { Header = "Picture or GIF" };
        picture.Click += async (_, _) => await SendPictureFromChooserAsync();
        var file = new MenuItem { Header = "File of any size" };
        file.Click += async (_, _) => await SendFileFromChooserAsync();
        menu.Items.Add(picture);
        menu.Items.Add(file);
        menu.Open(anchor);
    }

    private async Task SendFileFromChooserAsync()
    {
        if (_current == null || _session == null || MainActivity.Instance is not { } activity) return;
        var chat = _current;
        var picked = await activity.PickSharedFileAsync();
        if (picked is not { } file) return;
        var caption = (InputBox.Text ?? "").Trim();
        try
        {
            ShowChatNote(chat, $"Getting {file.Name} ready…");
            var progress = new Progress<long>(done => ShowChatNote(chat, $"Getting {file.Name} ready… {FileTexts.Size(done)}"));
            await _session.SendFileAsync(chat, file.Address, file.Name, caption, progress);
            InputBox.Text = "";
            ShowChatNote(chat, $"{file.Name} is offered. It is only sent to people who download it, from where it is on this phone.");
            ScrollToEnd();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowChatNote(chat, ex is ArgumentException ? ex.Message : "That file could not be read: " + ex.Message);
        }
    }

    private void FileDownloadClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: Line { File: { } offer } line } || _session == null || MainActivity.Instance is not { } activity) return;
        if (!_session.HasRoomFor(offer.Size, activity.ReceivedFilesDirectory))
        {
            if (_current != null) ShowChatNote(_current, $"There is not enough room on this phone for {offer.Name} ({FileTexts.Size(offer.Size)}).");
            return;
        }
        _session.StartFile(line);
    }

    private void FilePauseClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Line line }) _session?.PauseFile(line);
    }

    private void FileResumeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Line line }) _session?.ResumeFile(line);
    }

    private void FileCancelClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Line line }) _session?.CancelFile(line);
    }

    // One tap: a copy in the phone's Downloads folder (Downloads/Yaparr), where the Files app and other apps can see it.
    private async void FileSaveToDownloadsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: Line { FilePath: { } path, File: { } offer } line } || MainActivity.Instance is not { } activity) return;
        if (_current != null) ShowChatNote(_current, $"Saving {offer.Name} to Downloads\u2026");
        var where = await activity.SaveToDownloadsAsync(path, offer.Name);
        if (where != null) line.SetSavedTo(where);
        if (_current != null) ShowChatNote(_current, where != null ? $"Saved to {where}." : "Could not save it to Downloads. Try Save as\u2026");
    }

    // Copies a downloaded file to a place the person picks; it stays where it is as well, and is still given to others from there.
    private async void FileSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: Line { FilePath: { } path, File: { } offer } } || MainActivity.Instance is not { } activity) return;
        bool saved = await activity.SaveCopyAsync(path, offer.Name);
        if (saved && sender is Control { DataContext: Line line }) line.SetSavedTo("the place you chose");
        if (_current != null) ShowChatNote(_current, saved ? "Saved." : "Not saved.");
    }
}
