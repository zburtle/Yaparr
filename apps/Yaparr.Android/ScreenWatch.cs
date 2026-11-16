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
/// <para>
/// Like the computer app it judges its own picture (<see cref="ViewerQualityMeter"/>) and tells the sharer, and it moves between the share's main stream and its
/// lower stream on its own (<see cref="StreamChoice"/>, issue #254): the lower stream is watched beside the main one, and the decoder moves to it at its first key
/// frame, so the picture does not stop. Nothing is asked of the person; <see cref="StreamChanged"/> says when and why, for the notice.
/// </para>
/// </summary>
internal sealed class ScreenWatch : IDisposable
{
    private readonly ChatNode _node;
    private readonly ChatRoom _room;
    private readonly object _gate = new();
    private MediaView _picture;
    private MediaView? _soundView;
    private StreamAudioReceiver? _sound;
    private IDisposable? _speaker;
    private readonly ScreenDecoder _decoder;
    private bool _disposed;

    // The lower stream (issue #254).
    private readonly StreamChoice _choice = new();
    private readonly LoneStrugglerWatch _lone = new();
    private ViewerQualityMeter _quality = new();
    private (WatchedStream To, MediaView View, long StartedAt)? _switching;

    /// <summary>How long the first key frame of the other stream is waited for before the move is given up.</summary>
    private const long SwitchTimeoutMs = 10_000;

    public ShareItem Share { get; }

    /// <summary>Which of the share's streams is shown.</summary>
    public WatchedStream Watched { get; private set; } = WatchedStream.Main;

    /// <summary>The picture moved to the other stream: which one now, and why ("your connection lost pictures for 10 seconds"). On another thread.</summary>
    public event Action<WatchedStream, string>? StreamChanged;

    /// <summary>The picture is this size now (the first picture, or the sharer turned the phone). On another thread.</summary>
    public event Action<int, int>? PictureSize { add => _decoder.PictureSize += value; remove => _decoder.PictureSize -= value; }

    /// <summary>The picture could not be shown. On another thread.</summary>
    public event Action<string>? Failed { add => _decoder.Failed += value; remove => _decoder.Failed -= value; }

    public ScreenWatch(ChatNode node, ChatRoom room, ShareItem share, Surface surface)
    {
        _node = node;
        _room = room;
        Share = share;
        _picture = node.WatchMedia(room, share.Id);
        // How this picture is doing goes up the tree with the others' reports, so that the sharer can tell a room that struggles from one viewer that does.
        _picture.QualityProvider = () => _quality.Sample;
        _decoder = new ScreenDecoder(_picture, surface);
        _decoder.Switched += OnSwitched;

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

    /// <summary>Whether the picture's stream is still joined to the sharer or whoever passes it on (false when that connection was lost and is being looked for again).</summary>
    public bool Attached { get { lock (_gate) return _picture.Status.Attached; } }

    /// <summary>Pictures decoded and drawn, and frames dropped, for the diagnostics.</summary>
    public (long Shown, long Dropped) Stats => (_decoder.Shown, _decoder.Dropped);

    /// <summary>How this viewer judges its own picture.</summary>
    public ViewerQualitySample Quality => _quality.Sample;

    private MediaGroupInfo? LowGroup() => _room.MediaGroups.FirstOrDefault(g => g.Kind == LowStream.Kind && g.Name == LowStream.NameFor(Share.Id));

    /// <summary>Call once a second: the picture is judged, and the stream to watch chosen (a move is begun here and made at the other stream's first key frame).</summary>
    public void Tick(long nowMs)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var st = _picture.Status;
            _quality.Update(nowMs, new ViewerCounters(_decoder.Shown, _picture.FramesLost, _picture.FramesMissed, 0, _decoder.Dropped, st.GaveUp, st.PacketsSeen));
            if (_switching is { } going)
            {
                if (nowMs - going.StartedAt < SwitchTimeoutMs) return;
                _decoder.CancelSwitch();
                if (_decoder.Current == going.View) { Adopt(going.View); return; }   // it came just now: the move is made after all
                _switching = null;   // its first key frame never came: given up, the stream shown stays
                going.View.Dispose();
            }
            bool alone = Watched == WatchedStream.Main && st.Attached && _lone.Update(nowMs, _quality.Sample, _picture.RoomState);
            var wanted = _choice.Update(nowMs, LowGroup() != null, Watched, _quality.Sample, alone);
            if (wanted == Watched) return;
            string? id = wanted == WatchedStream.Low ? LowGroup()?.Id : Share.Id;
            if (id == null) return;
            MediaView view;
            try { view = _node.WatchMedia(_room, id); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return; }
            _switching = (wanted, view, nowMs);
            _decoder.SwitchTo(view);
        }
    }

    // The decoder took the other stream's first key frame. That is on the network's thread, inside the new stream's delivery and holding its lock, so the rest is
    // done on another: this thread must never wait for _gate (Tick holds it while it subscribes to and unsubscribes from the streams).
    private void OnSwitched(IMediaFrameSource source) => Task.Run(() =>
    {
        lock (_gate) if (!_disposed) Adopt(source);
    });

    // Under _gate: the stream being moved to is the one shown from now on, and the old one is let go.
    private void Adopt(IMediaFrameSource source)
    {
        if (_switching is not { } going || going.View != source) return;
        _switching = null;
        var old = _picture;
        _picture = going.View;
        Watched = going.To;
        _quality = new ViewerQualityMeter();   // the new stream is judged afresh
        old.QualityProvider = null;
        _picture.QualityProvider = () => _quality.Sample;
        try { old.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        string reason = _choice.Reason ?? "";
        Android.Util.Log.Info("Yaparr", Watched == WatchedStream.Low ? $"the screen {Share.Name} moved to its lower stream: {reason}" : $"the screen {Share.Name} moved back to its full stream: {reason}");
        StreamChanged?.Invoke(Watched, reason);
    }

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
        lock (_gate) _disposed = true;
        _decoder.Dispose();
        StopSound();
        MediaView picture;
        MediaView? going;
        lock (_gate) { picture = _picture; going = _switching?.View; _switching = null; }
        picture.Dispose();
        going?.Dispose();
    }
}
