using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.AppCore;
using Yaparr.Chat.Files;

namespace Yaparr.AndroidApp;

// Files of any size (docs/file-sharing.md): the + button offers a picture or a file; a file someone offered shows as a card with Download.
public sealed partial class MainView
{
    // The + button: a sheet from the bottom, since there are now two things to attach.
    private void AttachClicked(object? sender, RoutedEventArgs e)
    {
        if (_current == null || _session == null) return;
        ActionTitle.Text = "Add to the message";
        ActionList.Children.Clear();
        AddAction("Image", async () => await SendPictureFromChooserAsync());
        AddAction("File of any size", async () => await SendFileFromChooserAsync());
        AddAction("Poll", ShowPollForm);
        ActionPanel.IsVisible = true;
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

    // Download asks where to keep the file first; the app then remembers that place as where the file is.
    private async void FileDownloadClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: Line { File: { } offer } line } || _session == null || MainActivity.Instance is not { } activity) return;
        // The pieces arrive in the app's folder first (they come from several people at once), then the finished file goes to the chosen place.
        if (!_session.HasRoomFor(offer.Size, activity.ReceivedFilesDirectory))
        {
            if (_current != null) ShowChatNote(_current, $"There is not enough room on this phone for {offer.Name} ({FileTexts.Size(offer.Size)}).");
            return;
        }
        if (await activity.PickSaveLocationAsync(offer.Name) is not { } place) return;
        _session.StartFile(line, place.Address);
        line.SetSavedTo(place.Name);
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
        var saved = await activity.SaveToDownloadsWithAddressAsync(path, offer.Name);
        if (saved is { } s) line.SetSavedTo(s.Where, s.Address);
        if (_current != null) ShowChatNote(_current, saved != null ? $"Saved to {saved.Value.Where}." : "Could not save it to Downloads. Try Save as\u2026");
    }

    // Opens the file in another app. A file sent from this phone is opened where it is; a download is first copied to Downloads (once), since its own folder is private to Yaparr.
    private async void FileOpenClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: Line { FilePath: { } path, File: { } offer } line } || MainActivity.Instance is not { } activity) return;
        var address = path.Contains("://", StringComparison.Ordinal) ? path : line.SavedAddress;
        if (address == null)
        {
            var saved = await activity.SaveToDownloadsWithAddressAsync(path, offer.Name);
            if (saved is not { } s)
            {
                if (_current != null) ShowChatNote(_current, "Could not open it. Try Save as\u2026 and open it from there.");
                return;
            }
            line.SetSavedTo(s.Where, s.Address);
            address = s.Address;
        }
        if (!activity.OpenFile(address, offer.Name) && _current != null) ShowChatNote(_current, "No app on this phone can open that file.");
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
