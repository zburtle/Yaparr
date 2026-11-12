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
/// Shows a shared screen: the frames that arrive through the stream tree go to FFmpeg, which decodes them, and each picture
/// is raised as an event. A new key frame with another size restarts the decoder.
/// </summary>
public sealed class ScreenViewer : IDisposable
{
    private readonly IMediaFrameSource _view;
    private readonly string _ffmpeg;
    private readonly object _gate = new();
    private readonly Queue<string> _errors = new();
    private readonly CancellationTokenSource _cts = new();
    private Decoder? _decoder;
    private bool _disposed;
    private long _pictures, _dropped;
    private readonly PreviewSize? _preview;
    private TimeSpan _childCpu;

    private const int MaxQueuedFrames = 90;

    // When asked for, H.264 is decoded on the graphics card while that works; the first time it does not, this turns off and the processor does it.
    private GpuApi? _api;
    private string? _fallbackNote;
    private byte[]? _lastKey;

    /// <summary>
    /// FFmpeg hands over NV12 and the picture is converted to BGRA here, with vector instructions, which costs less than FFmpeg doing it.
    /// <paramref name="gpuDecode"/>: also decode H.264 on the graphics card (Windows, Direct3D 11), falling back to the processor if
    /// FFmpeg cannot. Off by default: on the machines measured (issue #100) it did not use less processor time than decoding in software.
    /// </summary>
    public ScreenViewer(IMediaFrameSource view, string? ffmpegPath = null, bool gpuDecode = false, PreviewSize? preview = null)
    {
        _preview = preview;
        // A watch decodes on the card through Direct3D 11 when asked to; a preview of every frame through Direct3D 12, which can also scale on the card
        // (and falls back to Direct3D 11, then to the processor, when FFmpeg or the card cannot).
        if (OperatingSystem.IsWindows())
            _api = preview == null ? (gpuDecode ? GpuApi.D3d11 : null) : (preview.KeyFramesOnly ? null : GpuApi.D3d12);
        _view = view;
        _ffmpeg = ffmpegPath ?? FfmpegLocator.Find() ?? throw new FileNotFoundException("FFmpeg was not found. Put ffmpeg.exe in an 'ffmpeg' folder next to the app, or set YAPARR_FFMPEG.");
        _view.FrameReceived += OnFrame;
    }

    public event Action<Picture>? PictureReceived;

    /// <summary>The decoder stopped for a reason other than being disposed.</summary>
    public event Action<Exception>? Failed;

    public long Pictures => Interlocked.Read(ref _pictures);

    /// <summary>The processor time the FFmpeg that decodes has used, over all the ones started so far. Not counted in this program's own time.</summary>
    public TimeSpan DecoderCpu
    {
        get
        {
            Decoder? d; TimeSpan done;
            lock (_gate) { d = _decoder; done = _childCpu; }
            return done + (d?.Cpu ?? default);
        }
    }

    /// <summary>Whether the current decoder runs on the graphics card.</summary>
    public bool GpuDecoding { get { lock (_gate) return _api != null && _decoder?.Codec == ScreenCodec.H264; } }

