using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Screen;
using Yaparr.Voice;

namespace Yaparr.Desktop;

/// <summary>One shared screen, with its sound and a line of figures. It sits inside the main window or in a <see cref="ScreenViewWindow"/>, and moves between them without the stream restarting. Stopping it ends the watching.</summary>
public partial class ScreenViewControl : UserControl
{
    private MediaView? _view;
    private ScreenViewer? _viewer;
    private readonly string _name = "";
    private WriteableBitmap? _bitmap;
    private long _shown;
    private int _pending;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly long _startedAt = Environment.TickCount64;

    public ScreenViewControl()
    {
        InitializeComponent();
        _playout = new PlayoutQueue<Picture>(picture => _viewer?.Return(picture));
    }

    /// <summary>What this watch has done so far, counted from when it began (null if there is no stream).</summary>
    public WatchFigures? Figures()
    {
        if (_view == null || _released) return null;
        var st = _view.Status;
        return new WatchFigures(_name, _startedAt, _shown, _viewer?.FramesIn ?? 0, _viewer?.BytesIn ?? 0, _view.FramesLost, st.Recovered, _playout.Late,
            _viewer?.Dropped ?? 0, _viewer?.DecodeQueue ?? 0, st.Attached, st.Parent, _view.FramesMissed, _viewer?.PictureKind,
            st.PacketsSeen, st.GapsNoticed, st.BigGaps, st.ArrivedLate, st.RepairedAfterAsking, st.NacksSent, st.PacketsAsked, st.GaveUp, _quality.Sample, st.Duplicates);
    }

    /// <summary>How the watch went, written when it ends (for the sharing diagnostics, which are often copied after the screen is closed).</summary>
    public string? LastReport { get; private set; }

    /// <summary>
    /// Watches <paramref name="view"/>, the share's main stream. With <paramref name="openLow"/> and <paramref name="openMain"/> (and <paramref name="lowAvailable"/> saying
    /// whether the share has a lower stream at the moment) the view moves between the two on its own (see <see cref="StreamChoice"/>); nothing is asked of the viewer,
    /// and a notice over the picture says when, and why, it shows the lower-quality stream.
    /// </summary>
    public ScreenViewControl(string name, MediaView view, Func<MediaView?>? openLow = null, Func<MediaView>? openMain = null, Func<bool>? lowAvailable = null) : this()
    {
        _name = name;
        _view = view;
        _openLow = openLow;
        _openMain = openMain;
        _lowAvailable = lowAvailable;
        _viewer = new ScreenViewer(view);
        // How this picture is doing, judged over ten seconds and sent up the tree with the others' (see ViewerQualityMeter), so that the sharer can tell
        // a room that struggles from one viewer that does.
        view.QualityProvider = () => _quality.Sample;
        _viewer.PictureReceived += OnPicture;
        _viewer.Failed += ex =>
        {
            DecoderFailed?.Invoke(ex.Message);
            Dispatcher.UIThread.Post(() => { Waiting.IsVisible = true; Waiting.Text = "The picture stopped: " + ex.Message; });
        };
        _draw.Tick += (_, _) => DrawTick();
        _timer.Tick += (_, _) => UpdateInfo();
        _timer.Start();
        DockButton.Click += (_, _) => DockToggleRequested?.Invoke();
        StopButton.Click += (_, _) => StopRequested?.Invoke();
        Docked = true;
    }

    /// <summary>The name of the share, for a window title.</summary>
    public string ShareName => _name;

    /// <summary>The button asks for the picture to be moved between the main window and a window of its own.</summary>
    public event Action? DockToggleRequested, StopRequested;

    /// <summary>The decoder stopped for a reason other than being closed; the text is what FFmpeg said.</summary>
    public event Action<string>? DecoderFailed;

    /// <summary>Whether the picture is inside the main window (otherwise it is in a window of its own); sets what the button says.</summary>
    public bool Docked
    {
        get => _docked;
        set { _docked = value; DockButton.Content = value ? "Pop out" : "Show in the app"; }
    }

    private bool _docked;

    /// <summary>The picture stopped coming because the connection to the sharer (or whoever passes it on) was lost, and again when it is back.</summary>
    public event Action? ConnectionLost, ConnectionRestored;

    private bool _wasAttached, _connectionLost;

    /// <summary>
    /// The picture stopped for a couple of seconds although the watch is still going (the sharer restarted, a relay hiccuped, packets were lost): a notice is shown over the
    /// last picture until pictures come again. The seconds are how long it has been stopped (when it was noticed), and how long it was (when it is over).
    /// </summary>
    public event Action<double>? StreamInterrupted, StreamResumed;

    private readonly StreamInterruptionWatch _interruptions = new();

    /// <summary>
    /// This viewer is the only one in trouble, and the room is fine (see <see cref="LoneStrugglerWatch"/>): true when the notice is shown, false when it goes. It is only said
    /// when it is certain, so a small room, a sharer with trouble of its own, or a room whose state has not come say nothing.
    /// </summary>
    public event Action<bool>? OnlyOneStrugglingChanged;

    private readonly LoneStrugglerWatch _lone = new();
    private bool _saidLone;

