using Yaparr.Chat;
using Yaparr.Chat.Files;

namespace Yaparr.AndroidApp;

// Files of any size (docs/file-sharing.md). A file is offered from where it is on the phone (the file chooser's address, kept readable), and only sent to people who tap Download.
// A downloaded file is kept in the app's own folder, from where it is also given on to others; "Save a copy" puts one somewhere else.
public sealed partial class Session
{
    private readonly Dictionary<string, List<Line>> _fileLines = new();

    private void TrackFile(Line line)
    {
        if (line.File is not { } offer) return;
        if (!_fileLines.TryGetValue(offer.Root, out var list)) _fileLines[offer.Root] = list = new List<Line>();
        list.Add(line);
        ShowFile(line);
    }

    private void ShowFile(Line line)
    {
        if (line.File is not { } offer || _node == null) return;
        line.SetFile(_node.FileProgressOf(offer.Root), _node.FilePath(offer.Root));
    }

    private void OnFileTransferChanged(FileProgress progress)
    {
        if (_node == null || !_fileLines.TryGetValue(progress.Root, out var list)) return;
        var path = _node.FilePath(progress.Root);
        foreach (var line in list) line.SetFile(progress.State == FileState.Completed ? null : progress, path);
    }

    /// <summary>Offers a file that was chosen with the system's file chooser (its address and name) in the chat. The file is only read, and stays where it is.</summary>
    public async Task SendFileAsync(Chat chat, string address, string name, string caption, IProgress<long>? progress = null)
    {
        if (_node is not { } node) return;
        if (!node.FileSharingAvailable) throw new InvalidOperationException("Sharing files is not available right now.");
        var offer = await node.ShareFileAsync(address, chat.Room, chat.Direct?.PeerId, progress, name: name);
        var text = offer.ToText(caption);
        string id;
        string? channel = null;
        if (chat.Room is { } room)
        {
            channel = chat.CurrentChannel;
            id = channel == RoomChannels.DefaultText ? await room.SendAsync(text) : await room.SendAsync(text, channel);
        }
        else id = await chat.Direct!.SendAsync(text);
        var line = new Line(ChatMessage.KeyOf(node.Id, id), node.DisplayName, caption, DateTimeOffset.Now, mine: true, channel, OwnMessage(chat, id, text, channel, null)) { FileOffered = offer };
        if (chat.Add(line)) TrackFile(line);
        Resort(chat);
    }

    /// <summary>True when the phone has room for a file of this size in the folder received files are kept in (a little more, for the pieces being written).</summary>
    public bool HasRoomFor(long bytes, string directory)
    {
        try { return new Android.OS.StatFs(directory).AvailableBytes > bytes + 64L * 1024 * 1024; }
        catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or IOException) { return true; }
    }

    /// <summary>Starts downloading the file a message offers into the app's folder for received files.</summary>
    public FileProgress? StartFile(Line line)
    {
        if (_node == null || line.Source == null || line.File is not { } offer || MainActivity.Instance is not { } activity) return null;
        var destination = Path.Combine(activity.ReceivedFilesDirectory, offer.Name);
        var progress = _node.StartFileDownload(line.Source, destination);
        line.SetFile(progress, _node.FilePath(offer.Root));
        return progress;
    }

    public void PauseFile(Line line) { if (line.File is { } o) _node?.PauseFile(o.Root); }
    public void ResumeFile(Line line) { if (line.File is { } o) _node?.ResumeFile(o.Root); }

    public void CancelFile(Line line)
    {
        if (line.File is not { } offer || _node == null) return;
        _node.CancelFile(offer.Root);
        line.SetFile(new FileProgress(offer.Root, FileState.Canceled, 0, offer.Size, 0, 0), null);
    }
}
