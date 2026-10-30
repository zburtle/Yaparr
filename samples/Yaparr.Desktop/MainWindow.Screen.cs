using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Desktop;

// Sharing a screen with a room and watching one. FFmpeg does the capture and the decoding; it is found on this computer or
// downloaded once (with a question first).
public partial class MainWindow
{
    private ScreenSharer? _sharer;
    private ConversationVm? _sharerConv;
    private readonly Dictionary<string, ScreenViewWindow> _screenViews = new();

    private void RefreshShares(ConversationVm conv, ChatRoom room)
    {
        var shares = room.MediaGroups.Where(g => g.Kind == "screen").ToList();
        var watching = _screenViews.Keys.ToHashSet();
        conv.Shares.Clear();
        foreach (var g in shares)
            conv.Shares.Add(new ShareVm(g.Id, g.Name, isMine: _node != null && g.Root == _node.Id) { Watching = watching.Contains(g.Id) });
        conv.SharesChanged();

        // A share that ended closes its window.
        foreach (var id in watching.Where(id => shares.All(g => g.Id != id)).ToList())
            if (_screenViews.TryGetValue(id, out var window)) window.Close();
    }

    // The FFmpeg program, found here or downloaded after asking. Null if there is none.
    private async Task<string?> GetFfmpegAsync()
    {
        if (FfmpegLocator.Find() is { } found) return found;

        var package = FfmpegPackage.Default;
        bool yes = await Dialogs.ConfirmAsync(this, "FFmpeg is needed",
            $"Sharing and watching a screen needs FFmpeg, a free video program. Yaparr can download it once ({package.SizeBytes / 1_048_576} MB) from {package.Url.Host} and keep it in its own folder.\n\nDownload it now?",
            "Download", "Not now");
        if (!yes) return null;

        var (path, error) = await DownloadDialog.RunAsync(this, package);
        if (path == null && error != null)
        {
            _log.Write("FFmpeg download failed: " + error);
            await Dialogs.MessageAsync(this, "Could not get FFmpeg", error);
        }
        return path;
    }

    private async void ShareScreen_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null || _selected is not { IsRoom: true, InVoice: true } conv || _sharer != null) return;
        var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
        if (room == null) return;

        var ffmpeg = await GetFfmpegAsync();
        if (ffmpeg == null) return;

        var dialog = new ShareDialog();
        if (!await dialog.ShowDialog<bool>(this) || dialog.Result == null) return;

        try
        {
            var what = dialog.Result.WindowHandle != null ? "window" : "screen";
            var sharer = ScreenSharer.Start(_node, room, $"{_settings.DisplayName}'s {what}", dialog.Result, ffmpeg);
            sharer.Ended += ex => UI(() =>
            {
                if (_sharer != sharer) return;
                StopSharingCore();
                if (ex != null)
                {
                    _log.Write("screen sharing stopped: " + ex.Message);
                    StatusText.Text = "Screen sharing stopped: " + ex.Message;
                }
            });
            _sharer = sharer;
            _sharerConv = conv;
            conv.IsSharing = true;
            conv.Add(MessageVm.System($"You are sharing your {what}. Everyone in the room can watch it."));
        }
        catch (Exception ex)
        {
            _log.Write("could not start screen sharing: " + ex.Message);
            StatusText.Text = "Could not share the screen: " + ex.Message;
        }
    }

    private void StopSharing_Click(object? sender, RoutedEventArgs e) => StopSharingCore();

    private void StopSharingCore()
    {
        var sharer = _sharer;
        var conv = _sharerConv;
        _sharer = null;
        _sharerConv = null;
        if (conv != null)
        {
            conv.IsSharing = false;
        }
        try { sharer?.Dispose(); }
        catch (Exception ex) { _log.Write("stopping the screen share: " + ex.Message); }
    }

    private async void WatchScreen_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null || _selected is not { IsRoom: true, InVoice: true } conv || (sender as Button)?.DataContext is not ShareVm share) return;
        var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
        if (room == null) return;

        if (_screenViews.TryGetValue(share.Id, out var open)) { open.Close(); return; }

        var ffmpeg = await GetFfmpegAsync();
        if (ffmpeg == null) return;

        try
        {
            var view = _node.WatchMedia(room, share.Id);
            var window = new ScreenViewWindow(share.Name, view, ffmpeg);
            _screenViews[share.Id] = window;
            share.Watching = true;
            window.Stopped += () => UI(() =>
            {
                _screenViews.Remove(share.Id);
                share.Watching = false;
            });
            window.Show(this);
        }
        catch (Exception ex)
        {
            _log.Write("could not watch the screen: " + ex.Message);
            StatusText.Text = "Could not watch the screen: " + ex.Message;
        }
    }

    private void CloseScreens()
    {
        StopSharingCore();
        foreach (var window in _screenViews.Values.ToList()) window.Close();
    }
}
