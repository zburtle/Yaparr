using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Voice;

namespace Yaparr.Screen;

/// <summary>
/// Plays a film or an episode from the person's own Jellyfin server into a room, as a screen share that everyone in the room watches in the usual way (the same stream tree,
/// the same Watch button). The server does the encoding: it is asked for a transport stream of H.264 (no B-frames) and AAC, which is taken apart here (<see cref="TsDemuxer"/>),
/// the pictures handed on as they are and the sound decoded and sent again as Opus like the sound of any share (<see cref="StreamAudioSender"/>). Nothing is captured and nothing
/// is encoded on this computer except the sound.
///
/// The server sends far faster than the film plays (about two hundred times at first), so the pictures and the sound are let go at the time stamps they carry, from one clock; while
/// that waits, the connection to the server backs up and the server holds back too. Pausing stops the clock. A viewer who joins late, or after a loss, has to wait for a key frame,
/// which the server makes about every ten seconds; this cannot ask for one sooner (see <see cref="KeyFrameRequests"/>).
/// </summary>
public sealed class JellyfinSharer : IDisposable
{
    /// <summary>How long what is sent is held back behind the time stamps, so that the first seconds go out smoothly and a short stall of the server does not show.</summary>
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(500);

    private readonly MediaSource _source;
    private readonly MediaSource? _soundSource;
    private readonly StreamAudioSender? _sound;
    private readonly AacDecoder _aac;
    private readonly FrameSender _sender;
    private readonly H264Reader _parameterSets = new(Stream.Null);
    private readonly Func<CancellationToken, Task<(Stream Body, IAsyncDisposable Owner)>> _open;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<float> _samples = new();
    private Task? _run;
    private long? _basePts;
    private long _origin;
    private long _lastPts;
    private bool _seenKey;
    private volatile TaskCompletionSource? _pause;
    private int _disposed;

    private JellyfinSharer(MediaSource source, MediaSource? soundSource, Func<CancellationToken, Task<(Stream, IAsyncDisposable)>> open)
    {
        _source = source;
        _soundSource = soundSource;
        _sound = soundSource == null ? null : new StreamAudioSender(soundSource);
        _open = open;
        _aac = new AacDecoder();
        _sender = new FrameSender((frame, key, ct) => _source.SendFrameAsync(frame, key, ct), new ShareStats(), () => Interlocked.Increment(ref _keyAsked), () => TimeSpan.TicksPerSecond / 24);
        _source.KeyFrameRequested += OnKeyRequested;
    }

    /// <summary>The stream in the room (its id is what a viewer asks for).</summary>
    public MediaSource Source => _source;

    /// <summary>The film ended, or stopped for a reason (the server went away, the sign-in ran out). Null: it ended by itself. Raised on another thread.</summary>
    public event Action<Exception?>? Ended;

    public bool Paused => _pause != null;

    /// <summary>How far into what was asked for the film is, by the time stamps of what was sent last.</summary>
    public TimeSpan Position => _basePts is { } b ? TimeSpan.FromSeconds(Math.Max(0, (Volatile.Read(ref _lastPts) - b) / 90000.0)) : TimeSpan.Zero;

    public long Pictures => Interlocked.Read(ref _pictures);
    public long SoundFrames => Interlocked.Read(ref _soundFrames);

    /// <summary>How often a viewer asked for a key frame, which a film cannot make on request (it has them about every ten seconds).</summary>
    public int KeyFrameRequests => Volatile.Read(ref _keyAsked);

    private long _pictures, _soundFrames;
    private int _keyAsked;

    private void OnKeyRequested() => Interlocked.Increment(ref _keyAsked);

    /// <summary>
    /// Announces the film in the room (as a screen share, to the voice channel this device is in or, when it is not in one, the channel another of your devices is in) and starts
    /// playing it. <paramref name="open"/> starts the transcode: the transport stream, and what stops it on the server when the film ends or the share is stopped.
    /// </summary>
    public static JellyfinSharer Start(ChatNode node, ChatRoom room, string title, int videoKbps, Func<CancellationToken, Task<(Stream Body, IAsyncDisposable Owner)>> open)
    {
        if (!AacDecoder.IsAvailable) throw new PlatformNotSupportedException("Playing a film needs FFmpeg's libraries with the AAC decoder (Yaparr.FFmpeg ffmpeg-11 or newer). " + (FfmpegLibraries.TryLoad(out var why) ? "" : why));
        ProcessPower.KeepResponsive();
        var source = node.StartMedia(room, "screen", title, signed: true, upstream: false, kbps: videoKbps, channel: room.ScreenShareChannel);
        MediaSource? sound = null;
        JellyfinSharer? sharer = null;
        try
        {
            sound = node.StartMedia(room, ScreenSharer.SoundKind, ScreenSharer.SoundNameFor(source.Info.Id), signed: true, upstream: false, kbps: 96, channel: source.Info.Channel);
            sharer = new JellyfinSharer(source, sound, open);
        }
        catch
        {
            sound?.Dispose();
            source.Dispose();
            throw;
        }
        sharer._sender.Start(sharer._cts.Token);
        sharer._run = Task.Run(() => sharer.PumpAsync(sharer._cts.Token));
        return sharer;
    }

    public void Pause() => _pause ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Resume()
    {
        var gate = Interlocked.Exchange(ref _pause, null);
        gate?.TrySetResult();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            var (body, owner) = await _open(ct).ConfigureAwait(false);
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
                // What is queued goes out before the film is said to be over.
                _sender.Complete();
                await Task.Run(() => _sender.Wait(TimeSpan.FromSeconds(10)), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) { error = ex; }
        Ended?.Invoke(error);
    }

    private async Task HandleAsync(TsUnit unit, CancellationToken ct)
    {
        if (_pause is { } gate)
        {
            long paused = Stopwatch.GetTimestamp();
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            _origin += Stopwatch.GetTimestamp() - paused;   // the film carries on from where it stopped
        }
        if (_basePts is null)
        {
            Volatile.Write(ref _lastPts, unit.Pts);
            _origin = Stopwatch.GetTimestamp() + (long)(Lead.TotalSeconds * Stopwatch.Frequency);
            _basePts = unit.Pts;
        }
        long due = _origin + (long)((unit.Pts - _basePts.Value) / 90000.0 * Stopwatch.Frequency);
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _source.KeyFrameRequested -= OnKeyRequested;
        Resume();
        _cts.Cancel();
        try { _run?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _sender.Complete();
        _sound?.Dispose();
        _soundSource?.Dispose();
        _source.Dispose();
        _aac.Dispose();
        _cts.Dispose();
    }
}
