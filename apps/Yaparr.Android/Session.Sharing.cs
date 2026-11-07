using Android.Content;
using Avalonia.Threading;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

// Sharing this phone's screen with a room. Like the computer, a share goes into a voice channel: the one this phone is in, or the one another of the
// person's devices is in; with none of their devices in a call there is nothing to share into.
public sealed partial class Session
{
    private ScreenShareJob? _shareJob;
    private Chat? _shareChat;
    private Context? _shareContext;
    private Timer? _shareGuard;

    /// <summary>
    /// Android 14 and newer only: its dialog has "Share one app", which keeps the status bar, notifications and other apps out of the picture. Older
    /// versions can only share the whole screen, so notifications would show; they are left out.
    /// </summary>
    public static bool CanShareHere => OperatingSystem.IsAndroidVersionAtLeast(34);

    /// <summary>The room being shared into, or null.</summary>
    public Chat? SharingChat => _shareChat;

    /// <summary>
    /// Starts a share with what the person agreed to in Android's dialog (<see cref="MainActivity.RequestScreenCaptureAsync"/>). Throws
    /// <see cref="InvalidOperationException"/> when none of their devices is in a voice channel of the room.
    /// </summary>
    internal void StartScreenShare(Chat chat, Context context, int resultCode, Intent consent, ShareQuality quality, bool shareSound)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(34)) throw new InvalidOperationException("Sharing the screen needs Android 14 or newer.");
        if (_node == null || chat.Room is not { } room || _shareJob != null) return;
        var channel = room.ScreenShareChannel ?? throw new InvalidOperationException("Join the voice channel on one of your devices first.");

        var source = _node.StartMedia(room, "screen", $"{_node.DisplayName}'s screen", signed: true, upstream: false, kbps: quality.Kbps, channel: channel);
        // The sound is a stream of its own that names the share it belongs to, so viewers find it (see ScreenSharer.SoundNameFor).
        MediaSource? sound = shareSound ? _node.StartMedia(room, Yaparr.Screen.ScreenSharer.SoundKind, Yaparr.Screen.ScreenSharer.SoundNameFor(source.Info.Id), signed: true, upstream: false, kbps: 96, channel: source.Info.Channel) : null;
        var job = new ScreenShareJob(context, source, quality, sound);
        job.SoundProblem += reason => UI(() => Notice?.Invoke(reason));
        job.Ended += reason => UI(() => EndScreenShare(job, reason));
        _shareJob = job;
        _shareChat = chat;
        _shareContext = context;
        chat.IsSharing = true;

        // The announcements of the call can be a few seconds late: the share ends when none of the person's devices has been in its channel for a while.
        int missed = 0;
        _shareGuard = new Timer(_ => UI(() =>
        {
            if (_shareJob != job) return;
            missed = room.ScreenShareChannel == channel ? 0 : missed + 1;
            if (missed < 3) return;
            Notice?.Invoke("Screen sharing stopped: none of your devices is in the call any more.");
            StopScreenShare();
        }), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));

        ScreenShareService.Begin(context, job, resultCode, consent);
    }

    /// <summary>Stops sharing (the person's choice, or the call ended).</summary>
    public void StopScreenShare()
    {
        var job = _shareJob;
        var context = _shareContext;
        if (job == null) return;
        EndScreenShare(job, null);
        if (context != null && OperatingSystem.IsAndroidVersionAtLeast(34)) ScreenShareService.End(context);
    }

    private void EndScreenShare(ScreenShareJob job, string? reason)
    {
        if (_shareJob != job) return;
        var chat = _shareChat;
        var context = _shareContext;
        _shareJob = null;
        _shareChat = null;
        _shareContext = null;
        _shareGuard?.Dispose();
        _shareGuard = null;
        if (chat != null) chat.IsSharing = false;
        if (context != null && OperatingSystem.IsAndroidVersionAtLeast(34)) ScreenShareService.End(context);
        if (reason != null) Notice?.Invoke(reason);
    }
}
