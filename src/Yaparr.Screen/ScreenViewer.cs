using System.Diagnostics;
using System.Threading.Channels;
using Yaparr.Chat;

namespace Yaparr.Screen;

/// <summary>
/// A preview instead of a watch: the pictures are scaled down by FFmpeg to fit this box, so reading, converting and drawing them costs little,
/// and no more than <paramref name="Fps"/> of them a second are kept. With one or two a second only key frames are decoded (a small part of
/// what decoding every frame costs); with more, every frame is decoded (on the graphics card where there is one), because a frame cannot be
/// decoded without the ones before it, and that is where the processor time goes.
/// </summary>
public sealed record PreviewSize(int MaxWidth, int MaxHeight, int Fps = 1)
{
    /// <summary>One or two pictures a second is what the key frames give (a key frame about every second): nothing in between is decoded.</summary>
    public bool KeyFramesOnly => Fps <= 2;

    /// <summary>The size a <paramref name="width"/> x <paramref name="height"/> picture is shown at: as large as fits, never larger than it is, both sides even.</summary>
    public (int Width, int Height) Fit(int width, int height)
    {
        double scale = Math.Min(1.0, Math.Min((double)MaxWidth / Math.Max(1, width), (double)MaxHeight / Math.Max(1, height)));
        return (Math.Max(2, (int)(width * scale) & ~1), Math.Max(2, (int)(height * scale) & ~1));
    }
}

/// <summary>One decoded picture: <paramref name="Bgra"/> holds width x height x 4 bytes, blue first. Give it back with <see cref="ScreenViewer.Return"/> when done.</summary>
public sealed record Picture(int Width, int Height, byte[] Bgra);

/// <summary>
/// Shows a shared screen: the frames that arrive through the stream tree are decoded inside this program with FFmpeg's libraries (<see cref="InProcessDecoder"/>),
/// and each picture is raised as an event. A new key frame with another size starts a new decoder. Nothing runs as a separate program.
/// </summary>
public sealed class ScreenViewer : IDisposable
{
    private readonly IMediaFrameSource _view;
    private readonly object _gate = new();
    private readonly Queue<string> _errors = new();
    private readonly CancellationTokenSource _cts = new();
    private Decoder? _decoder;
    private bool _disposed;
    private long _pictures, _dropped;
    private readonly PreviewSize? _preview;
    private TimeSpan _doneCpu;

    private const int MaxQueuedFrames = 90;

    // Where H.264 is decoded: the card while that works (when asked for), and the next choice down the first time it does not (Direct3D 12, then 11; NVDEC, then
    // VA-API; then the processor).
    private DecodeDevice _device;
    private string? _fallbackNote;
    private byte[]? _lastKey;

    /// <summary>
    /// The decoder hands over NV12 and the picture is converted to BGRA here, with vector instructions, which costs less than the library doing it.
    /// <paramref name="gpuDecode"/>: decode H.264 on the graphics card (Windows: Direct3D 11; Linux: NVDEC, then VA-API) while that works. Off by default for a watch:
    /// on the machines measured (issue #100) it did not use less processor time than decoding in software. A preview of every frame (<see cref="PreviewSize.KeyFramesOnly"/>
    /// false) decodes on the card where it can (Direct3D 12 on Windows, scaled there; NVDEC or VA-API on Linux). Nothing is started until the first key frame arrives; a decoder that cannot start raises <see cref="Failed"/>.
    /// </summary>
    public ScreenViewer(IMediaFrameSource view, bool gpuDecode = false, PreviewSize? preview = null)
    {
        _preview = preview;
        _view = view;
        bool card = preview == null ? gpuDecode : !preview.KeyFramesOnly;
        _device = card ? InProcessDecoder.FirstCard(preferD3d12: preview != null) : DecodeDevice.Processor;
        // A program that ended in the middle of decoding, twice in a row, decodes on the processor (a driver is the likeliest cause) until a decoder ends well,
        // unless the card was asked for in so many words (see InProcessDecodeGuard).
        if (_device != DecodeDevice.Processor && !InProcessSettings.CardDecodeAsked && !InProcessDecodeGuard.Allowed)
        {
            _fallbackNote = $"the card is not used: the program ended in the middle of decoding {InProcessDecodeGuard.UncleanEndings} times in a row (see {InProcessDecodeGuard.MarkFile}); YAPARR_CARD_DECODE=1 asks for it";
            _device = DecodeDevice.Processor;
        }
        _view.FrameReceived += OnFrame;
    }

