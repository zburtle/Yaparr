using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Desktop;

/// <summary>A window that shows one shared screen. Closing it stops watching.</summary>
public partial class ScreenViewWindow : Window
{
    private readonly MediaView? _view;
    private readonly ScreenViewer? _viewer;
    private readonly string _name;
    private WriteableBitmap? _bitmap;
    private long _shown;
    private int _pending;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ScreenViewWindow() => InitializeComponent();

    public ScreenViewWindow(string name, MediaView view, string ffmpeg) : this()
    {
        _name = name;
        _view = view;
        Title = name;
        _viewer = new ScreenViewer(view, ffmpeg);
        _viewer.PictureReceived += OnPicture;
        _viewer.Failed += ex => Dispatcher.UIThread.Post(() => { Waiting.IsVisible = true; Waiting.Text = "The picture stopped: " + ex.Message; });
        _draw.Tick += (_, _) => DrawTick();
        _timer.Tick += (_, _) => UpdateInfo();
        _timer.Start();
        Closed += (_, _) => Stop();
    }

    /// <summary>Raised when the window is closed, so the owner can mark the share as not watched any more.</summary>
    public event Action? Stopped;

    // Over the internet pictures arrive unevenly: a few close together, then a gap. Drawn as they come, that looks like
    // stutter. They wait in a short queue and are drawn on a steady clock set by how fast they arrive on average. The
    // queue holds only a few pictures (about a tenth of a second), and if it grows past that the oldest are dropped
    // so the delay does not build up.
    private const int TargetQueue = 2, MaxQueue = 5;
    private const double MinIntervalMs = 15, MaxIntervalMs = 200;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Picture> _queue = new();
    private readonly DispatcherTimer _draw = new() { Interval = TimeSpan.FromMilliseconds(4) };
    private double _intervalMs = 33;
    private long _lastArrival, _nextDue, _firstQueued;
    private bool _playing;
    private long _statsAt = Environment.TickCount64, _statsShown, _statsFrames, _statsBytes;
    private long _late;   // pictures that arrived too bunched up to be shown in time and were skipped

    private void OnPicture(Picture picture)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Exchange(ref _lastArrival, now);
        if (previous != 0)
        {
            // Smoothed over many pictures, so one late arrival does not slow the clock.
            double gap = Math.Clamp(now - previous, MinIntervalMs, MaxIntervalMs);
            _intervalMs += (gap - _intervalMs) * 0.05;
        }
        if (_queue.IsEmpty) Volatile.Write(ref _firstQueued, now);
        _queue.Enqueue(picture);
        while (_queue.Count > MaxQueue && _queue.TryDequeue(out var old)) { Interlocked.Increment(ref _late); _viewer?.Return(old); }
        if (Interlocked.Exchange(ref _pending, 1) == 0) Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _pending, 0); if (!_draw.IsEnabled) _draw.Start(); });
    }

    private void DrawTick()
    {
        long now = Environment.TickCount64;
        if (_queue.IsEmpty) { _playing = false; _draw.Stop(); return; }
        if (!_playing)
        {
            // Start once a couple of pictures are waiting, or after a short wait if they come slowly.
            if (_queue.Count < TargetQueue && now - Volatile.Read(ref _firstQueued) < 120) return;
            _playing = true;
            _nextDue = now;
        }
        if (now < _nextDue) return;
        if (!_queue.TryDequeue(out var picture)) return;
        _nextDue = Math.Max(_nextDue + (long)_intervalMs, now - (long)_intervalMs);   // a late draw does not make a burst afterwards
        // More waiting than the target means the clock is behind; show the newest of them instead of falling further back.
        while (_queue.Count > TargetQueue + 1 && _queue.TryDequeue(out var skipped)) { Interlocked.Increment(ref _late); _viewer?.Return(picture); picture = skipped; }
        Show(picture);
        _viewer?.Return(picture);
    }

    private void Show(Picture picture)
    {
        if (_bitmap == null || _bitmap.PixelSize.Width != picture.Width || _bitmap.PixelSize.Height != picture.Height)
        {
            _bitmap = new WriteableBitmap(new PixelSize(picture.Width, picture.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            Picture.Source = _bitmap;
        }
        using (var fb = _bitmap.Lock())
        {
            int row = picture.Width * 4;
            if (fb.RowBytes == row) Marshal.Copy(picture.Bgra, 0, fb.Address, row * picture.Height);
            else
                for (int y = 0; y < picture.Height; y++)
                    Marshal.Copy(picture.Bgra, y * row, fb.Address + y * fb.RowBytes, row);
        }
        Picture.InvalidateVisual();
        Waiting.IsVisible = false;
        _shown++;
    }

    private void UpdateInfo()
    {
        if (_view == null) return;
        var st = _view.Status;
        // "Lost" frames could not be put together even after asking for the missing packets again; "recovered" packets were
        // lost on the way and then arrived on the second try. A rising "lost" is what freezes the picture.
        long now = Environment.TickCount64, shown = _shown, bytes = _viewer?.BytesIn ?? 0, frames = _viewer?.FramesIn ?? 0;
        double seconds = Math.Max(0.1, (now - _statsAt) / 1000.0);
        double shownFps = (shown - _statsShown) / seconds, arriveFps = (frames - _statsFrames) / seconds, kbps = (bytes - _statsBytes) * 8 / 1000.0 / seconds;
        (_statsAt, _statsShown, _statsFrames, _statsBytes) = (now, shown, frames, bytes);
        // Delay added by the playout queue: what waits to be drawn, at the pace they arrive.
        int delayMs = (int)(_queue.Count * _intervalMs);
        Info.Text = $"{_name}  -  {_shown} pictures, {_view.FramesLost} lost, {st.Recovered} packets recovered, {Interlocked.Read(ref _late)} late"
            + $"\nArriving {arriveFps:0.0}/s at {kbps:0} kbit/s, drawn {shownFps:0.0}/s, decoder queue {_viewer?.DecodeQueue ?? 0}, playout delay {delayMs} ms"
            + (st.Attached ? "" : "  -  connecting to the sharer...");
    }

    private void Stop()
    {
        _timer.Stop();
        _draw.Stop();
        _viewer?.Dispose();
        _view?.Dispose();
        Stopped?.Invoke();
    }
}
