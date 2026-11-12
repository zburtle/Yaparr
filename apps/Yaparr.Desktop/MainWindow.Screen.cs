using Avalonia.Controls;
using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Desktop;

// Sharing a screen with a room and watching one. FFmpeg does the capture and the decoding; it is found on this computer or
// downloaded once (with a question first).
public partial class MainWindow
{
    private ScreenSharer? _sharer;
    private ConversationVm? _sharerConv;
    private SharerStatsView? _sharerStats;

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

    // Which shares each room had at the last look, so a new one can be told from those that were already there.
    private readonly Dictionary<ConversationVm, PresenceTracker> _shareCues = new();

    private void PrimeShareCues(ConversationVm conv, ChatRoom room) =>
        _shareCues[conv] = new PresenceTracker(room.MediaGroups.Where(g => g.Kind == "screen").Select(g => g.Id));

    private void RefreshShares(ConversationVm conv, ChatRoom room)
    {
        // A sound when someone else starts sharing in the call you are in. Every share in the room counts as seen, whichever channel it is in, so
        // joining a call or moving to another channel does not announce the shares that were already going.
        if (!_shareCues.TryGetValue(conv, out var cues)) PrimeShareCues(conv, room);
        else
        {
            var all = room.MediaGroups.Where(g => g.Kind == "screen").ToList();
            var (arrived, _) = cues.Update(all.Select(g => g.Id));
            string? myChannel = _voiceConv == conv ? _voice?.ChannelId : null;
            if (_voiceConv == conv && conv.InVoice
                && all.Any(g => arrived.Contains(g.Id) && !(_node != null && _node.IsMyDevice(g.Root)) && (g.Channel ?? RoomChannels.DefaultVoice) == myChannel))
                _sounds.ShareStarted();
        }

        // In voice, the shares are those of the voice channel you are in; otherwise all of them, so you know to join.
        string? inChannel = _voiceConv == conv ? _voice?.ChannelId : null;
        var shares = room.MediaGroups.Where(g => g.Kind == "screen" && (inChannel == null || (g.Channel ?? RoomChannels.DefaultVoice) == inChannel)).ToList();
        var watching = _screenViews.Keys.ToHashSet();
        conv.Shares.Clear();
        foreach (var g in shares)
            conv.Shares.Add(new ShareVm(g.Id, g.Name, isMine: _node != null && _node.IsMyDevice(g.Root)) { Watching = watching.Contains(g.Id) });
        conv.SharesChanged();

        // A share that ended stops being watched.
        foreach (var id in watching.Where(id => shares.All(g => g.Id != id)).ToList())
            if (_screenViews.TryGetValue(id, out var watch) && !watch.Control.IsEnded)
            {
                // The sharer really stopped (not this call moving to a channel where that share is not shown): the picture stays up as a black screen that
                // says so, with a sound, until it is closed.
                if (room.MediaGroups.All(g => g.Id != id)) { _sounds.ShareEnded(); watch.Control.ShowEnded(); }
                else EndWatch(watch);
            }
    }

