using System.Threading.Channels;
using Android.Media;
using Android.Views;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.AndroidApp;

/// <summary>
/// Shows the picture of a screen someone shares: the frames that arrive (H.264, AV1 when the sharer chose it, or VP8 when the sharer's computer had no graphics card to encode on) go to
/// the phone's own video decoder, which draws straight onto a surface. The first key frame says which codec it is and how big; a key frame of another size
/// starts the decoder again. Nothing is decoded with FFmpeg on the phone.
/// </summary>
internal sealed class ScreenDecoder : IDisposable
{
    private const string H264Mime = "video/avc", Vp8Mime = "video/x-vnd.on2.vp8", Av1Mime = "video/av01", H265Mime = "video/hevc";

    // The stream the frames come from, and the one being moved to (the share's lower stream or back, issue #254): frames of the next one are let through from
    // its first key frame on, and from then on the old one's are not, so the picture goes on without a gap (the decoder starts again only if the size changes).
    private readonly object _sourceGate = new();
    private IMediaFrameSource _frames;
    private Action<MediaFrame> _onFrames;
    private IMediaFrameSource? _next;
    private Action<MediaFrame>? _onNext;
    private readonly Surface _surface;
    private readonly Channel<(byte[] Data, bool IsKey)> _queue = Channel.CreateBounded<(byte[], bool)>(new BoundedChannelOptions(60) { SingleReader = true, SingleWriter = false });
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();

    private MediaCodec? _codec;
    private ScreenCodec _kind;
    private int _width, _height;
    private volatile bool _waitForKey = true;
    private long _fed, _shown, _dropped;
    private int _disposed;

    /// <summary>The picture is this size (raised when the first picture is drawn, and when the sharer changes size). On another thread.</summary>
    public event Action<int, int>? PictureSize;

    /// <summary>The decoder could not be started or stopped. The picture is gone; the reason is for the person. On another thread.</summary>
    public event Action<string>? Failed;

    public ScreenDecoder(IMediaFrameSource frames, Surface surface)
    {
        _frames = frames;
        _surface = surface;
        _thread = new Thread(Run) { IsBackground = true, Name = "screen decoder" };
        _thread.Start();
        _onFrames = frame => OnFrame(frames, frame);
        _frames.FrameReceived += _onFrames;
    }

    public long Fed => Interlocked.Read(ref _fed);
    public long Shown => Interlocked.Read(ref _shown);
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The frames now come from the given source (raised at its first key frame, on the network's thread); the old source is no longer listened to.</summary>
    public event Action<IMediaFrameSource>? Switched;

    /// <summary>Moves to <paramref name="next"/> at its first key frame; until then the frames of the stream shown go on. A move not yet made is replaced.</summary>
    public void SwitchTo(IMediaFrameSource next)
    {
        CancelSwitch();
        Action<MediaFrame> handler = frame => OnFrame(next, frame);
        lock (_sourceGate) { _next = next; _onNext = handler; }
        next.FrameReceived += handler;
    }

    /// <summary>The stream the frames are taken from now.</summary>
    public IMediaFrameSource Current { get { lock (_sourceGate) return _frames; } }

    /// <summary>Gives up a move whose first key frame has not come.</summary>
    public void CancelSwitch()
    {
        IMediaFrameSource? next;
        Action<MediaFrame>? handler;
        lock (_sourceGate) { (next, handler) = (_next, _onNext); _next = null; _onNext = null; }
        if (next != null && handler != null) next.FrameReceived -= handler;
    }

    private void OnFrame(IMediaFrameSource source, MediaFrame frame)
    {
        IMediaFrameSource? old = null;
        Action<MediaFrame>? oldHandler = null;
        lock (_sourceGate)
        {
            if (source == _next)
            {
                if (!frame.IsKey) return;   // not yet: the stream shown goes on until the new one can be started from a key frame
                (old, oldHandler) = (_frames, _onFrames);
                _frames = _next;
                _onFrames = _onNext!;
                _next = null;
                _onNext = null;
            }
            else if (source != _frames) return;   // the old stream, after the move
        }
        if (old != null)
        {
            old.FrameReceived -= oldHandler;
            Switched?.Invoke(source);
        }
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_queue.Writer.TryWrite((frame.Data.ToArray(), frame.IsKey)))
        {
            // The decoder has fallen behind: what is queued is dropped, and nothing is shown until the next key frame.
            Interlocked.Increment(ref _dropped);
            _waitForKey = true;
        }
    }

    private bool _saidH265Off;

