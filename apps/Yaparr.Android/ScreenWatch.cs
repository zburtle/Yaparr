using Android.Views;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Screen;
using Yaparr.Voice;

namespace Yaparr.AndroidApp;

/// <summary>One screen someone shares, as the voice panel lists it.</summary>
public sealed record ShareItem(string Id, string Name, bool IsMine);

/// <summary>
/// Watching a screen someone shares: the picture (the stream through the room's tree, decoded by <see cref="ScreenDecoder"/> onto a surface) and, if the
/// sharer shares it, the sound that goes with it, played on the phone's speakers. Dispose it to stop.
/// </summary>
internal sealed class ScreenWatch : IDisposable
{
    private readonly MediaView _picture;
    private MediaView? _soundView;
    private StreamAudioReceiver? _sound;
    private IDisposable? _speaker;
    private readonly ScreenDecoder _decoder;

    public ShareItem Share { get; }

    /// <summary>The picture is this size now (the first picture, or the sharer turned the phone). On another thread.</summary>
    public event Action<int, int>? PictureSize { add => _decoder.PictureSize += value; remove => _decoder.PictureSize -= value; }

    /// <summary>The picture could not be shown. On another thread.</summary>
    public event Action<string>? Failed { add => _decoder.Failed += value; remove => _decoder.Failed -= value; }

    public ScreenWatch(ChatNode node, ChatRoom room, ShareItem share, Surface surface)
    {
        Share = share;
        _picture = node.WatchMedia(room, share.Id);
        _decoder = new ScreenDecoder(_picture, surface);

        // The sound, if the sharer shares it, is a stream of its own that names this share; the picture is shown whether or not it can be heard.
        if (room.MediaGroups.FirstOrDefault(g => g.Kind == ScreenSharer.SoundKind && g.Name == ScreenSharer.SoundNameFor(share.Id)) is { } soundGroup)
        {
            try
            {
                _soundView = node.WatchMedia(room, soundGroup.Id);
                _sound = new StreamAudioReceiver(_soundView);
                _speaker = VoiceAudio.PlayStream(_sound, null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Android.Util.Log.Warn("Yaparr", "could not play the sound of the screen: " + ex.Message);
                StopSound();
            }
        }
    }

    public bool HasSound => _sound != null;

    /// <summary>Pictures decoded and drawn, and frames dropped, for the diagnostics.</summary>
    public (long Shown, long Dropped) Stats => (_decoder.Shown, _decoder.Dropped);

    private void StopSound()
    {
        try { _speaker?.Dispose(); } catch (Exception) { }
        _speaker = null;
        _sound?.Dispose();
        _sound = null;
        _soundView?.Dispose();
        _soundView = null;
    }

    public void Dispose()
    {
        _decoder.Dispose();
        StopSound();
        _picture.Dispose();
    }
}
