using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Voice;

namespace Yaparr.Screen;

/// <summary>What is being played: the server's id for it, a title to show, and how long it is.</summary>
public sealed record FilmTarget(string Id, string Title, TimeSpan Duration);

/// <summary>Starts the transcode of a film at a time and hands over its bytes, and what stops it on the server when it is let go.</summary>
public delegate Task<(Stream Body, IAsyncDisposable Owner)> FilmOpener(FilmTarget target, TimeSpan start, CancellationToken ct);

/// <summary>Says what comes after a film (the next episode), or null when nothing does.</summary>
public delegate Task<FilmTarget?> FilmNext(FilmTarget current, CancellationToken ct);

/// <summary>
/// Plays a film or an episode from the person's own Jellyfin server into a room, as a screen share that everyone in the room watches in the usual way (the same stream tree,
/// the same Watch button). The server does the encoding: it is asked for a transport stream of H.264 (no B-frames) and AAC, which is taken apart here (<see cref="TsDemuxer"/>),
/// the pictures handed on as they are and the sound decoded and sent again as Opus like the sound of any share (<see cref="StreamAudioSender"/>). Nothing is captured and nothing
/// is encoded on this computer except the sound.
///
/// The server sends far faster than the film plays (about two hundred times at first), so the pictures and the sound are let go at the time stamps they carry, from one clock; while
/// that waits, the connection to the server backs up and the server holds back too. Pausing stops the clock. A jump (<see cref="Seek"/>) and the next episode (<see cref="Next"/>,
/// or by itself when an episode ends and <see cref="Autoplay"/> is on) start a new transcode on the same stream, so the room keeps watching the same share: viewers get a key frame
/// at once, since the new stream starts on one. What is playing, how far it is and whether it is paused goes to the viewers as a stream of its own (<see cref="FilmStatus"/>).
/// A viewer who joins late, or after a loss, has to wait for a key frame, which the server makes about every ten seconds; this cannot ask for one sooner (see <see cref="KeyFrameRequests"/>).
/// </summary>
public sealed class JellyfinSharer : IDisposable
{
    /// <summary>How long what is sent is held back behind the time stamps, so that the first seconds go out smoothly and a short stall of the server does not show.</summary>
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(500);

    private const long NoPts = long.MinValue;

    private sealed record Request(bool Next, TimeSpan Start);

    private readonly MediaSource _source;
    private readonly MediaSource? _soundSource;
    private readonly MediaSource? _statusSource;
    private readonly StreamAudioSender? _sound;
    private readonly AacDecoder _aac;
    private readonly FrameSender _sender;
    private readonly H264Reader _parameterSets = new(Stream.Null);
    private readonly FilmOpener _open;
    private readonly FilmNext? _next;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<float> _samples = new();
    private readonly object _segmentGate = new();
    private readonly Timer? _statusTimer;
    private CancellationTokenSource? _segment;
    private Request? _request;
    private FilmTarget _target;
    private TimeSpan _segmentStart;
    private Task? _run;
    private long _basePts = NoPts;
    private long _origin;
    private long _lastPts;
    private bool _seenKey;
    private volatile TaskCompletionSource? _pause;
    private int _disposed;
    private long _pictures, _soundFrames;
    private int _keyAsked;