    /// <summary>Whether this program can decode a shared screen: FFmpeg's libraries are there and load. When not, <paramref name="why"/> says why in plain words.</summary>
    public static bool CanDecode(out string why)
    {
        if (FfmpegLibraries.TryLoad(out why)) return true;
        why = "FFmpeg's libraries are needed to show a shared screen. " + why;
        return false;
    }

    public event Action<Picture>? PictureReceived;

    /// <summary>The decoder stopped for a reason other than being disposed (or could not start).</summary>
    public event Action<Exception>? Failed;

    public long Pictures => Interlocked.Read(ref _pictures);

    /// <summary>The time the decoders' threads spent decoding and converting, over all the ones started so far (part of this program's own processor time).</summary>
    public TimeSpan DecoderCpu
    {
        get
        {
            Decoder? d; TimeSpan done;
            lock (_gate) { d = _decoder; done = _doneCpu; }
            return done + (d?.Cpu ?? default);
        }
    }

    /// <summary>Whether the current decoder's pictures come out of the graphics card.</summary>
    public bool GpuDecoding { get { lock (_gate) return _decoder?.OnCard ?? false; } }

    /// <summary>How pictures are being decoded, for a log: on the card through Direct3D 12 or 11, or by the processor, and why not the first choice when it did not work.</summary>
    public string DecoderKind
    {
        get
        {
            lock (_gate)
            {
                string kind = _device == DecodeDevice.Processor ? "on the processor" : "on the card through " + InProcessDecoder.Name(_device);
                string? note = _fallbackNote;
                // The library decodes on the processor by itself a stream the card does not take (a profile it lacks): said, so that the log is true.
                if (_device != DecodeDevice.Processor && _decoder is { Pictures: > 0, OnCard: false })
                    note = (note == null ? "" : note + "; ") + "the card did not take this stream, the processor decodes it";
                return note == null ? kind : $"{kind} ({note})";
            }
        }
    }

    /// <summary>Frames not handed to the decoder because it fell behind; the viewer then waits for the next key frame.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public string LastErrors { get { lock (_gate) return string.Join(" | ", _errors); } }

    private long _framesIn, _bytesIn;

    /// <summary>Encoded frames and bytes that reached this viewer (before decoding).</summary>
    public long FramesIn => Interlocked.Read(ref _framesIn);
    public long BytesIn => Interlocked.Read(ref _bytesIn);

    /// <summary>What is being decoded now, like "H264 1920x1080" (null before the first key frame).</summary>
    public string? PictureKind { get { lock (_gate) return _decoder is { } d ? $"{d.Codec} {d.Width}x{d.Height}" : null; } }

    /// <summary>Frames waiting for the decoder. Staying above a few means the decoder is behind.</summary>
    public int DecodeQueue { get { lock (_gate) return _decoder?.Queued ?? 0; } }

    // A 1080p picture is 8 MB. A new array for each one lands on the large object heap and makes the garbage collector pause
    // every few seconds, which shows as stutter; so arrays of the same size are handed back and used again.
    private readonly System.Collections.Concurrent.ConcurrentBag<byte[]> _spare = new();
    private const int MaxSpare = 8;

    private byte[] Rent(int size)
    {
        while (_spare.TryTake(out var array))
            if (array.Length == size) return array;
        return new byte[size];
    }

    /// <summary>Gives back the memory of a picture that has been shown, so the next one can use it. The picture must not be used afterwards.</summary>
    public void Return(Picture picture)
    {
        if (_spare.Count < MaxSpare) _spare.Add(picture.Bgra);
    }