    // The FFmpeg program, found here or downloaded after asking. Null if there is none.
    private async Task<string?> GetFfmpegAsync()
    {
        if (FfmpegLocator.Find() is { } found) return found;

        var package = FfmpegPackage.ForThisSystem;
        if (package == null)
        {
            _log.Write("FFmpeg was not found and there is no download for this system");
            await Dialogs.MessageAsync(this, "FFmpeg is needed",
                "Sharing and watching a screen needs FFmpeg, a free video program. Install the ffmpeg package with your system's package manager (for example \"sudo apt install ffmpeg\"), then try again.");
            return null;
        }
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
        if (_node == null || _selected is not { IsRoom: true } conv || _sharer != null) return;
        var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
        if (room == null) return;
        // From this device when it is in the call, or when another of your devices is; with none of them in voice there is nothing to share into.
        if (!room.CanShareScreen) { StatusText.Text = "Join the voice channel on one of your devices first."; return; }

        var ffmpeg = await GetFfmpegAsync();
        if (ffmpeg == null) return;

        var dialog = new ShareDialog(_node.MeasuredUploadBytesPerSecond);
        dialog.PreferAv1(_settings.ScreenShareAv1);
        if (!await dialog.ShowDialog<bool>(this) || dialog.Result == null) return;
        var options = dialog.Result;
        if (_settings.ScreenShareAv1 != dialog.Av1Picked) { _settings.ScreenShareAv1 = dialog.Av1Picked; _data.SaveSettings(_settings); }

        // Under Wayland the desktop's own picker decides what is shared.
        if (ScreenCapture.UsesPortal)
        {
            var portal = await AskDesktopWhatToShareAsync(dialog.PortalWindow);
            if (portal == null) return;
            options = options.Retarget(portal);
        }

        try
        {
            var what = options.Portal?.IsWindow ?? options.WindowHandle != null ? "window" : "screen";
            var sharer = ScreenSharer.Start(_node, room, $"{_settings.DisplayName}'s {what}", options, ffmpeg);
            _log.Write($"sharing your {what}: {options.Fps} fps, {options.Kbps} kbit/s, up to {options.MaxWidth}x{options.MaxHeight}, a key frame every {options.KeyFrameSeconds:0.##} s, "
                       + $"asked for: {options.Codec}{(options.Encoder is { } asked ? " on " + asked : "")}{(options.GpuScale ? ", scaling on the card" : "")}{(options.InProcessCapture ? (options.CaptureOnCard ? ", capturing and encoding in this program, pictures kept on the card" : ", capturing and encoding in this program, pictures copied to memory") : options.InProcessEncoder ? ", encoding in this program" : "")}, "
                       + $"{(options.ShareSound ? "with sound" : "no sound")}, {(options.ShowCursor ? "with" : "without")} the pointer, FFmpeg {ffmpeg}");
            if (options.Portal == null)
                _log.Write(options.WindowHandle != null ? "what is shared: a window" : options.Display is { Width: > 0 } shown ? $"what is shared: a monitor of {shown.Width}x{shown.Height} at {shown.X},{shown.Y}{(shown.Primary ? ", the primary one" : "")}" : "what is shared: a monitor");
            if (options.Encoder is null or Yaparr.Screen.HardwareEncoder.Nvenc)
                _ = Task.Run(async () => { if (await NvidiaSmi.InfoAsync() is { } card) _log.Write("graphics card: " + card); });
            if (options.InProcessEncoder)
            {
                if (!FfmpegLibraries.TryLoad(out var libraryProblem))
                    _log.Write($"in-process capture and encoding {(options.InProcessAsked ? "asked for" : "is the default here")} but not usable: {libraryProblem} A separate FFmpeg is used.");
                else if (!options.InProcessAsked && !InProcessGuard.Allowed)
                    _log.Write($"in-process capture and encoding is the default here but off: the program ended in the middle of such a share {InProcessGuard.UncleanEndings} times in a row (see {InProcessGuard.MarkFile}); a separate FFmpeg is used until a share ends well, or YAPARR_INPROCESS=capture asks for it.");
                else _log.Write("in-process encoder: FFmpeg libraries " + FfmpegLibraries.Describe());
            }
            // Which encoder is really in use (not only which was asked for): in this program, a separate FFmpeg, or the next one after a failure.
            sharer.EncoderStarted += () =>
            {
                _log.Write($"screen sharing encoder: {ScreenCodecs.Describe(sharer.Codec, sharer.Encoder)}{(sharer.InProcess ? ", in this program" : ", separate FFmpeg")}{(sharer.FallbackReason is { } reason ? $" (not used: {reason})" : "")}");
                _log.Write("screen sharing encoder set up as: " + sharer.EncoderDetails);
            };
            sharer.KeyFrameAnswered += (ms, bytes) => _log.Write($"screen sharing: made a key frame {ms} ms after a viewer asked for one ({bytes / 1024} KB)");
            sharer.Ended += ex => UI(() =>
            {
                if (_sharer != sharer) return;
                _log.Write("screen sharing ended: " + SharingSummary(sharer));
                StopSharingCore(writeSummary: false);
                if (ex != null)
                {
                    _log.Write($"screen sharing stopped: {ex.Message} ({ex.GetType().Name})" + (sharer.LastErrors.Length > 0 ? " | FFmpeg: " + sharer.LastErrors : ""));
                    StatusText.Text = "Screen sharing stopped: " + ex.Message;
                }
            });
            sharer.SoundFailed += ex => UI(() =>
            {
                if (_sharer != sharer) return;
                _log.Write("the shared sound stopped: " + ex);
                conv.Add(MessageVm.System("The sound stopped: " + ex.Message + " The picture is still shared."));
            });
            _sharer = sharer;
            _shareStartedTicks = Stopwatch.GetTimestamp();
            using (var me = Process.GetCurrentProcess()) _shareAppCpu = me.TotalProcessorTime;
            _sharerConv = conv;
            GuardSharingWithoutVoice(sharer, conv, room);
            _sharerStats = new SharerStatsView(sharer, options.Fps, room);
            _sharerStats.IsMine = d => _node != null && _node.IsMyDevice(d);
            _sharerStats.ViewersChanged += (came, went) => { if (came > 0) _sounds.ViewerJoined(); else if (went > 0) _sounds.ViewerLeft(); };
            _sharerStats.StopRequested += () => StopSharingCore();
            _sharerStats.ChangeRequested += () => _ = ChangeSharedAsync(sharer, conv);
            SharerPanel.Child = _sharerStats;
            SharePreview.Attach(sharer, ffmpeg, wholeMonitor: what == "screen");
            UpdateSharerPanel();
            conv.IsSharing = true;
            conv.Add(MessageVm.System(conv.InVoice
                ? $"You are sharing your {what}. Everyone in the room can watch it."
                : $"You are sharing your {what} in the call on your other device. Everyone in the room can watch it. It stops when none of your devices is in the call."));
            if (options.ShareSound)
                conv.Add(MessageVm.System(sharer.SharingSound
                    ? (options.WindowHandle != null && options.Portal == null ? "The sound of that window is shared too." : "The sound of your computer is shared too, except Yaparr's own, so the voice chat does not go round.")
                    : "The sound could not be shared, so only the picture is: " + sharer.SoundProblem));
        }
        catch (Exception ex)
        {
            options.Portal?.Dispose();
            _log.Write("could not start screen sharing: " + ex.Message);
            StatusText.Text = "Could not share the screen: " + ex.Message;
        }
    }