    /// <summary>How pictures are being decoded, for a log: on the card through Direct3D 12 or 11, or by the processor, and why not the first choice when it did not work.</summary>
    public string DecoderKind
    {
        get
        {
            lock (_gate)
            {
                string kind = _api switch { GpuApi.D3d12 => "on the card through Direct3D 12", GpuApi.D3d11 => "on the card through Direct3D 11", _ => "on the processor" };
                return _fallbackNote == null ? kind : $"{kind} ({_fallbackNote})";
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

    /// <summary>Frames waiting for the decoder. Staying above a few means the decoder is behind.</summary>
    /// <summary>What is being decoded now, like "H264 1920x1080" (null before the first key frame).</summary>
    public string? PictureKind { get { lock (_gate) return _decoder is { } d ? $"{d.Codec} {d.Width}x{d.Height}" : null; } }

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
        lock (_gate)
        {
            if (_disposed) return;
            decoder = _decoder;
            if (frame.IsKey) _lastKey = data;
            if (frame.IsKey && ScreenCodecs.TryGetKeyFrame(data, out var codec, out int w, out int h)
                && (decoder == null || decoder.Codec != codec || decoder.Width != w || decoder.Height != h))
            {
                decoder?.Stop();
                decoder = _decoder = StartDecoder(codec, w, h);
            }
        }
        decoder?.Enqueue(data, frame.IsKey);
    }

    private Decoder StartDecoder(ScreenCodec codec, int width, int height)
    {
        var psi = new ProcessStartInfo(_ffmpeg) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var shown = _preview?.Fit(width, height);
        foreach (var a in FfmpegArgs.DecodeForViewer(codec, _api != null, shown, _preview?.KeyFramesOnly ?? true, _api ?? GpuApi.D3d11)) psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 }) return;
            lock (_gate) { _errors.Enqueue(e.Data); while (_errors.Count > 5) _errors.Dequeue(); }
        };
        process.BeginErrorReadLine();
        var decoder = new Decoder(this, process, codec, width, height, _api != null && codec == ScreenCodec.H264, shown ?? (width, height));
        decoder.Start(_cts.Token);
        return decoder;
    }

    private sealed class Decoder
    {
        private readonly ScreenViewer _owner;
        private readonly Process _process;
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        private readonly CancellationTokenSource _stop = new();
        private int _queued;
        private bool _waitForKey;

        public int Queued => Volatile.Read(ref _queued);

        public ScreenCodec Codec { get; }
        public int Width { get; }
        public int Height { get; }

        public bool OnCard { get; }

        /// <summary>The size of the pictures that come out: the size of the stream, or the smaller one a preview is scaled to.</summary>
        public (int Width, int Height) Out { get; }

        public Decoder(ScreenViewer owner, Process process, ScreenCodec codec, int width, int height, bool onCard, (int Width, int Height) output)
        {
            _owner = owner; _process = process; Codec = codec; Width = width; Height = height; OnCard = onCard; Out = output;
        }

        public TimeSpan Cpu
        {
            get
            {
                try { return _process.TotalProcessorTime; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return default; }
            }
        }

        public void Start(CancellationToken ct)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token).Token;
            _ = Task.Run(() => WriteAsync(linked));
            _ = Task.Run(() => ReadAsync(linked));
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

        private async Task WriteAsync(CancellationToken ct)
        {
            try
            {
                var stdin = _process.StandardInput.BaseStream;
                bool h264 = Codec == ScreenCodec.H264;
                if (!h264) await stdin.WriteAsync(new IvfHeader(Codec == ScreenCodec.Av1 ? "AV01" : "VP80", Width, Height).ToBytes(), ct).ConfigureAwait(false);
                long pts = 0;
                await foreach (var frame in _input.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    Interlocked.Decrement(ref _queued);
                    if (h264)
                    {
                        // A preview gets a picture only now and then, and FFmpeg holds one picture back until the next goes in, so the key frame goes in twice
                        // (two identical pictures come out, at once; the second is dropped when drawn).
                        for (int copy = 0; copy < (_owner._preview is { KeyFramesOnly: true } ? 2 : 1); copy++)
                        {
                            await stdin.WriteAsync(frame, ct).ConfigureAwait(false);
                            await stdin.WriteAsync(H264.EndMarker.ToArray(), ct).ConfigureAwait(false); // the picture is complete: no waiting for the next
                        }
                    }
                    else
                    {
                        await stdin.WriteAsync(Ivf.FrameHeader(frame.Length, pts++), ct).ConfigureAwait(false);
                        await stdin.WriteAsync(frame, ct).ConfigureAwait(false);
                    }
                    await stdin.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }

        private async Task ReadAsync(CancellationToken ct)
        {
            try
            {
                var stdout = _process.StandardOutput.BaseStream;
                var (outWidth, outHeight) = Out;
                int size = outWidth * outHeight * 4;
                var raw = new byte[Nv12.Size(outWidth, outHeight)];
                // A preview of every frame keeps no more than its rate: the rest are read (they have to be) but not converted or drawn.
                long keepEvery = _owner._preview is { KeyFramesOnly: false } p ? Stopwatch.Frequency / Math.Max(1, p.Fps) : 0, last = 0;
                while (!ct.IsCancellationRequested)
                {
                    await stdout.ReadExactlyAsync(raw, ct).ConfigureAwait(false);
                    if (keepEvery > 0)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (last != 0 && now - last < keepEvery * 9 / 10) continue;
                        last = now;
                    }
                    var picture = _owner.Rent(size);
                    Nv12.ToBgra(raw, outWidth, outHeight, picture);
                    Interlocked.Increment(ref _owner._pictures);
                    _owner.PictureReceived?.Invoke(new Picture(outWidth, outHeight, picture));
                }
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException)
            {
                if (_stop.IsCancellationRequested || _owner._disposed) return;
                if (OnCard && _owner.FallBackToProcessor(this)) return;
                _owner.Failed?.Invoke(new InvalidOperationException("FFmpeg stopped decoding. " + _owner.LastErrors));
            }
        }

        public void Stop()
        {
            _stop.Cancel();
            _input.Writer.TryComplete();
            _owner.AddCpu(Cpu);
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }

    private void AddCpu(TimeSpan used) { lock (_gate) _childCpu += used; }

    /// <summary>The graphics card could not decode: the processor takes over, starting with the key frame that is already here.</summary>
    private bool FallBackToProcessor(Decoder failed)
    {
        Decoder? replacement = null;
        byte[]? key;
        lock (_gate)
        {
            if (_disposed || _decoder != failed) return _decoder != failed && !_disposed;
            // The next choice down: Direct3D 12, then Direct3D 11, then the processor. What FFmpeg said goes in the note, for the log.
            string said = LastErrors.Length > 0 ? ": " + (LastErrors.Length > 120 ? LastErrors[..120] : LastErrors) : "";
            _fallbackNote = (_fallbackNote == null ? "" : _fallbackNote + "; ") + (_api == GpuApi.D3d12 ? "Direct3D 12" : "Direct3D 11") + " did not work" + said;
            _api = _api == GpuApi.D3d12 ? GpuApi.D3d11 : null;
            _errors.Clear();   // what the next one says is about the next one
            key = _lastKey;
            _decoder = null;
            if (key != null && ScreenCodecs.TryGetKeyFrame(key, out var codec, out int w, out int h)) replacement = _decoder = StartDecoder(codec, w, h);
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
