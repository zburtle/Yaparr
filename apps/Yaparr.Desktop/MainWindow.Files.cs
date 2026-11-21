using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.Chat;
using Yaparr.Chat.Files;

namespace Yaparr.Desktop;

// Files of any size (docs/file-sharing.md): the 📄 button offers a file in the open room or chat, and a file someone offered shows as a card with Download.
// Nothing leaves this computer until someone downloads, and the file stays where it is.
public partial class MainWindow
{
    private readonly Dictionary<string, List<MessageVm>> _fileVms = new();

    // A message that offers a file: shown as a card, and kept up to date as the file moves.
    private void TrackFile(MessageVm vm)
    {
        if (vm.File is not { } offer) return;
        if (!_fileVms.TryGetValue(offer.Root, out var list)) _fileVms[offer.Root] = list = new List<MessageVm>();
        list.Add(vm);
        ShowFile(vm);
    }

    private void ShowFile(MessageVm vm)
    {
        if (vm.File is not { } offer || _node == null) return;
        vm.SetFile(_node.FileProgressOf(offer.Root), _node.FilePath(offer.Root));
    }

    private void OnFileTransferChanged(FileProgress progress)
    {
        if (_node == null || !_fileVms.TryGetValue(progress.Root, out var list)) return;
        var path = _node.FilePath(progress.Root);
        foreach (var vm in list) vm.SetFile(progress.State == FileState.Completed ? null : progress, path);
    }

    private async void SendFileButton_Click(object? sender, RoutedEventArgs e)
    {
        var path = await Dialogs.PickOpenAsync(this, "Send a file", Dialogs.AllFiles);
        if (path != null) await SendFileOfferAsync(path);
    }

    private async Task SendFileOfferAsync(string path)
    {
        var conv = _selected;
        if (conv == null || _node == null) return;
        if (!_node.FileSharingAvailable) { await Dialogs.MessageAsync(this, "Yaparr", "Sharing files is not available right now."); return; }
        try
        {
            var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
            var direct = _directConversations.FirstOrDefault(kv => kv.Value == conv);
            var name = Path.GetFileName(path);
            long size = new FileInfo(path).Length;
            var progress = new Progress<long>(done => StatusText.Text = $"Getting {name} ready… {(size > 0 ? done * 100 / size : 0)}%");
            StatusText.Text = $"Getting {name} ready…";
            var offer = await _node.ShareFileAsync(path, room, room == null && direct.Value == conv ? direct.Key : null, progress);
            var caption = InputBox.Text?.Trim() ?? "";
            var id = await conv.Send(offer.ToText(caption));
            InputBox.Text = "";
            StatusText.Text = $"{name} is offered. It is only sent to people who download it, from where it is on this computer.";
            var vm = new MessageVm(DateTimeOffset.Now, _node.DisplayName, caption, isMine: true, id: id)
            {
                File = offer, Channel = conv.IsRoom ? conv.CurrentChannel : null, Key = ChatMessage.KeyOf(_node.Id, id),
            };
            conv.Add(vm);
            TrackFile(vm);
            ScrollToEnd();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = ex.Message;
            await Dialogs.MessageAsync(this, "Yaparr", ex is ArgumentException ? ex.Message : "That file could not be read: " + ex.Message);
        }
    }

    private async void FileDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MessageVm { File: { } offer, Source: { } message } vm } || _node == null) return;
        var destination = await Dialogs.PickSaveAsync(this, "Save the file", offer.Name, Dialogs.AllFiles);
        if (destination == null) return;
        vm.SetFile(_node.StartFileDownload(message, destination), null);
    }

    private void FilePause_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MessageVm { File: { } offer } } && _node != null) _node.PauseFile(offer.Root);
    }

    private void FileResume_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MessageVm { File: { } offer } } && _node != null) _node.ResumeFile(offer.Root);
    }

    private async void FileCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MessageVm { File: { } offer } vm } || _node == null) return;
        if (!await Dialogs.ConfirmAsync(this, "Stop downloading", "Stop downloading and throw away what has arrived of this file? You can download it again later.", yes: "Stop and discard", no: "Keep going")) return;
        _node.CancelFile(offer.Root);
        vm.SetFile(new FileProgress(offer.Root, FileState.Canceled, 0, offer.Size, 0, 0), null);
    }

    // Shows the file in the folder it is in. A received file is never opened from here: a program someone sent would run.
    private void FileReveal_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MessageVm { FilePath: { } path } }) return;
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", new[] { "-R", path });
            else Process.Start(new ProcessStartInfo("xdg-open", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            StatusText.Text = "Could not open the folder: " + ex.Message;
        }
    }
}