    /// <summary>Raised when watching ends, so the owner can mark the share as not watched any more.</summary>
    public event Action? Stopped;

    private MediaView? _soundView;
    private StreamAudioReceiver? _sound;
    private IDisposable? _speaker;

    /// <summary>Plays the sound that goes with the picture, on the chosen speakers. Failing to open them leaves the picture without sound.</summary>
    public void AttachSound(MediaView soundView, string? output)
    {
        _soundView = soundView;
        try
        {
            _sound = new StreamAudioReceiver(soundView);
            _speaker = VoiceAudio.PlayStream(_sound, output);
            SoundPanel.IsVisible = true;
            SoundBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && _sound != null) _sound.Gain = (float)SoundBox.Value / 100f;
            };
        }
        catch (Exception)
        {
            _sound?.Dispose(); _sound = null;
            _soundView.Dispose(); _soundView = null;
        }
    }

    // Over the internet pictures arrive unevenly: a few close together, then a gap. Drawn as they come, that looks like stutter. They wait in a
    // short queue and are drawn on a steady clock set by how fast they arrive on average (see PlayoutQueue).
    private readonly PlayoutQueue<Picture> _playout;
    private readonly DispatcherTimer _draw = new() { Interval = TimeSpan.FromMilliseconds(4) };
    private long _statsAt = Environment.TickCount64, _statsShown, _statsFrames, _statsBytes;
    private ViewerQualityMeter _quality = new();

    private void OnPicture(Picture picture)
    {
        _playout.Arrive(picture, Environment.TickCount64);
        if (Interlocked.Exchange(ref _pending, 1) == 0) Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _pending, 0); if (!_draw.IsEnabled) _draw.Start(); });
    }

    private void DrawTick()
    {
        if (IsEnded || _playout.IsEmpty) { _playout.Reset(); _draw.Stop(); return; }
        if (!_playout.TryDraw(Environment.TickCount64, out var picture)) return;
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
        var (change, seconds) = _interruptions.PictureShown(Environment.TickCount64);
        if (change == InterruptionChange.Resumed) { Interrupted.IsVisible = false; StreamResumed?.Invoke(seconds); }
    }

    // ---- the lower stream (issue #254) -------------------------------------------------------------------------------------------------
    //
    // A move is made without a gap: the other stream is watched and decoded beside the one shown, and when its first picture comes the old one is let go and the
    // new one shown from then on. A stream whose first picture does not come within ten seconds is given up, and the one shown stays.

    private readonly Func<MediaView?>? _openLow;
    private readonly Func<MediaView>? _openMain;
    private readonly Func<bool>? _lowAvailable;
    private readonly StreamChoice _choice = new();
    private WatchedStream _watched = WatchedStream.Main;
    private (WatchedStream To, MediaView View, ScreenViewer Viewer, long StartedAt)? _switching;
    private readonly object _switchGate = new();

    /// <summary>Which stream is shown now.</summary>
    public WatchedStream Watched => _watched;

    /// <summary>The picture moved to the other stream: which one now, and why (for the log). On the UI thread.</summary>
    public event Action<WatchedStream, string>? StreamChanged;

    private bool LowAvailable => _lowAvailable?.Invoke() ?? false;

    private void ConsiderSwitch(long now)
    {
        if (_view == null || _released || IsEnded || _openLow == null || _openMain == null) return;
        bool available = LowAvailable;
        lock (_switchGate)
        {
            if (_switching is { } going)
            {
                if (now - going.StartedAt < 10_000) return;
                _switching = null;   // its first picture never came: given up, the one shown stays
                going.Viewer.Dispose();
                going.View.Dispose();
            }
        }
        var wanted = _choice.Update(now, available, _watched, _quality.Sample, _saidLone);
        if (wanted != _watched) BeginSwitch(wanted);
    }

    private void BeginSwitch(WatchedStream to)
    {
        MediaView? view;
        try { view = to == WatchedStream.Low ? _openLow!() : _openMain!(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return; }
        if (view == null) return;
        var viewer = new ScreenViewer(view);
        lock (_switchGate) _switching = (to, view, viewer, Environment.TickCount64);
        viewer.PictureReceived += picture =>
        {
            bool first;
            lock (_switchGate) first = _switching is { } s && s.Viewer == viewer;
            if (first)
            {
                viewer.Return(picture);
                Dispatcher.UIThread.Post(() => CompleteSwitch(viewer));
                return;
            }
            if (_viewer == viewer) OnPicture(picture); else viewer.Return(picture);
        };
    }

    private void CompleteSwitch(ScreenViewer viewer)
    {
        (WatchedStream To, MediaView View, ScreenViewer Viewer, long StartedAt) going;
        lock (_switchGate)
        {
            if (_switching is not { } s || s.Viewer != viewer) return;
            going = s;
            _switching = null;
        }
        if (_released || IsEnded) { going.Viewer.Dispose(); going.View.Dispose(); return; }
        var (oldViewer, oldView) = (_viewer, _view);
        if (oldViewer != null) oldViewer.PictureReceived -= OnPicture;
        _viewer = going.Viewer;
        _view = going.View;
        _quality = new ViewerQualityMeter();   // the new stream is judged afresh
        _view.QualityProvider = () => _quality.Sample;
        _watched = going.To;
        oldViewer?.Dispose();
        oldView?.Dispose();
        string reason = _choice.Reason ?? "";
        LowNoticeText.Text = $"Watching a lower-quality stream: {reason}";
        LowNotice.IsVisible = _watched == WatchedStream.Low;
        if (_watched == WatchedStream.Low) Struggling.IsVisible = false;   // the notice about the link says what was done about it
        StreamChanged?.Invoke(_watched, reason);
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
        _quality.Update(now, new ViewerCounters(shown, _view.FramesLost, _view.FramesMissed, _playout.Late, _viewer?.Dropped ?? 0, st.GaveUp));
        // Delay added by the playout queue: what waits to be drawn, at the pace they arrive.
        int delayMs = (int)(_playout.Count * _playout.IntervalMs);
        // The stream's own check: a parent that has not been heard from for a few seconds is let go, and it is looked for again.
        if (st.Attached)
        {
            _wasAttached = true;
            if (_connectionLost) { _connectionLost = false; Waiting.IsVisible = _shown == 0; ConnectionRestored?.Invoke(); }
        }
        else if (_wasAttached && !_connectionLost)
        {
            _connectionLost = true;
            Waiting.Text = "Lost the connection to the screen. Reconnecting…";
            Waiting.IsVisible = true;
            ConnectionLost?.Invoke();
        }
        // The picture stopped while the connection is still there (a lost connection has its own notice, and an ended stream needs none).
        var (interruption, stoppedFor) = _interruptions.Tick(now, suppressed: _connectionLost || IsEnded);
        if (interruption == InterruptionChange.Interrupted) { Interrupted.IsVisible = true; StreamInterrupted?.Invoke(stoppedFor); }
        else if (interruption == InterruptionChange.Cleared) Interrupted.IsVisible = false;
        // The only one in trouble: the room's state came down the tree from the sharer, and this viewer's own judgement is the other half.
        bool lone = _watched == WatchedStream.Main && !_connectionLost && !IsEnded && _lone.Update(now, _quality.Sample, _view.RoomState);
        if (lone != _saidLone) { _saidLone = lone; Struggling.IsVisible = lone; OnlyOneStrugglingChanged?.Invoke(lone); }
        // A small figure over the picture: how many pictures a second are really drawn.
        FpsBadge.IsVisible = _shown > 0 && !_connectionLost;
        FpsText.Text = $"{shownFps:0} fps";
        ConsiderSwitch(now);
        Info.Text = $"{_name}{(_watched == WatchedStream.Low ? " (lower stream)" : "")}  -  {_shown} pictures, {_view.FramesLost} lost, {st.Recovered} packets recovered, {_playout.Late} late"
            + $"\nArriving {arriveFps:0.0}/s at {kbps:0} kbit/s, drawn {shownFps:0.0}/s, decoder queue {_viewer?.DecodeQueue ?? 0}, playout delay {delayMs} ms"
            + (st.Attached ? "" : "  -  connecting to the sharer...");
    }

    private bool _stopped, _released;

    /// <summary>Whether the sharer stopped and this shows only the notice that it has ended.</summary>
    public bool IsEnded { get; private set; }

    /// <summary>
    /// The sharer stopped. The stream, the decoder and the sound are released, and the picture area goes black with a notice, to be closed by the person
    /// (rather than the window vanishing under them). <see cref="Stopped"/> is raised when that happens.
    /// </summary>
    public void ShowEnded()
    {
        if (_stopped || IsEnded) return;
        IsEnded = true;
        Release();
        Picture.Source = null;
        _bitmap = null;
        FpsBadge.IsVisible = false;
        Interrupted.IsVisible = false;
        Struggling.IsVisible = false;
        Waiting.Text = "This stream has ended";
        Waiting.FontSize = 22;
        Waiting.Foreground = Avalonia.Media.Brushes.White;
        Waiting.IsVisible = true;
        SoundPanel.IsVisible = false;
        Info.Text = _name + "  -  ended";
        StopButton.Content = "Close";
    }

    // The stream, the decoder, the sound and the timers; once. What the watch did is written down first, while the figures can still be read.
    private void Release()
    {
        if (_released) return;
        try { LastReport = Figures()?.Describe(Environment.TickCount64); }
        catch (ObjectDisposedException) { }
        _released = true;
        _timer.Stop();
        _draw.Stop();
        _viewer?.Dispose();
        _view?.Dispose();
        lock (_switchGate)
        {
            if (_switching is { } going) { going.Viewer.Dispose(); going.View.Dispose(); }
            _switching = null;
        }
        _speaker?.Dispose();
        _sound?.Dispose();
        _soundView?.Dispose();
    }

    /// <summary>Ends the watching: the stream, the decoder and the sound are released. Safe to call more than once.</summary>
    public void StopWatching()
    {
        if (_stopped) return;
        _stopped = true;
        Release();
        Stopped?.Invoke();
    }
}