    private JellyfinSharer(MediaSource source, MediaSource? soundSource, MediaSource? statusSource, FilmTarget first, TimeSpan start, FilmOpener open, FilmNext? next)
    {
        _source = source;
        _soundSource = soundSource;
        _statusSource = statusSource;
        _sound = soundSource == null ? null : new StreamAudioSender(soundSource);
        _target = first;
        _segmentStart = start;
        _open = open;
        _next = next;
        _aac = new AacDecoder();
        _sender = new FrameSender((frame, key, ct) => _source.SendFrameAsync(frame, key, ct), new ShareStats(), () => Interlocked.Increment(ref _keyAsked), () => TimeSpan.TicksPerSecond / 24);
        _source.KeyFrameRequested += OnKeyRequested;
        if (_statusSource != null) _statusTimer = new Timer(_ => PublishStatus(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    /// <summary>The stream in the room (its id is what a viewer asks for).</summary>
    public MediaSource Source => _source;

    /// <summary>What is playing now (it changes when the next episode starts).</summary>
    public FilmTarget Target { get { lock (_segmentGate) return _target; } }

    /// <summary>Another film started (the next episode). Raised on another thread.</summary>
    public event Action<FilmTarget>? TargetChanged;

    /// <summary>The film ended, or stopped for a reason (the server went away, the sign-in ran out). Null: it ended by itself. Raised on another thread.</summary>
    public event Action<Exception?>? Ended;

    /// <summary>Whether the next episode starts by itself when one ends (on unless turned off).</summary>
    public bool Autoplay { get; set; } = true;

    public bool Paused => _pause != null;

    /// <summary>How far into the film it is, by the time stamps of what was sent last.</summary>
    public TimeSpan Position
    {
        get
        {
            long b = Volatile.Read(ref _basePts);
            TimeSpan start;
            lock (_segmentGate) start = _segmentStart;
            return b == NoPts ? start : start + TimeSpan.FromSeconds(Math.Max(0, (Volatile.Read(ref _lastPts) - b) / 90000.0));
        }
    }

    public long Pictures => Interlocked.Read(ref _pictures);
    public long SoundFrames => Interlocked.Read(ref _soundFrames);

    /// <summary>How often a viewer asked for a key frame, which a film cannot make on request (it has them about every ten seconds).</summary>
    public int KeyFrameRequests => Volatile.Read(ref _keyAsked);

    private void OnKeyRequested() => Interlocked.Increment(ref _keyAsked);

    /// <summary>
    /// Announces the film in the room (as a screen share, to the voice channel this device is in or, when it is not in one, the channel another of your devices is in) and starts
    /// playing it at <paramref name="start"/>. <paramref name="open"/> starts the transcode; <paramref name="next"/>, when given, says what follows an episode.
    /// </summary>
    public static JellyfinSharer Start(ChatNode node, ChatRoom room, string shareName, int videoKbps, FilmTarget first, TimeSpan start, FilmOpener open, FilmNext? next = null)
    {
        if (!AacDecoder.IsAvailable) throw new PlatformNotSupportedException("Playing a film needs FFmpeg's libraries with the AAC decoder (Yaparr.FFmpeg ffmpeg-11 or newer). " + (FfmpegLibraries.TryLoad(out var why) ? "" : why));
        ProcessPower.KeepResponsive();
        var source = node.StartMedia(room, "screen", shareName, signed: true, upstream: false, kbps: videoKbps, channel: room.ScreenShareChannel);
        MediaSource? sound = null, status = null;
        JellyfinSharer? sharer = null;
        try
        {
            sound = node.StartMedia(room, ScreenSharer.SoundKind, ScreenSharer.SoundNameFor(source.Info.Id), signed: true, upstream: false, kbps: 96, channel: source.Info.Channel);
            status = node.StartMedia(room, FilmStatus.Kind, FilmStatus.NameFor(source.Info.Id), signed: true, upstream: false, kbps: 8, channel: source.Info.Channel);
            sharer = new JellyfinSharer(source, sound, status, first, start, open, next);
        }
        catch
        {
            status?.Dispose();
            sound?.Dispose();
            source.Dispose();
            throw;
        }
        sharer._sender.Start(sharer._cts.Token);
        sharer._run = Task.Run(() => sharer.PumpAsync(sharer._cts.Token));
        return sharer;
    }

    public void Pause()
    {
        _pause ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PublishStatus();
    }

    public void Resume()
    {
        var gate = Interlocked.Exchange(ref _pause, null);
        gate?.TrySetResult();
        PublishStatus();
    }

    /// <summary>Jumps to a time in what is playing: a new transcode is started there, and the old one stopped.</summary>
    public void Seek(TimeSpan position)
    {
        var limit = Target.Duration;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (limit > TimeSpan.Zero && position > limit - TimeSpan.FromSeconds(5)) position = limit > TimeSpan.FromSeconds(10) ? limit - TimeSpan.FromSeconds(5) : TimeSpan.Zero;
        Ask(new Request(false, position));
    }

    /// <summary>Goes on to what follows (the next episode); when nothing does, the film ends.</summary>
    public void Next() => Ask(new Request(true, TimeSpan.Zero));

    private void Ask(Request request)
    {
        lock (_segmentGate)
        {
            _request = request;
            try { _segment?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            var target = Target;
            TimeSpan start;
            lock (_segmentGate) start = _segmentStart;
            while (true)
            {
                using var segment = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_segmentGate)
                {
                    _segment = segment;
                    _segmentStart = start;
                    _target = target;
                    _basePts = NoPts;
                }
                _seenKey = false;
                PublishStatus();

                bool ended = false;
                try { ended = await PlaySegmentAsync(target, start, segment.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }   // a jump or a switch was asked for

                Request? request;
                lock (_segmentGate) { request = _request; _request = null; _segment = null; }
                if (request != null)
                {
                    if (!request.Next) { start = request.Start; continue; }
                    var following = _next == null ? null : await _next(target, ct).ConfigureAwait(false);
                    if (following == null) break;
                    target = following;
                    start = TimeSpan.Zero;
                    TargetChanged?.Invoke(target);
                    continue;
                }
                if (!ended) break;
                // The film ended by itself: on to the next episode, if there is one and it is wanted.
                if (Autoplay && _next != null && await _next(target, ct).ConfigureAwait(false) is { } after)
                {
                    target = after;
                    start = TimeSpan.Zero;
                    TargetChanged?.Invoke(target);
                    continue;
                }
                break;
            }
            // What is queued goes out before the film is said to be over.
            _sender.Complete();
            await Task.Run(() => _sender.Wait(TimeSpan.FromSeconds(10)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) { error = ex; }
        Ended?.Invoke(error);
    }

    // Plays one transcode to its end; false when it was cut short.
    private async Task<bool> PlaySegmentAsync(FilmTarget target, TimeSpan start, CancellationToken ct)
    {
        var (body, owner) = await _open(target, start, ct).ConfigureAwait(false);
        await using (owner.ConfigureAwait(false))
        {
            var queue = new Queue<TsUnit>();
            var demuxer = new TsDemuxer(queue.Enqueue);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                int n = await body.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break;
                demuxer.Feed(buffer.AsSpan(0, n));
                while (queue.TryDequeue(out var unit)) await HandleAsync(unit, ct).ConfigureAwait(false);
            }
            demuxer.Finish();
            while (queue.TryDequeue(out var unit)) await HandleAsync(unit, ct).ConfigureAwait(false);
            return true;
        }
    }

    private async Task HandleAsync(TsUnit unit, CancellationToken ct)
    {
        if (_pause is { } gate)
        {
            long paused = Stopwatch.GetTimestamp();
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            _origin += Stopwatch.GetTimestamp() - paused;   // the film carries on from where it stopped
        }
        if (Volatile.Read(ref _basePts) == NoPts)
        {
            Volatile.Write(ref _lastPts, unit.Pts);
            _origin = Stopwatch.GetTimestamp() + (long)(Lead.TotalSeconds * Stopwatch.Frequency);
            Volatile.Write(ref _basePts, unit.Pts);
        }
        long due = _origin + (long)((unit.Pts - Volatile.Read(ref _basePts)) / 90000.0 * Stopwatch.Frequency);
        double waitMs = (due - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
        if (waitMs > 1) await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
        if (unit.Pts > Volatile.Read(ref _lastPts)) Volatile.Write(ref _lastPts, unit.Pts);

        if (unit.IsVideo)
        {
            var frame = _parameterSets.WithParameterSets(unit.Data);
            bool key = H264.IsKeyFrame(frame);
            if (!_seenKey)
            {
                if (!key) return;   // a viewer can only start at a key frame
                _seenKey = true;
            }
            _sender.Enqueue(frame, key);
            Interlocked.Increment(ref _pictures);
        }
        else if (_sound != null && _aac.Decode(unit.Data, _samples) > 0)
        {
            _sound.Push(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_samples));
            Interlocked.Increment(ref _soundFrames);
        }
    }

    // Sent every second and whenever something changes; one lost on the way is made good by the next.
    private void PublishStatus()
    {
        if (_statusSource == null || Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var target = Target;
            _statusSource.Send(new FilmStatus(target.Title, Position, target.Duration, Paused).Encode());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _statusTimer?.Dispose();
        _source.KeyFrameRequested -= OnKeyRequested;
        Resume();
        _cts.Cancel();
        try { _run?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _sender.Complete();
        _sound?.Dispose();
        _statusSource?.Dispose();
        _soundSource?.Dispose();
        _source.Dispose();
        _aac.Dispose();
        _cts.Dispose();
    }
}
