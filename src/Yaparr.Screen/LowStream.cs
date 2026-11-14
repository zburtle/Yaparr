using System.Diagnostics;
using Yaparr.Chat;

namespace Yaparr.Screen;

/// <summary>
/// The second, lower stream of a share (stage 7 of #225, issue #254): a smaller picture at a lower bitrate, sent as a stream of its own with its own tree, for
/// the viewers whose link cannot carry the main one. They move to it, and back, on their own, without touching anyone else; the main stream's adaptive quality
/// counts only the viewers of the main stream. Nobody picks it and nobody is told about it but the streamer (an indicator in the sharing panel) and the viewer
/// that is moved to it (a notice saying why).
/// It exists only while it is needed: the stream (<see cref="Kind"/>, named after the share, <see cref="NameFor"/>; no list shows that kind, and no sound is made
/// for it) is started when a viewer of the main stream reports trouble (<see cref="Tick"/>), and taken away <see cref="Unneeded"/> after the last viewer left it
/// with nobody in trouble. It is encoded only while someone watches it: the encoder starts when it has a viewer and stops <see cref="Linger"/> after the last one
/// leaves, so a room where everyone keeps up costs nothing more. Its pictures are the share's own, scaled down (<see cref="PictureScaler"/>; a picture kept on the graphics card is copied back first,
/// only while the lower stream is watched), encoded with the same card as the main stream (H.264) or with VP8 when the share is VP8 or the card refuses.
/// The share's loop hands it each picture (<see cref="Offer(CapturedPicture)"/>) and it keeps its own clock (at most 30 a second) and its own sender.
/// </summary>
public sealed class LowStream : IDisposable
{
    /// <summary>The kind of the stream; its name says which share it belongs to (see <see cref="NameFor"/>).</summary>
    public const string Kind = "screen-low";

    public static string NameFor(string shareId) => "low:" + shareId;

    /// <summary>How long the encoder keeps going after the last viewer of the lower stream left (a viewer that comes straight back gets pictures at once).</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(10);

    /// <summary>How long the stream stays after its last viewer left, with nobody in trouble, before it is taken away.</summary>
    public static readonly TimeSpan Unneeded = TimeSpan.FromSeconds(60);

    /// <summary>Whether shares offer a lower stream: unless YAPARR_LOW_STREAM is 0 or off.</summary>
    public static bool Enabled => !(Environment.GetEnvironmentVariable("YAPARR_LOW_STREAM")?.Trim().ToLowerInvariant() is "0" or "off" or "no" or "false");

    /// <summary>
    /// The lower stream's level for a share at <paramref name="main"/>: at most 1280x720 and never more than about two thirds of each side of the main picture's box,
    /// at most 30 pictures a second (never more than the main stream), and a third of the main bitrate (between 300 and 1500 kbit/s, never more than the main).
    /// </summary>
    public static (int Kbps, int Fps, int MaxWidth, int MaxHeight) LevelFor(ScreenOptions main)
    {
        int maxWidth = Math.Max(160, Math.Min(1280, main.MaxWidth * 2 / 3)) & ~1;
        int maxHeight = Math.Max(90, Math.Min(720, main.MaxHeight * 2 / 3)) & ~1;
        int fps = Math.Clamp(Math.Min(30, main.Fps), 1, 30);
        int kbps = Math.Min(main.Kbps, Math.Clamp(main.Kbps / 3, 300, 1500));
        return (Math.Max(50, kbps), fps, maxWidth, maxHeight);
    }

    private readonly ChatNode _node;
    private readonly ChatRoom _room;
    private readonly MediaSource _main;
    private volatile MediaSource? _source;
    private long _neededAt;
    private readonly Func<ScreenOptions> _mainOptions;
    private readonly Func<(ScreenCodec Codec, HardwareEncoder? Card)> _mainEncoder;
    private readonly CancellationTokenSource _cts = new();
    private readonly ShareStats _stats = new();
    private readonly FrameSender _sender;
    private readonly object _gate = new();
    private PictureScaler? _scaler;
    private InProcessEncoder? _encoder;
    private ScreenCodec _codec;
    private HardwareEncoder? _card;
    private (int Kbps, int Fps, int MaxWidth, int MaxHeight) _level;
    private long _lastEncodedAt, _lastViewerAt, _lastKeyAt;
    private volatile bool _keyAsked;
    private bool _started;   // a key frame has gone out since the encoder started: nothing is sent before one
    private long _frames, _bytes, _keys;
    private string? _problem;
    private bool _vp8Only;
    private bool _disposed;

