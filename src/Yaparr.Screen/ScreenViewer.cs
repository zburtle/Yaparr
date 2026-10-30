using System.Diagnostics;
using System.Threading.Channels;
using Yaparr.Chat;

namespace Yaparr.Screen;

/// <summary>One decoded picture: <paramref name="Bgra"/> holds width x height x 4 bytes, blue first.</summary>
public sealed record Picture(int Width, int Height, byte[] Bgra);

/// <summary>
/// Shows a shared screen: the frames that arrive through the stream tree go to FFmpeg, which decodes them, and each picture
/// is raised as an event. A new key frame with another size restarts the decoder.
/// </summary>
public sealed class ScreenViewer : IDisposable
{
    private readonly MediaView _view;
    private readonly string _ffmpeg;
    private readonly object _gate = new();
    private readonly Queue<string> _errors = new();
    private readonly CancellationTokenSource _cts = new();
    private Decoder? _decoder;
    private bool _disposed;
    private long _pictures, _dropped;

    private const int MaxQueuedFrames = 90;

    public ScreenViewer(MediaView view, string? ffmpegPath = null)
    {
        _view = view;
        _ffmpeg = ffmpegPath ?? FfmpegLocator.Find() ?? throw new FileNotFoundException("FFmpeg was not found. Put ffmpeg.exe in an 'ffmpeg' folder next to the app, or set YAPARR_FFMPEG.");
        _view.FrameReceived += OnFrame;
    }

    public event Action<Picture>? PictureReceived;

    /// <summary>The decoder stopped for a reason other than being disposed.</summary>
    public event Action<Exception>? Failed;

    public long Pictures => Interlocked.Read(ref _pictures);

    /// <summary>Frames not handed to the decoder because it fell behind; the viewer then waits for the next key frame.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public string LastErrors { get { lock (_gate) return string.Join(" | ", _errors); } }

    private void OnFrame(MediaFrame frame)
    {
        var data = frame.Data.ToArray();
        Decoder? decoder;
        lock (_gate)
        {
            if (_disposed) return;
            decoder = _decoder;
            if (frame.IsKey && Vp8.TryGetSize(data, out int w, out int h) && (decoder == null || decoder.Width != w || decoder.Height != h))
            {
                decoder?.Stop();
                decoder = _decoder = StartDecoder(w, h);
            }
        }
        decoder?.Enqueue(data, frame.IsKey);
    }

    private Decoder StartDecoder(int width, int height)
    {
        var psi = new ProcessStartInfo(_ffmpeg) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in FfmpegArgs.Decode()) psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 }) return;
            lock (_gate) { _errors.Enqueue(e.Data); while (_errors.Count > 5) _errors.Dequeue(); }
        };
        process.BeginErrorReadLine();
        var decoder = new Decoder(this, process, width, height);
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

        public int Width { get; }
        public int Height { get; }

        public Decoder(ScreenViewer owner, Process process, int width, int height)
        {
            _owner = owner; _process = process; Width = width; Height = height;
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
                await stdin.WriteAsync(new IvfHeader("VP80", Width, Height).ToBytes(), ct).ConfigureAwait(false);
                long pts = 0;
                await foreach (var frame in _input.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    Interlocked.Decrement(ref _queued);
                    await stdin.WriteAsync(Ivf.FrameHeader(frame.Length, pts++), ct).ConfigureAwait(false);
                    await stdin.WriteAsync(frame, ct).ConfigureAwait(false);
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
                int size = Width * Height * 4;
                while (!ct.IsCancellationRequested)
                {
                    var picture = new byte[size];
                    await stdout.ReadExactlyAsync(picture, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _owner._pictures);
                    _owner.PictureReceived?.Invoke(new Picture(Width, Height, picture));
                }
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException)
            {
                if (!_stop.IsCancellationRequested && !_owner._disposed)
                    _owner.Failed?.Invoke(new InvalidOperationException("FFmpeg stopped decoding. " + _owner.LastErrors));
            }
        }

        public void Stop()
        {
            _stop.Cancel();
            _input.Writer.TryComplete();
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.Dispose();
        }
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
