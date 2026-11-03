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
    private SharerStatsWindow? _sharerStats;

    // A screen being watched. Its picture is either in the main window over the chat (only one at a time) or in a window of its
    // own; moving it does not restart the stream.
    private sealed class ScreenWatch
    {
        public required string Id { get; init; }
        public required ShareVm Share { get; init; }
        public required ConversationVm Conv { get; init; }
        public required ScreenViewControl Control { get; init; }
        public ScreenViewWindow? Window { get; set; }
    }

    private readonly Dictionary<string, ScreenWatch> _screenViews = new();
    private ScreenWatch? _inlineWatch;

    private void RefreshShares(ConversationVm conv, ChatRoom room)
    {
        // In voice, the shares are those of the voice channel you are in; otherwise all of them, so you know to join.
        string? inChannel = _voiceConv == conv ? _voice?.ChannelId : null;
        var shares = room.MediaGroups.Where(g => g.Kind == "screen" && (inChannel == null || (g.Channel ?? RoomChannels.DefaultVoice) == inChannel)).ToList();
        var watching = _screenViews.Keys.ToHashSet();
        conv.Shares.Clear();
        foreach (var g in shares)
            conv.Shares.Add(new ShareVm(g.Id, g.Name, isMine: _node != null && g.Root == _node.Id) { Watching = watching.Contains(g.Id) });
        conv.SharesChanged();

        // A share that ended stops being watched.
        foreach (var id in watching.Where(id => shares.All(g => g.Id != id)).ToList())
            if (_screenViews.TryGetValue(id, out var watch)) EndWatch(watch);
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
            _sharerStats = new SharerStatsWindow(sharer, dialog.Result.Fps, room);
            _sharerStats.Show(this);
            conv.IsSharing = true;
            conv.Add(MessageVm.System($"You are sharing your {what}. Everyone in the room can watch it."));
            if (dialog.Result.ShareSound)
                conv.Add(MessageVm.System(sharer.SharingSound
                    ? (dialog.Result.WindowHandle != null ? "The sound of that window is shared too." : "The sound of your computer is shared too, except Yaparr's own, so the voice chat does not go round.")
                    : "The sound could not be shared, so only the picture is: " + sharer.SoundProblem));
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
        _sharerStats?.Close();
        _sharerStats = null;
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

        if (_screenViews.TryGetValue(share.Id, out var open)) { EndWatch(open); return; }

        var ffmpeg = await GetFfmpegAsync();
        if (ffmpeg == null) return;

        try
        {
            var view = _node.WatchMedia(room, share.Id);
            var control = new ScreenViewControl(share.Name, view, ffmpeg);
            // The sound, if the sharer shares it, is a stream of its own that names this share.
            if (room.MediaGroups.FirstOrDefault(g => g.Kind == ScreenSharer.SoundKind && g.Name == ScreenSharer.SoundNameFor(share.Id)) is { } sound)
            {
                try { control.AttachSound(_node.WatchMedia(room, sound.Id), _settings.VoiceOutput); }
                catch (Exception ex) { _log.Write("could not watch the sound of the screen: " + ex.Message); }
            }
            control.ConnectionLost += () => { _log.Write($"lost the connection to the screen {share.Name}"); _sounds.ConnectionLost(); };
            control.ConnectionRestored += () => { _log.Write($"connection to the screen {share.Name} restored"); _sounds.ConnectionBack(); };
            var watch = new ScreenWatch { Id = share.Id, Share = share, Conv = conv, Control = control };
            _screenViews[share.Id] = watch;
            share.Watching = true;
            control.Stopped += () => UI(() => WatchEnded(watch));
            control.StopRequested += () => EndWatch(watch);
            control.DockToggleRequested += () =>
            {
                if (watch.Window == null) PopOut(watch); else DockIn(watch);
                // The next screen opens where this one was left.
                _settings.WatchScreensInWindow = watch.Window != null;
                try { _data.SaveSettings(_settings); }
                catch (Exception ex) { _log.Write("could not save the screen view choice: " + ex.Message); }
            };
            // It opens where it was last put: in a window of its own, or in the app over the chat.
            if (_settings.WatchScreensInWindow) PopOut(watch); else DockIn(watch);
        }
        catch (Exception ex)
        {
            _log.Write("could not watch the screen: " + ex.Message);
            StatusText.Text = "Could not watch the screen: " + ex.Message;
        }
    }

    // Ends the watching, wherever the picture is.
    private void EndWatch(ScreenWatch watch)
    {
        if (watch.Window is { } window) window.Close();   // its Closed handler stops the control
        else watch.Control.StopWatching();
    }

    private void StopAllWatches()
    {
        foreach (var watch in _screenViews.Values.ToList()) EndWatch(watch);
    }

    private void WatchEnded(ScreenWatch watch)
    {
        _screenViews.Remove(watch.Id);
        watch.Share.Watching = false;
        if (_inlineWatch == watch)
        {
            _inlineWatch = null;
            ScreenHost.Child = null;
        }
        watch.Window = null;   // a window it was in is closed already, or is closing
        UpdateInlineScreen();
    }

    private void PopOut(ScreenWatch watch)
    {
        if (watch.Window != null) return;
        if (_inlineWatch == watch)
        {
            _inlineWatch = null;
            ScreenHost.Child = null;
        }
        var window = new ScreenViewWindow(watch.Control);
        watch.Window = window;
        watch.Control.Docked = false;
        window.Show(this);
        UpdateInlineScreen();
    }

    private void DockIn(ScreenWatch watch)
    {
        // The place over the chat holds one picture; a screen that was there moves out to a window of its own.
        if (_inlineWatch is { } other && other != watch) PopOut(other);
        if (watch.Window is { } window)
        {
            watch.Window = null;
            window.HandedBack = true;
            window.Content = null;
            window.Close();
        }
        _inlineWatch = watch;
        watch.Control.Docked = true;
        ScreenHost.Child = watch.Control;
        UpdateInlineScreen();
    }

    // The picture over the chat shows while you are looking at the room it belongs to; it keeps playing when you look elsewhere.
    private void UpdateInlineScreen() => ScreenOverlay.IsVisible = _inlineWatch is { } watch && _selected == watch.Conv;

    private void CloseScreens()
    {
        StopSharingCore();
        StopAllWatches();
    }
}
