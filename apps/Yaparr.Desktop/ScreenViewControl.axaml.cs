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
        FullScreenButton.Click += (_, _) => FullScreenToggleRequested?.Invoke();
        Picture.DoubleTapped += (_, _) => { _pauseClick.Stop(); FullScreenToggleRequested?.Invoke(); };
        Waiting.DoubleTapped += (_, _) => { _pauseClick.Stop(); FullScreenToggleRequested?.Invoke(); };
        // A click on the picture pauses the film (for the one who plays it), after a moment: a double-click, which is full screen, must not pause it twice on the way.
        _pauseClick.Tick += (_, _) => { _pauseClick.Stop(); PauseToggleRequested?.Invoke(); };
        Picture.Tapped += (_, _) => { if (PauseToggleRequested != null) { _pauseClick.Stop(); _pauseClick.Start(); } };
        _hideControls.Tick += (_, _) => HideControlsWhileIdle();
        PointerMoved += (_, _) => ShowControlsForAWhile();
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

    /// <summary>
    /// The sharer's own view of a film played into the room: the frames that are being sent, decoded here (nothing is received, so there are no figures of a watch, and no other stream to move to).
    /// </summary>
    public ScreenViewControl(string name, IMediaFrameSource frames) : this()
    {
        _name = name;
        _viewer = new ScreenViewer(frames);
        StopButton.Content = "Stop Stream";   // there is no setting it aside: closing this view ends the film
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

    private LocalSound? _localSound;

    /// <summary>The line at the bottom left, which says what is playing when the player is shown (the figures of a watch are not kept for the sharer's own view).</summary>
    public TextBlock CaptionBlock => Info;

    /// <summary>
    /// Takes the sound controls out of the bar of buttons, to be put where the caller wants them (beside the player's buttons). The slider takes what room there is, between 40 and 120 wide,
    /// so that it does not run over the buttons in a narrow window.
    /// </summary>
    public Control TakeSoundPanel()
    {
        (SoundPanel.Parent as Panel)?.Children.Remove(SoundPanel);
        var label = SoundPanel.Children.OfType<TextBlock>().First();
        SoundPanel.Children.Clear();
        SoundBox.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        SoundBox.Width = 120;
        label.Margin = new Thickness(0, 0, 6, 0);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), IsVisible = SoundPanel.IsVisible, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(SoundBox, 1);
        grid.Children.Add(label);
        grid.Children.Add(SoundBox);
        return grid;
    }

    /// <summary>The player of the film the sharer plays (pause, jump, next episode), shown above the buttons like a video player's.</summary>
    public void AttachPlayer(Control player)
    {
        PlayerHost.Child = player;
        PlayerHost.IsVisible = true;
    }

    /// <summary>The film's sound, which the sharer hears here (the volume of the panel is its volume).</summary>
    public void AttachLocalSound(LocalSound sound, string? output)
    {
        _localSound = sound;
        try
        {
            _speaker = VoiceAudio.PlayLocal(sound, output);
            SoundPanel.IsVisible = true;
            SoundBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) sound.Gain = (float)SoundBox.Value / 100f;
            };
        }
        catch (Exception) { _localSound = null; }
    }

    private readonly DispatcherTimer _pauseClick = new() { Interval = TimeSpan.FromMilliseconds(300) };

    /// <summary>
    /// Pause or resume was asked for (the space bar, or a click on the picture). Only the view of a film you play has anyone to hear it: for a film someone else plays it is
    /// not set, and neither key nor click does anything.
    /// </summary>
    public event Action? PauseToggleRequested;

    /// <summary>Whether this is the view of a film you play, which the space bar and a click on the picture pause.</summary>
    public bool CanTogglePause => PauseToggleRequested != null;

    public void RequestPauseToggle() => PauseToggleRequested?.Invoke();

    /// <summary>Shows the Change… button among the others (Full screen, Pop out, Stop watching), for the film you play: it picks something else to play now.</summary>
    public void ShowChangeButton(Action changed)
    {
        ChangeButton.IsVisible = true;
        ChangeButton.Click += (_, _) => changed();
    }

    /// <summary>The full screen button, a double-click on the picture or F11 asks for full screen, or for leaving it.</summary>
    public event Action? FullScreenToggleRequested;

    /// <summary>Asks for full screen, or for leaving it, as the button does (the window it is in calls this for F11).</summary>
    public void RequestFullScreenToggle() => FullScreenToggleRequested?.Invoke();

    // In full screen the controls under the picture go after a few seconds without the mouse moving, and the pointer with them, so that only the picture is
    // seen; moving the mouse brings them back.
    private readonly DispatcherTimer _hideControls = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _fullScreen;

    /// <summary>Whether the picture fills the screen; set by the window it is in, whoever changed it (the button, a key, or the desktop).</summary>
    public bool IsFullScreen
    {
        get => _fullScreen;
        set
        {
            _fullScreen = value;
            FullScreenButton.Content = value ? "Exit full screen" : "Full screen";
            DockButton.IsVisible = !value;
            ShowControlsForAWhile();
            if (!value) _hideControls.Stop();
        }
    }

    private void ShowControlsForAWhile()
    {
        Controls.IsVisible = true;
        Cursor = null;
        _hideControls.Stop();
        if (_fullScreen) _hideControls.Start();
    }

    private void HideControlsWhileIdle()
    {
        _hideControls.Stop();
        if (!_fullScreen || Controls.IsPointerOver) return;
        Controls.IsVisible = false;
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.None);
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

    private MediaView? _statusView;
    private long _filmStatusAt;

    /// <summary>
    /// Shows what a film played into the room says about itself (what it is, how far it is, whether it is paused) over the picture. The status comes once a second; when it stops coming
    /// (the film is over, or the sharer went away) what it showed goes after a few seconds.
    /// </summary>
    public void AttachFilmStatus(MediaView statusView)
    {
        _statusView = statusView;
        statusView.Received += packet =>
        {
            if (!FilmStatus.TryDecode(packet.Payload.Span, out var status)) return;
            Dispatcher.UIThread.Post(() => ShowFilm(status));
        };
    }

    private void ShowFilm(FilmStatus status)
    {
        if (_released) return;
        _filmStatusAt = Environment.TickCount64;
        FilmTitle.Text = status.Title;
        string Clock(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
        FilmTimes.Text = status.Duration > TimeSpan.Zero ? Clock(status.Position) + " / " + Clock(status.Duration) : Clock(status.Position);
        FilmProgress.IsVisible = status.Duration > TimeSpan.Zero;
        FilmProgress.Value = status.Duration > TimeSpan.Zero ? Math.Clamp(status.Position.TotalSeconds / status.Duration.TotalSeconds * 1000, 0, 1000) : 0;
        FilmBar.IsVisible = true;
        FilmPaused.IsVisible = status.Paused;
    }

    private void HideStaleFilmStatus()
    {
        if (_filmStatusAt == 0 || Environment.TickCount64 - _filmStatusAt < 6000) return;
        _filmStatusAt = 0;
        FilmBar.IsVisible = false;
        FilmPaused.IsVisible = false;
    }

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

    // The sharer's own film sound is held back only by a small buffer. It cannot be none: the speakers ask for sound in short pieces and the decoder delivers it in pieces of its own, so
    // with nothing held back the sound runs dry between them and stutters. YAPARR_FILM_SOUND_DELAY sets the delay in ms, to find what suits.
    private const int SoundBufferMs = 100;
    private static readonly int? SoundDelayOverride = int.TryParse(Environment.GetEnvironmentVariable("YAPARR_FILM_SOUND_DELAY"), out int ms) ? ms : null;

    private void UpdateInfo()
    {
        if (_localSound != null) _localSound.DelayMs = SoundDelayOverride ?? SoundBufferMs;
        HideStaleFilmStatus();
        if (_view == null) return;
        var st = _view.Status;
        // "Lost" frames could not be put together even after asking for the missing packets again; "recovered" packets were
        // lost on the way and then arrived on the second try. A rising "lost" is what freezes the picture.
        long now = Environment.TickCount64, shown = _shown, bytes = _viewer?.BytesIn ?? 0, frames = _viewer?.FramesIn ?? 0;
        double seconds = Math.Max(0.1, (now - _statsAt) / 1000.0);
        double shownFps = (shown - _statsShown) / seconds, arriveFps = (frames - _statsFrames) / seconds, kbps = (bytes - _statsBytes) * 8 / 1000.0 / seconds;
        (_statsAt, _statsShown, _statsFrames, _statsBytes) = (now, shown, frames, bytes);
        _quality.Update(now, new ViewerCounters(shown, _view.FramesLost, _view.FramesMissed, _playout.Late, _viewer?.Dropped ?? 0, st.GaveUp, st.PacketsSeen));
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
        _statusView?.Dispose();
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