    private void OnFrame(MediaFrame frame)
    {
        if (_preview is { KeyFramesOnly: true } && !frame.IsKey) return;   // not even copied: this preview shows key frames only
        var data = frame.Data.ToArray();
        Interlocked.Increment(ref _framesIn);
        Interlocked.Add(ref _bytesIn, data.Length);
        Decoder? decoder;
        Exception? failed = null;
        lock (_gate)
        {
            if (_disposed) return;
            decoder = _decoder;
            if (frame.IsKey) _lastKey = data;
            if (frame.IsKey && ScreenCodecs.TryGetKeyFrame(data, out var codec, out int w, out int h)
                && (decoder == null || decoder.Codec != codec || decoder.Width != w || decoder.Height != h))
            {
                decoder?.Stop();
                try { decoder = _decoder = StartDecoder(codec, w, h); }
                catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
                {
                    decoder = _decoder = null;
                    failed = ex;
                }
            }
        }
        if (failed != null) { Failed?.Invoke(failed); return; }
        decoder?.Enqueue(data, frame.IsKey);
    }

    // The first choice that opens: the device wanted, then the ones below it. Only the processor's failure goes to the caller (no libraries, no decoder for the codec).
    private Decoder StartDecoder(ScreenCodec codec, int width, int height)
    {
        if (!CanDecode(out var why)) throw new PlatformNotSupportedException(why);
        while (true)
        {
            try
            {
                var decoder = new Decoder(this, codec, width, height, _preview, codec == ScreenCodec.H264 ? _device : DecodeDevice.Processor);
                decoder.Start(_cts.Token);
                return decoder;
            }
            catch (PlatformNotSupportedException ex) when (codec == ScreenCodec.H264 && _device != DecodeDevice.Processor)
            {
                StepDown(ex.Message);
            }
        }
    }

    // Under the gate: the next choice down, and a note of why for the log.
    private void StepDown(string why)
    {
        string said = why.Length > 120 ? why[..120] : why;
        _fallbackNote = (_fallbackNote == null ? "" : _fallbackNote + "; ") + InProcessDecoder.Name(_device) + " did not work" + (said.Length > 0 ? ": " + said : "");
        _device = InProcessDecoder.NextDown(_device);
        _errors.Clear();   // what the next one says is about the next one
    }

    /// <summary>
    /// Decodes one stream (one codec and size) on a thread of its own. Frames wait in a queue; a decoder that falls behind drops frames until the next key frame
    /// (anything between would decode wrongly).
    /// </summary>
    private sealed class Decoder
    {
        private readonly ScreenViewer _owner;
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        private readonly CancellationTokenSource _stop = new();
        private readonly InProcessDecoder _decoder;
        private readonly PreviewSize? _preview;
        private int _queued;
        private bool _waitForKey;
        private long _busyTicks, _pictures;
        private int _failures;
        private volatile bool _onCard;

        public Decoder(ScreenViewer owner, ScreenCodec codec, int width, int height, PreviewSize? preview, DecodeDevice device)
        {
            _owner = owner; Codec = codec; Width = width; Height = height; _preview = preview;
            InProcessDecodeGuard.Begin();   // until the decoder is freed (at the end of Run), or the program ends in the middle of it
            try { _decoder = new InProcessDecoder(codec, device); }
            catch { InProcessDecodeGuard.End(); throw; }
        }

        public ScreenCodec Codec { get; }
        public int Width { get; }
        public int Height { get; }
        public int Queued => Volatile.Read(ref _queued);
        public long Pictures => Interlocked.Read(ref _pictures);

        /// <summary>Whether the last picture came out of the graphics card.</summary>
        public bool OnCard => _onCard;

        public TimeSpan Cpu => TimeSpan.FromSeconds((double)Interlocked.Read(ref _busyTicks) / Stopwatch.Frequency);

