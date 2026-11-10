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
    private const string H264Mime = "video/avc", Vp8Mime = "video/x-vnd.on2.vp8", Av1Mime = "video/av01";

    private readonly IMediaFrameSource _frames;
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
        _frames.FrameReceived += OnFrame;
    }

    public long Fed => Interlocked.Read(ref _fed);
    public long Shown => Interlocked.Read(ref _shown);
    public long Dropped => Interlocked.Read(ref _dropped);

    private void OnFrame(MediaFrame frame)
    {
        if (!_queue.Writer.TryWrite((frame.Data.ToArray(), frame.IsKey)))
        {
            // The decoder has fallen behind: what is queued is dropped, and nothing is shown until the next key frame.
            Interlocked.Increment(ref _dropped);
            _waitForKey = true;
        }
    }

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
        string mime = kind switch { ScreenCodec.H264 => H264Mime, ScreenCodec.Av1 => Av1Mime, _ => Vp8Mime };
        MediaCodec codec;
        try { codec = MediaCodec.CreateDecoderByType(mime)!; }
        catch (Exception ex) when (kind == ScreenCodec.Av1 && ex is Java.Lang.IllegalArgumentException or Java.IO.IOException or NullReferenceException)
        {
            throw new InvalidOperationException("This phone cannot play AV1. Ask the person sharing to pick another format.", ex);
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
        _frames.FrameReceived -= OnFrame;
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try { if (_thread.IsAlive) _thread.Join(1000); } catch (ThreadStateException) { }
        CloseCodec();
        _cts.Dispose();
    }
}
