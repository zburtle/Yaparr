using Android.Views;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.AndroidApp;

// Watching the screens other people share with a room. Like the computer, a share is seen from the voice channel it was started in, so this is for a
// phone that is in the call.
public sealed partial class Session
{
    private ScreenWatch? _watch;
    private Chat? _watchChat;

    /// <summary>The screen being watched ended or was stopped (the sharer stopped, or this phone left the call).</summary>
    public event Action? WatchEnded;

    /// <summary>The screen being watched, or null.</summary>
    internal ScreenWatch? Watching => _watch;

    /// <summary>Lists the screens shared in the voice channel this phone is in (none when it is not in a call), yours marked so that they are not offered back to you.</summary>
    private void RefreshShares(Chat chat)
    {
        if (chat.Room is not { } room || _node == null) return;
        string? inChannel = _callChat == chat ? chat.VoiceChannelId : null;
        var shares = inChannel == null
            ? new List<ShareItem>()
            : room.MediaGroups.Where(g => g.Kind == "screen" && (g.Channel ?? RoomChannels.DefaultVoice) == inChannel)
                .Select(g => new ShareItem(g.Id, g.Name, _node.IsMyDevice(g.Root))).ToList();
        chat.SetShares(shares);
        // A share that ended stops being watched.
        if (_watch != null && _watchChat == chat && shares.All(s => s.Id != _watch.Share.Id)) StopWatching();
    }

    /// <summary>Starts watching a share, drawing on <paramref name="surface"/>. Throws <see cref="InvalidOperationException"/> if it cannot be watched.</summary>
    internal ScreenWatch StartWatching(Chat chat, ShareItem share, Surface surface)
    {
        if (_node == null || chat.Room is not { } room) throw new InvalidOperationException("Yaparr is still starting.");
        StopWatching();
        var watch = new ScreenWatch(_node, room, share, surface);
        _watch = watch;
        _watchChat = chat;
        return watch;
    }

    public void StopWatching()
    {
        var watch = _watch;
        _watch = null;
        _watchChat = null;
        if (watch == null) return;
        try { watch.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Android.Util.Log.Warn("Yaparr", "stopping the screen: " + ex.Message); }
        WatchEnded?.Invoke();
    }
}