        public void Start(CancellationToken ct)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token).Token;
            _ = Task.Run(() => Run(linked));
        }

        public void Enqueue(byte[] frame, bool isKey)
        {
            // If the decoder cannot keep up, throw frames away until a key frame: anything between would decode wrongly.
            if (Volatile.Read(ref _queued) > MaxQueuedFrames) _waitForKey = true;
            if (_waitForKey)
            {
                if (!isKey) { Interlocked.Increment(ref _owner._dropped); return; }
                _waitForKey = false;
            }
            Interlocked.Increment(ref _queued);
            _input.Writer.TryWrite(frame);
        }

        private async Task Run(CancellationToken ct)
        {
            try
            {
                int maxWidth = _preview?.MaxWidth ?? 0, maxHeight = _preview?.MaxHeight ?? 0;
                // A preview of every frame keeps no more than its rate: the rest are decoded (they have to be) but not converted or drawn.
                long keepEvery = _preview is { KeyFramesOnly: false } p ? Stopwatch.Frequency / Math.Max(1, p.Fps) : 0, last = 0;
                await foreach (var frame in _input.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    Interlocked.Decrement(ref _queued);
                    long started = Stopwatch.GetTimestamp();
                    bool ok = _decoder.Decode(frame, maxWidth, maxHeight, (nv12, w, h) =>
                    {
                        _onCard = _decoder.LastOnCard;
                        Interlocked.Increment(ref _pictures);
                        if (keepEvery > 0)
                        {
                            long now = Stopwatch.GetTimestamp();
                            if (last != 0 && now - last < keepEvery * 9 / 10) return;
                            last = now;
                        }
                        Show(nv12, w, h);
                    });
                    Interlocked.Add(ref _busyTicks, Stopwatch.GetTimestamp() - started);
                    if (ok) { _failures = 0; continue; }
                    lock (_owner._gate) { _owner._errors.Enqueue(_decoder.LastError); while (_owner._errors.Count > 5) _owner._errors.Dequeue(); }
                    // Frames that do not decode one after the other on the card: the next choice down gets to try, from the last key frame. On the processor a damaged
                    // stream is waited out (the next key frame mends it).
                    if (++_failures >= 30 && _decoder.Device != DecodeDevice.Processor && !_stop.IsCancellationRequested && _owner.FallBack(this, null)) return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!_stop.IsCancellationRequested && !_owner._disposed)
            {
                if (_decoder.Device != DecodeDevice.Processor && _owner.FallBack(this, ex.Message)) return;
                _owner.Failed?.Invoke(ex);
            }
            finally
            {
                _owner.AddCpu(Cpu);
                // The native decoder is freed here, by the thread that used it, and only then does the guard's mark go: a program that quits while a viewer is
                // decoding has the mark left only if the thread was still inside the library.
                try { _decoder.Dispose(); } catch (Exception) { } finally { InProcessDecodeGuard.End(); }
            }
        }

        // Converts a decoded NV12 picture to BGRA and raises the event.
        private void Show(ReadOnlySpan<byte> nv12, int width, int height)
        {
            var picture = _owner.Rent(width * height * 4);
            Nv12.ToBgra(nv12, width, height, picture);
            Interlocked.Increment(ref _owner._pictures);
            _owner.PictureReceived?.Invoke(new Picture(width, height, picture));
        }

        /// <summary>Stops: the thread leaves its loop (after the frame it is in, if any) and frees the decoder.</summary>
        public void Stop()
        {
            _stop.Cancel();
            _input.Writer.TryComplete();
        }
    }

    private void AddCpu(TimeSpan used) { lock (_gate) _doneCpu += used; }

    /// <summary>The card could not decode: the next choice down takes over, starting with the key frame that is already here.</summary>
    private bool FallBack(Decoder failed, string? why)
    {
        Decoder? replacement = null;
        byte[]? key;
        lock (_gate)
        {
            if (_disposed || _decoder != failed) return _decoder != failed && !_disposed;
            StepDown(why ?? LastErrors);
            key = _lastKey;
            _decoder = null;
            if (key != null && ScreenCodecs.TryGetKeyFrame(key, out var codec, out int w, out int h))
            {
                try { replacement = _decoder = StartDecoder(codec, w, h); }
                catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException) { return false; }
            }
        }
        failed.Stop();
        replacement?.Enqueue(key!, true);
        return true;
    }

    public void Dispose()
    {
        Decoder? d;
        lock (_gate) { _disposed = true; d = _decoder; _decoder = null; }
        _view.FrameReceived -= OnFrame;
        _cts.Cancel();
        d?.Stop();
        _cts.Dispose();
    }
}