    public LowStream(ChatNode node, ChatRoom room, MediaSource main, Func<ScreenOptions> mainOptions, Func<(ScreenCodec Codec, HardwareEncoder? Card)> mainEncoder)
    {
        _node = node;
        _room = room;
        _main = main;
        _mainOptions = mainOptions;
        _mainEncoder = mainEncoder;
        _level = LevelFor(mainOptions());
        _sender = new FrameSender(DeliverAsync, _stats, () => _keyAsked = true, () => TimeSpan.FromSeconds(1.0 / Math.Max(1, _level.Fps)).Ticks);
        _sender.Start(_cts.Token);
    }

    /// <summary>The stream itself (its id, its viewers), or null while it is not needed.</summary>
    public MediaSource? Source => _source;

    /// <summary>Whether the stream exists now (a viewer of the main stream was in trouble, or someone still watches it).</summary>
    public bool Started => _source != null;

    /// <summary>How many watch the lower stream now.</summary>
    public int Viewers => _source?.Viewers.Count ?? 0;

    /// <summary>The stream was started (true) or taken away (false). Not a notification for anyone but the streamer's own panel and log.</summary>
    public event Action<bool>? StartedChanged;

    /// <summary>
    /// Once a second, from the share's quality watch: <paramref name="troubled"/> is how many viewers of the main stream said they are in trouble. The stream is
    /// started when one is, and taken away when nobody is in trouble and nobody has watched it for <see cref="Unneeded"/>.
    /// </summary>
    public void Tick(long nowMs, int troubled)
    {
        if (_disposed) return;
        if (troubled > 0 || Viewers > 0) Interlocked.Exchange(ref _neededAt, nowMs);
        if (_source == null)
        {
            if (troubled <= 0) return;
            var level = LevelFor(_mainOptions());
            MediaSource source;
            try { source = _node.StartMedia(_room, Kind, NameFor(_main.Info.Id), signed: true, upstream: false, kbps: level.Kbps, channel: _main.Info.Channel); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { _problem = ex.Message; return; }
            source.KeyFrameRequested += () => _keyAsked = true;
            _source = source;
            StartedChanged?.Invoke(true);
            return;
        }
        if (nowMs - Interlocked.Read(ref _neededAt) < Unneeded.TotalMilliseconds) return;
        var gone = _source;
        _source = null;
        lock (_gate) StopEncoder();
        gone?.Dispose();
        StartedChanged?.Invoke(false);
    }

    /// <summary>Whether it is being encoded now (someone watches it, or did a moment ago).</summary>
    public bool Encoding { get { lock (_gate) return _encoder != null; } }

    public long Frames => Interlocked.Read(ref _frames);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long KeyFrames => Interlocked.Read(ref _keys);

    /// <summary>What it has counted about sending (see <see cref="ShareStats"/>).</summary>
    public ShareFigures Figures() => _stats.Figures(new KeyFramePacer(), 0);

    /// <summary>One line for the share's report.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            string state = _source == null ? "not started (no viewer has needed it)"
                : _encoder is { } e ? $"encoding {e.Describe().Split(';')[0]}"
                : _problem != null ? "not encoded: " + _problem : "started, not encoded (nobody watches it yet)";
            return $"{Viewers} watching; {state}; sent {Frames} frames ({KeyFrames} key frames), {Bytes / 1024} KB"
                   + (Figures().Sends > 0 ? "; " + Figures().DescribeSending(1000.0 / Math.Max(1, _level.Fps)) : "");
        }
    }

    /// <summary>A picture of the share, as the share's loop encodes it (in memory or on the card). Called on the share's loop; returns quickly when nobody watches.</summary>
    public void Offer(CapturedPicture picture) => Offer(scaler => scaler.Scale(picture, _level.MaxWidth, _level.MaxHeight));

    /// <summary>A picture of the share as NV12 bytes (a Wayland share's).</summary>
    public void Offer(byte[] nv12, int width, int height) => Offer(scaler => scaler.Scale(nv12, width, height, _level.MaxWidth, _level.MaxHeight));

    private void Offer(Action<PictureScaler> scale)
    {
        if (_disposed || _source == null || !Due()) return;
        lock (_gate)
        {
            try
            {
                _scaler ??= new PictureScaler();
                scale(_scaler);
                var encoder = EnsureEncoder(_scaler.Width, _scaler.Height);
                if (encoder == null) return;
                long now = Environment.TickCount64;
                bool force = !_started || (_keyAsked && now - _lastKeyAt >= 500);
                if (force) { _keyAsked = false; _lastKeyAt = now; }
                encoder.Encode(_scaler.Output, force, (frame, key) =>
                {
                    bool isKey = key && ScreenCodecs.IsKeyFrame(_codec, frame);
                    if (!_started && !isKey) return;
                    _started = true;
                    _sender.Enqueue(frame, isKey);
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException)
            {
                // The card refused the second encoder (or the picture could not be made): VP8 next time, and said in the report.
                _problem = ex.Message;
                StopEncoder();
                if (_codec != ScreenCodec.Vp8) _vp8Only = true;
            }
        }
    }

    // Whether this picture is one to encode: someone watches (or did within the linger), and the lower stream's own clock has come round.
    private bool Due()
    {
        long now = Stopwatch.GetTimestamp();
        if (Viewers > 0) Interlocked.Exchange(ref _lastViewerAt, now);
        long lastViewer = Interlocked.Read(ref _lastViewerAt);
        if (lastViewer == 0 || now - lastViewer > Linger.TotalSeconds * Stopwatch.Frequency)
        {
            lock (_gate) StopEncoder();
            return false;
        }
        long interval = (long)(Stopwatch.Frequency / (double)Math.Max(1, _level.Fps) * 0.9);
        if (now - Interlocked.Read(ref _lastEncodedAt) < interval) return false;
        Interlocked.Exchange(ref _lastEncodedAt, now);
        return true;
    }

    // Under the gate: the encoder for this size and level, made again when the main stream's level, codec or the picture's size changed.
    private InProcessEncoder? EnsureEncoder(int width, int height)
    {
        var level = LevelFor(_mainOptions());
        var (mainCodec, mainCard) = _mainEncoder();
        var codec = _vp8Only || mainCodec == ScreenCodec.Vp8 || mainCard == null ? ScreenCodec.Vp8 : ScreenCodec.H264;
        if (_encoder is { } current && current.Width == width && current.Height == height && level == _level && codec == _codec && mainCard == _card) return current;
        StopEncoder();
        _level = level;
        _codec = codec;
        _card = codec == ScreenCodec.Vp8 ? null : mainCard;
        if (width < 16 || height < 16) return null;
        var options = new ScreenOptions { Fps = level.Fps, Kbps = level.Kbps, MaxWidth = level.MaxWidth, MaxHeight = level.MaxHeight, KeyFrameSeconds = _mainOptions().KeyFrameSeconds, X11Display = null };
        _encoder = new InProcessEncoder(width, height, options, null, codec, _card);
        if (_source is { } source) source.EffectiveKbps = level.Kbps;
        _started = false;
        _problem = null;
        return _encoder;
    }

    private void StopEncoder()
    {
        _encoder?.Dispose();
        _encoder = null;
        _started = false;
    }

    private async Task DeliverAsync(byte[] frame, bool isKey, CancellationToken ct)
    {
        if (_source is not { } source) return;   // taken away while the frame waited
        await source.SendFrameAsync(frame, isKey, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _bytes, frame.Length);
        if (isKey) Interlocked.Increment(ref _keys);
        _stats.Frame(frame.Length, isKey);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _sender.Complete();
        _sender.Wait(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            StopEncoder();
            _scaler?.Dispose();
            _scaler = null;
        }
        _source?.Dispose();
        _source = null;
        _cts.Dispose();
    }
}
