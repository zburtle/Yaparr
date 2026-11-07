using System.Threading.Channels;
using Android.Content;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

/// <summary>
/// One screen share from the phone: what Android's screen capture (MediaProjection) shows, drawn into the input of an H.264 encoder, and its
/// frames sent to the room through a <see cref="MediaSource"/> like the computer's. With Android 14 and newer the person chooses what to
/// share in the system's own dialog, and "Share one app" keeps the status bar, notifications and other apps out of the picture.
/// It runs inside <see cref="ScreenShareService"/>; dispose it to stop.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("android34.0")]
internal sealed class ScreenShareJob : IDisposable
{
    private const string Mime = "video/avc";

    private readonly Context _context;
    private readonly MediaSource _source;
    private readonly ShareQuality _quality;
    private readonly MediaSource? _soundSource;
    private ScreenShareSound? _sound;
    private int _kbps => _quality.Kbps;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(byte[] Frame, bool IsKey)> _queue = Channel.CreateBounded<(byte[], bool)>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });
    private readonly object _gate = new();

    private MediaProjection? _projection;
    private VirtualDisplay? _display;
    private MediaCodec? _codec;
    private Surface? _surface;
    private H264FramePacker _packer = new();
    private Handler? _handler;
    private Thread? _pump;
    private Task? _send;
    private int _dpi;
    private volatile int _wantWidth, _wantHeight;
    private int _width, _height;
    private volatile bool _needKey = true, _stopped, _hidden;
    private long _frames, _keys, _bytes, _dropped;
    private volatile string? _lastError;

    /// <summary>The share ended, with the reason (null when it was asked for): the person stopped it in Android, or the encoder failed. Raised on another thread.</summary>
    public event Action<string?>? Ended;

    /// <summary>What is being shared changed size (the person chose an app, turned the phone, folded it): the picture is now this size.</summary>
    public event Action<int, int>? Resized;

    /// <summary>The sound could not be shared, or stopped: why. The picture goes on. Raised on another thread.</summary>
    public event Action<string>? SoundProblem;

    /// <param name="quality">The size, pictures a second and bitrate to send at.</param>
    /// <param name="soundSource">The stream for the sound of the shared app, or null to share the picture alone.</param>
    public ScreenShareJob(Context context, MediaSource source, ShareQuality quality, MediaSource? soundSource = null)
    {
        _context = context;
        _source = source;
        _quality = quality;
        _soundSource = soundSource;
        _source.KeyFrameRequested += () => _needKey = true;
    }

    public MediaSource Source => _source;

    /// <summary>The size of the picture being sent, once the share has started.</summary>
    public (int Width, int Height) Size => (_width, _height);

    public long Frames => Interlocked.Read(ref _frames);
    public long KeyFrames => Interlocked.Read(ref _keys);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The shared app is out of sight (another app is in front): there is nothing new to show until it is back.</summary>
    public bool Hidden => _hidden;

    /// <summary>The last thing that went wrong with the encoder, for the diagnostics.</summary>
    public string? LastError => _lastError;

    private sealed class Callback : MediaProjection.Callback
    {
        private readonly ScreenShareJob _job;
        public Callback(ScreenShareJob job) => _job = job;
        public override void OnStop() => _job.Fail(null);
        public override void OnCapturedContentResize(int width, int height) => _job.Resize(width, height);
        public override void OnCapturedContentVisibilityChanged(bool isVisible) => _job._hidden = !isVisible;
    }

    /// <summary>Starts capturing with the projection Android handed out for this share. Must be called after the service is in the foreground.</summary>
    public void Start(MediaProjection projection)
    {
        _projection = projection;
        _handler = new Handler(Looper.MainLooper!);

        var metrics = _context.Resources!.DisplayMetrics!;
        _dpi = (int)metrics.DensityDpi;
        var (w, h) = H264FramePacker.FitSize(metrics.WidthPixels, metrics.HeightPixels, _quality.MaxLongSide);
        _wantWidth = w; _wantHeight = h;

        // Android 14 wants the callback registered before the capture is created.
        projection.RegisterCallback(new Callback(this), _handler);
        OpenEncoder(w, h);
        _display = projection.CreateVirtualDisplay("Yaparr screen share", w, h, _dpi, (DisplayFlags)(int)VirtualDisplayFlags.AutoMirror, _surface, null, _handler);
        if (_display == null) throw new InvalidOperationException("Android would not start the screen capture.");

        StartSound(projection);
        _send = Task.Run(() => SendLoopAsync(_cts.Token));
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "screen share encoder" };
        _pump.Start();
    }

    // ---- the encoder ----

    // Optional settings an encoder may refuse (they are about quality and about what a still screen does, not about whether it works): the encoder is
    // made with all of them first, then with fewer, since devices differ in which they accept.
    [Flags]
    private enum Extras { None = 0, Prepend = 1, Repeat = 2, Mode = 4, All = 7 }

    private static MediaFormat MakeFormat(int width, int height, int kbps, ShareQuality quality, Extras extras)
    {
        var format = MediaFormat.CreateVideoFormat(Mime, width, height);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        format.SetInteger(MediaFormat.KeyBitRate, kbps * 1000);
        format.SetInteger(MediaFormat.KeyFrameRate, quality.Fps);
        format.SetInteger(MediaFormat.KeyIFrameInterval, 2);
        if ((extras & Extras.Mode) != 0) format.SetInteger(MediaFormat.KeyBitrateMode, (int)BitrateMode.Vbr);
        // A screen that does not change makes no new picture: ask for the last one again, so the viewers' picture does not stall.
        if ((extras & Extras.Repeat) != 0) format.SetLong(MediaFormat.KeyRepeatPreviousFrameAfter, 500_000);
        if ((extras & Extras.Prepend) != 0) format.SetInteger(MediaFormat.KeyPrependHeaderToSyncFrames, 1);
        return format;
    }

    private void OpenEncoder(int width, int height)
    {
        Exception? last = null;
        foreach (var extras in new[] { Extras.All, Extras.Repeat | Extras.Mode, Extras.Repeat, Extras.None })
        {
            var codec = MediaCodec.CreateEncoderByType(Mime)!;
            try
            {
                using var format = MakeFormat(width, height, _kbps, _quality, extras);
                codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
                var surface = codec.CreateInputSurface()!;
                codec.Start();
                lock (_gate)
                {
                    _codec = codec;
                    _surface = surface;
                    _packer = new H264FramePacker();
                    _width = width; _height = height;
                }
                _needKey = true;
                if (extras != Extras.All) Android.Util.Log.Info("Yaparr", $"screen share: the encoder took {extras} only");
                return;
            }
            catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or Java.Lang.IllegalStateException or MediaCodec.CodecException)
            {
                last = ex;
                Android.Util.Log.Warn("Yaparr", $"screen share: the encoder refused {extras}: {ex.Message}");
                codec.Release();
            }
        }
        throw new InvalidOperationException("The phone's video encoder would not start" + (last?.Message is { Length: > 0 } m ? ": " + m : "."), last);
    }

    private void Resize(int capturedWidth, int capturedHeight)
    {
        var (w, h) = H264FramePacker.FitSize(capturedWidth, capturedHeight, _quality.MaxLongSide);
        _wantWidth = w; _wantHeight = h;
    }

    // The encoder is rebuilt on this thread when the captured area changes size: the picture is then a new size, which a viewer takes at the next key frame.
    private void ChangeSize()
    {
        int w = _wantWidth, h = _wantHeight;
        MediaCodec? old; Surface? oldSurface;
        lock (_gate) { old = _codec; oldSurface = _surface; }
        OpenEncoder(w, h);
        _display?.Resize(w, h, _dpi);
        if (_display != null) _display.Surface = _surface;
        try { old?.Stop(); } catch (Exception ex) when (ex is Java.Lang.IllegalStateException or InvalidOperationException) { }
        old?.Release();
        oldSurface?.Release();
        Resized?.Invoke(w, h);
    }

    private void PumpLoop()
    {
        var info = new MediaCodec.BufferInfo();
        try
        {
            while (!_stopped)
            {
                if (_wantWidth != _width || _wantHeight != _height) ChangeSize();
                if (_needKey) RequestKeyFrame();

                var codec = _codec!;
                int index = codec.DequeueOutputBuffer(info, 100_000);
                if (index < 0) continue;   // nothing yet, or the output format changed (the configuration comes as a buffer)

                byte[]? data = null;
                if (info.Size > 0)
                {
                    var buffer = codec.GetOutputBuffer(index)!;
                    buffer.Position(info.Offset);
                    buffer.Limit(info.Offset + info.Size);
                    data = new byte[info.Size];
                    buffer.Get(data);
                }
                var flags = info.Flags;
                codec.ReleaseOutputBuffer(index, false);
                if (data == null) continue;

                if ((flags & MediaCodecBufferFlags.CodecConfig) != 0) { _packer.SetConfig(data); continue; }
                Offer(data, (flags & MediaCodecBufferFlags.KeyFrame) != 0);
            }
        }
        catch (Exception ex) when (!_stopped)
        {
            _lastError = ex.Message;
            Android.Util.Log.Error("Yaparr", "screen share encoder stopped: " + ex);
            Fail("The encoder stopped: " + ex.Message);
        }
    }

    private void RequestKeyFrame()
    {
        _needKey = false;
        try
        {
            using var bundle = new Bundle();
            bundle.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 0);
            _codec?.SetParameters(bundle);
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.Lang.IllegalArgumentException) { }
    }

    // ---- sending ----

    private bool _waitingForKey = true;

    // A frame is sent to the room when the connection has caught up with the ones before it. A phone's picture is made faster than a slow upload can carry,
    // so when the line is behind, pictures are dropped until the next key frame (and one is asked for) instead of building up a delay.
    private void Offer(byte[] encoded, bool isKey)
    {
        var frame = _packer.Pack(encoded, isKey);
        if (frame == null) { _needKey = true; return; }
        if (_waitingForKey && !isKey) { Interlocked.Increment(ref _dropped); return; }
        if (isKey)
        {
            // A key frame starts afresh: what is queued behind it is of no use.
            while (_queue.Reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
            _waitingForKey = false;
        }
        if (!_queue.Writer.TryWrite((frame, isKey)))
        {
            Interlocked.Increment(ref _dropped);
            _waitingForKey = true;
            _needKey = true;
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (frame, isKey) in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await _source.SendFrameAsync(frame, isKey, ct).ConfigureAwait(false); }
                catch (ArgumentException) { _needKey = true; continue; }   // a frame over the limit: the next key frame is smaller
                Interlocked.Increment(ref _frames);
                if (isKey) Interlocked.Increment(ref _keys);
                Interlocked.Add(ref _bytes, frame.Length);
            }
        }
        catch (System.OperationCanceledException) { }
        catch (Exception ex) when (!_stopped)
        {
            _lastError = ex.Message;
            Fail("Sending the picture stopped: " + ex.Message);
        }
    }

    // ---- sound ----

    // The sound of the shared app, if it was asked for: the picture is shared whether or not this works.
    private void StartSound(MediaProjection projection)
    {
        if (_soundSource == null) return;
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) { SoundProblem?.Invoke("Sharing sound needs Android 10 or newer, so only the picture is shared."); return; }
        try
        {
            var sound = new ScreenShareSound(projection, new Yaparr.Voice.StreamAudioSender(_soundSource));
            sound.Failed += reason => SoundProblem?.Invoke(reason + " The picture is still shared.");
            sound.Start();
            _sound = sound;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Android.Util.Log.Warn("Yaparr", "screen share sound could not start: " + ex.Message);
            SoundProblem?.Invoke("The sound could not be shared (" + ex.Message + "), so only the picture is.");
        }
    }

    /// <summary>Whether the sound is being shared along with the picture.</summary>
    public bool SharingSound => _sound != null;

    // ---- stopping ----

    private int _ended;

    /// <summary>The person stopped the share from its notification.</summary>
    internal void RaiseStopped() => Fail(null);

    /// <summary>The share could not start or went wrong.</summary>
    internal void RaiseFailed(string reason) => Fail(reason);

    private void Fail(string? reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        Ended?.Invoke(reason);
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopped = true;
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try { _pump?.Join(1000); } catch (ThreadStateException) { }
        try { _display?.Release(); } catch (Exception ex) when (ex is Java.Lang.IllegalStateException) { }
        _display = null;
        MediaCodec? codec; Surface? surface;
        lock (_gate) { codec = _codec; surface = _surface; _codec = null; _surface = null; }
        try { codec?.Stop(); } catch (Exception ex) when (ex is Java.Lang.IllegalStateException) { }
        codec?.Release();
        surface?.Release();
        try { _projection?.Stop(); } catch (Exception ex) when (ex is Java.Lang.IllegalStateException) { }
        _projection = null;
        _sound?.Dispose();
        _sound = null;
        _soundSource?.Dispose();
        _source.Dispose();
        _cts.Dispose();
    }
}