    private void Run()
    {
        var info = new MediaCodec.BufferInfo();
        try
        {
            var reader = _queue.Reader;
            while (!_cts.IsCancellationRequested)
            {
                if (!reader.TryRead(out var item))
                {
                    Drain(info);
                    if (!reader.WaitToReadAsync(_cts.Token).AsTask().Wait(20, _cts.Token)) continue;
                    continue;
                }
                var (data, isKey) = item;
                if (isKey && ScreenCodecs.TryGetKeyFrame(data, out var kind, out int width, out int height)
                    && (_codec == null || kind != _kind || width != _width || height != _height))
                {
                    Restart(kind, width, height);
                }
                if (_codec == null && isKey && !_saidH265Off && !ScreenFeatures.H265 && H265.LooksLikeKeyFrame(data))
                {
                    // H.265 arrives and this version has it switched off: said once, instead of a share that stays black without a word.
                    _saidH265Off = true;
                    Failed?.Invoke(ScreenFeatures.H265Off + " Ask the person sharing to pick H.264 or AV1.");
                }
                if (_codec == null) continue;                // nothing to start from yet
                if (_waitForKey && !isKey) continue;
                if (isKey) _waitForKey = false;
                Feed(data, isKey);
                Drain(info);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex)
        {
            Android.Util.Log.Error("Yaparr", "screen decoder stopped: " + ex);
            if (Volatile.Read(ref _disposed) == 0) Failed?.Invoke("The picture stopped: " + ex.Message);
        }
    }

    private void Restart(ScreenCodec kind, int width, int height)
    {
        CloseCodec();
        string mime = kind switch { ScreenCodec.H264 => H264Mime, ScreenCodec.Av1 => Av1Mime, ScreenCodec.H265 => H265Mime, _ => Vp8Mime };
        MediaCodec codec;
        try { codec = MediaCodec.CreateDecoderByType(mime)!; }
        catch (Exception ex) when (kind is ScreenCodec.Av1 or ScreenCodec.H265 && ex is Java.Lang.IllegalArgumentException or Java.IO.IOException or NullReferenceException)
        {
            throw new InvalidOperationException($"This phone cannot play {ScreenCodecs.Name(kind)}. Ask the person sharing to pick another format.", ex);
        }
        try
        {
            using var format = MediaFormat.CreateVideoFormat(mime, width, height);
            // Show what arrives as soon as it arrives; where a phone has no such setting this is ignored.
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) format.SetInteger(MediaFormat.KeyLowLatency, 1);
            codec.Configure(format, _surface, null, 0);
            codec.Start();
        }
        catch
        {
            codec.Release();
            throw;
        }
        _codec = codec;
        _kind = kind; _width = width; _height = height;
        _waitForKey = false;
        PictureSize?.Invoke(width, height);
    }

    private void Feed(byte[] data, bool isKey)
    {
        var codec = _codec!;
        int index = codec.DequeueInputBuffer(20_000);
        if (index < 0) { Interlocked.Increment(ref _dropped); _waitForKey = true; return; }   // the decoder is behind: wait for a key frame
        var buffer = codec.GetInputBuffer(index)!;
        buffer.Clear();
        if (data.Length > buffer.Capacity()) { codec.QueueInputBuffer(index, 0, 0, 0, 0); _waitForKey = true; return; }
        buffer.Put(data);
        codec.QueueInputBuffer(index, 0, data.Length, System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1_000_000), isKey ? MediaCodecBufferFlags.KeyFrame : 0);
        Interlocked.Increment(ref _fed);
    }

    private void Drain(MediaCodec.BufferInfo info)
    {
        var codec = _codec;
        if (codec == null) return;
        while (true)
        {
            int index = codec.DequeueOutputBuffer(info, 0);
            if (index < 0) return;   // nothing ready, or the format changed (the size is known from the key frame)
            codec.ReleaseOutputBuffer(index, true);
            Interlocked.Increment(ref _shown);
        }
    }

    private void CloseCodec()
    {
        var codec = _codec;
        _codec = null;
        if (codec == null) return;
        try { codec.Stop(); } catch (Exception ex) when (ex is Java.Lang.IllegalStateException) { }
        codec.Release();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelSwitch();
        IMediaFrameSource frames;
        Action<MediaFrame> handler;
        lock (_sourceGate) (frames, handler) = (_frames, _onFrames);
        frames.FrameReceived -= handler;
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try { if (_thread.IsAlive) _thread.Join(1000); } catch (ThreadStateException) { }
        CloseCodec();
        _cts.Dispose();
    }
}