    // Asks the desktop's ScreenCast portal (Wayland), which shows its own picker. Null if the user cancels or it cannot be done;
    // the reason goes to the status line.
    private async Task<PortalScreenCast?> AskDesktopWhatToShareAsync(bool window)
    {
        try
        {
            // Better to say so before the desktop asks the user to choose than after.
            if (await Task.Run(PortalPipeline.Missing) is { } missing)
            {
                _log.Write("screen sharing on Wayland is not possible: " + missing);
                StatusText.Text = "Could not share the screen: " + missing;
                return null;
            }
            StatusText.Text = "Choose what to share in the window the desktop shows...";
            var portal = await PortalScreenCast.StartAsync(window, showCursor: true);
            StatusText.Text = "";
            return portal;
        }
        catch (OperationCanceledException) { StatusText.Text = ""; return null; }
        catch (Exception ex)
        {
            _log.Write("the desktop would not share the screen: " + ex.Message);
            StatusText.Text = "Could not share the screen: " + ex.Message;
            return null;
        }
    }

    private void StopSharing_Click(object? sender, RoutedEventArgs e) => StopSharingCore();

    // A share belongs to a call. This device may share while another of your devices is the one in the call, so the share ends when no device of
    // yours is in that channel any more. The announcements of the call come every few seconds and can be late, so it takes a short silence.
    private void GuardSharingWithoutVoice(ScreenSharer sharer, ConversationVm conv, ChatRoom room)
    {
        var channel = sharer.Source.Info.Channel ?? RoomChannels.DefaultVoice;
        int missed = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            if (_sharer != sharer) { timer.Stop(); return; }
            missed = room.ScreenShareChannel == channel ? 0 : missed + 1;
            if (missed < 3) return;
            timer.Stop();
            _log.Write("screen sharing ended: none of your devices is in the voice channel any more");
            StopSharingCore();
            conv.Add(MessageVm.System("Screen sharing stopped: none of your devices is in the call any more."));
        };
        timer.Start();
    }

    // What the last share looked like when it ended, for "Copy sharing diagnostics".
    private string? _lastSharingReport;

    // How the last few watches went, kept after the picture is closed (the sharing diagnostics are often copied afterwards).
    private readonly List<string> _lastWatchReports = new();

    /// <summary>What a share did, in one line for the log: the encoder, the frames and key frames, the encoder's timings and the capture's processor time.</summary>
    // When the share began and what this program had used by then, so that the totals can say what the share cost this program (a share too short for
    // the per-minute line, or one the line missed, still says it).
    private long _shareStartedTicks;
    private TimeSpan _shareAppCpu;

    private string SharingSummary(ScreenSharer sharer)
    {
        double seconds = _shareStartedTicks == 0 ? 0 : Stopwatch.GetElapsedTime(_shareStartedTicks).TotalSeconds;
        TimeSpan used;
        using (var me = Process.GetCurrentProcess()) used = me.TotalProcessorTime - _shareAppCpu;
        string program = seconds >= 1 ? $", this program used {used.TotalSeconds:0.0} s of processor time over {seconds:0} s ({used.TotalSeconds / seconds * 100:0}% of a core)" : "";
        var figures = sharer.Figures();
        return $"{ScreenCodecs.Describe(sharer.Codec, sharer.Encoder)}{(sharer.InProcess ? ", in this program" : "")}{(sharer.FallbackReason is { } why ? $" (not used: {why})" : "")}, "
               + $"{sharer.Frames} frames sent; {figures.DescribeKeys()}{(figures.Encodes > 0 ? "; " + figures.DescribeEncoding(sharer.FrameBudgetMs) : "")}; "
               + (sharer.CaptureInProcess ? (sharer.CaptureOnCard ? "capture in this program, pictures kept on the card" : "capture in this program, pictures copied to memory") : $"capture program used {sharer.CaptureCpu.TotalSeconds:0.0} s of processor time, restarted {figures.Restarts} times") + program;
    }

    // The totals go to the log when a share stops (the ended handler writes its own, with the reason, and asks not to).
    private void StopSharingCore(bool writeSummary = true)
    {
        var sharer = _sharer;
        if (sharer != null) _lastSharingReport = sharer.Describe();
        if (sharer != null && writeSummary) _log.Write("screen sharing stopped: " + SharingSummary(sharer));
        var conv = _sharerConv;
        _sharer = null;
        _sharerConv = null;
        _sharerStats = null;
        SharerPanel.Child = null;
        SharerPanel.IsVisible = false;
        SharePreview.Detach();
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
            _log.Write($"watching the screen {share.Name}");
            var view = _node.WatchMedia(room, share.Id);
            var control = new ScreenViewControl(share.Name, view, ffmpeg);
            control.DecoderFailed += message => _log.Write($"the picture of the screen {share.Name} stopped: {message}");
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
        if (watch.Control.LastReport is { } report)
        {
            _lastWatchReports.Add("(ended) " + report);
            while (_lastWatchReports.Count > 3) _lastWatchReports.RemoveAt(0);
        }
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
        if (_inlineWatch is { } other && other != watch)
        {
            if (other.Control.IsEnded) EndWatch(other);   // nothing left to show: it closes instead of opening a window for a notice
            else PopOut(other);
        }
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

    // Moves a running share to another monitor or window; watchers stay connected.
    private async Task ChangeSharedAsync(ScreenSharer sharer, ConversationVm conv)
    {
        var dialog = new ShareDialog(sharer.Options);
        if (!await dialog.ShowDialog<bool>(this) || dialog.Result == null || _sharer != sharer) return;
        var next = dialog.Result;
        if (ScreenCapture.UsesPortal)
        {
            var portal = await AskDesktopWhatToShareAsync(dialog.PortalWindow);
            if (portal == null || _sharer != sharer) { portal?.Dispose(); return; }
            next = next.Retarget(portal);
        }
        try
        {
            sharer.Change(next);
            var what = next.Portal?.IsWindow ?? next.WindowHandle != null ? "a window" : "a monitor";
            _log.Write($"screen sharing changed to {what}");
            SharePreview.SetKind(wholeMonitor: !(next.Portal?.IsWindow ?? next.WindowHandle != null));
            conv.Add(MessageVm.System($"You are now sharing {what}."));
        }
        catch (Exception ex)
        {
            _log.Write("could not change what is shared: " + ex.Message);
            StatusText.Text = "Could not change what is shared: " + ex.Message;
        }
    }

    // The readout of your own share shows in the room you share to.
    private void UpdateSharerPanel() => SharerPanel.IsVisible = SharePreview.IsVisible = _sharerStats != null && _selected == _sharerConv;

    private void CloseScreens()
    {
        StopSharingCore();
        StopAllWatches();
    }
}
