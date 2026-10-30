using System.Diagnostics;
using Yaparr.Chat;

namespace Yaparr.Screen;

/// <summary>
/// Shares the screen or one window with a room: FFmpeg captures and encodes, and every encoded frame goes out through the
/// stream tree. Windows only, because capture uses the Windows screen sources.
/// </summary>
public sealed class ScreenSharer : IDisposable
{
    private readonly MediaSource _source;
    private readonly ScreenOptions _options;
    private readonly string _ffmpeg;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Queue<string> _errors = new();
    private Process? _process;
    private Task? _run;
    private long _frames, _bytes, _keys;

    public ScreenSharer(MediaSource source, ScreenOptions options, string ffmpegPath)
    {
        _source = source;
        _options = options;
        _ffmpeg = ffmpegPath;
    }

    /// <summary>Announces a screen share in the room and starts sending it.</summary>
    public static ScreenSharer Start(ChatNode node, ChatRoom room, string name, ScreenOptions options, string? ffmpegPath = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sharing a screen works on Windows only for now.");
        ffmpegPath ??= FfmpegLocator.Find() ?? throw new FileNotFoundException("FFmpeg was not found. Put ffmpeg.exe in an 'ffmpeg' folder next to the app, or set YAPARR_FFMPEG.");
        FfmpegArgs.Capture(options); // checks the numbers before anything is announced
        if (options.WindowHandle is { } handle && !ShareWindows.Exists(handle)) throw new InvalidOperationException("That window is not open any more.");
        var source = node.StartMedia(room, "screen", name, signed: true, upstream: false, kbps: options.Kbps);
        var sharer = new ScreenSharer(source, options, ffmpegPath);
        sharer._run = Task.Run(() => sharer.PumpAsync(sharer._cts.Token));
        return sharer;
    }

    public MediaSource Source => _source;
    public long Frames => Interlocked.Read(ref _frames);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long KeyFrames => Interlocked.Read(ref _keys);

    /// <summary>The sharing ended; the exception is null when it was stopped on purpose.</summary>
    public event Action<Exception?>? Ended;

    /// <summary>What FFmpeg last printed to its error output, to explain a failure.</summary>
    public string LastErrors { get { lock (_gate) return string.Join(" | ", _errors); } }

    private async Task PumpAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            var psi = new ProcessStartInfo(_ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in FfmpegArgs.Capture(_options)) psi.ArgumentList.Add(a);
            var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
            lock (_gate) _process = process;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not { Length: > 0 }) return;
                lock (_gate) { _errors.Enqueue(e.Data); while (_errors.Count > 5) _errors.Dequeue(); }
            };
            process.BeginErrorReadLine();

            var stdout = process.StandardOutput.BaseStream;
            await Ivf.ReadHeaderAsync(stdout, ct).ConfigureAwait(false);
            bool started = false;
            while (await Ivf.ReadFrameAsync(stdout, ct).ConfigureAwait(false) is { } frame)
            {
                bool isKey = Vp8.IsKeyFrame(frame.Data);
                // A viewer can only start at a key frame, so nothing is sent before the first one.
                if (!started && !isKey) continue;
                started = true;
                await _source.SendFrameAsync(frame.Data, isKey, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _frames);
                Interlocked.Add(ref _bytes, frame.Data.Length);
                if (isKey) Interlocked.Increment(ref _keys);
            }
            if (!ct.IsCancellationRequested)
            {
                if (_options.WindowHandle is { } handle && !ShareWindows.Exists(handle))
                    throw new InvalidOperationException("The window you were sharing was closed.");
                throw new InvalidOperationException("FFmpeg stopped. " + LastErrors);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally { KillProcess(); }
        Ended?.Invoke(failure);
    }

    private void KillProcess()
    {
        Process? p;
        lock (_gate) { p = _process; _process = null; }
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        p.Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        KillProcess();
        try { _run?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _source.Dispose();
        _cts.Dispose();
    }
}
