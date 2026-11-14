using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Yaparr.Screen;

namespace Yaparr.Desktop;

/// <summary>
/// What the people watching your share see: the frames that are being sent, decoded again here, shrunk, and shown above the sharing panel, at
/// <see cref="Size"/> (about 15 pictures a second by default; with 1 or 2 only key frames are decoded, which is much cheaper). The first version
/// decoded every frame in software and drew them at full size, and cost more than the whole share (measured: this program went from 138% to
/// 10% of a core with the preview hidden). It only runs while it is shown.
/// Sharing a whole monitor with the preview showing on it makes the picture show itself, so for a monitor it starts closed.
/// </summary>
public sealed class SharePreviewView : UserControl
{
    private readonly TextBlock _title = new() { Text = "What viewers see", FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _toggle = new() { Padding = new Thickness(8, 2), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _picture = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _waiting = new() { Foreground = Brushes.Gray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(8) };
    private readonly Border _body = new() { Background = Brushes.Black, CornerRadius = new CornerRadius(6), MinHeight = 90, MaxHeight = 150, ClipToBounds = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _note = new() { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };

    private ScreenSharer? _sharer;
    private volatile LocalFrames? _frames;
    private ScreenViewer? _viewer;
    private WriteableBitmap? _bitmap;
    private Picture? _latest;
    private int _pending;
    private bool _expanded;
    private bool _wholeMonitor;
    private TimeSpan _decoderCpuDone;

    /// <summary>
    /// The picture shown is no larger than this (the box it is shown in is 150 high), and no more of them a second than this; scaling down is done by
    /// the decoder. YAPARR_PREVIEW_FPS changes the rate (1 shows key frames only), to find what suits.
    /// </summary>
    internal static readonly PreviewSize Size = new(640, 360, Environment.GetEnvironmentVariable("YAPARR_PREVIEW_FPS") is { } fps && int.TryParse(fps, out int n) && n > 0 ? Math.Min(n, 60) : 15);

    /// <summary>The processor time the preview's decoder has spent since the share began (it decodes in this program, so this is part of the program's own figure).</summary>
    public TimeSpan DecoderCpu => _decoderCpuDone + (_viewer?.DecoderCpu ?? default);

    private string? _decoderKind;

    /// <summary>How the preview is decoding (on the card through Direct3D 12 or 11, or on the processor, and what did not work), kept after it closes; null until it has run.</summary>
    public string? DecoderKind => _viewer?.DecoderKind ?? _decoderKind;

    public SharePreviewView()
    {
        var header = new DockPanel();
        DockPanel.SetDock(_toggle, Dock.Right);
        header.Children.Add(_toggle);
        header.Children.Add(_title);
        ToolTip.SetTip(header, Size.KeyFramesOnly
            ? "Shows each key frame of what is being sent, about once a second, shrunk. It is not live video."
            : $"Shows what is being sent, shrunk, at about {Size.Fps} pictures a second. It decodes the stream a second time, which costs some processor time while it is open.");
        _body.Child = new Grid { Children = { _picture, _waiting } };
        Content = new StackPanel { Children = { header, _body, _note } };
        _toggle.Click += (_, _) => SetExpanded(!_expanded);
        IsVisible = false;
    }

    /// <summary>Starts following a share. <paramref name="wholeMonitor"/>: the picture would show itself, so the preview starts closed.</summary>
    public void Attach(ScreenSharer sharer, bool wholeMonitor)
    {
        Detach();
        _sharer = sharer;
        _decoderCpuDone = default;
        _decoderKind = null;
        sharer.FrameEncoded += OnEncoded;
        IsVisible = true;
        SetKind(wholeMonitor);
        SetExpanded(!wholeMonitor);
    }

    /// <summary>What is shared changed (a window or a whole monitor): a monitor closes the preview, since it would show itself.</summary>
    public void SetKind(bool wholeMonitor)
    {
        _wholeMonitor = wholeMonitor;
        _note.Text = "You are sharing a whole monitor, so the preview would show itself inside the picture. Open it only to check quality, and close it again.";
        _note.IsVisible = wholeMonitor && !_expanded;
        if (wholeMonitor && _expanded) SetExpanded(false);
    }

    public void Detach()
    {
        if (_sharer != null) _sharer.FrameEncoded -= OnEncoded;
        _sharer = null;
        Close();
        IsVisible = false;
    }

    // Not in the room being looked at (or hidden some other way): no decoder is kept running for a picture nobody sees.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || _sharer == null) return;
        if (IsVisible && _expanded) Open(); else if (!IsVisible) Close();
    }

    private void SetExpanded(bool open)
    {
        _expanded = open;
        _toggle.Content = open ? "Hide" : "Show";
        _body.IsVisible = open;
        _note.IsVisible = _wholeMonitor && !open;
        if (open) Open(); else Close();
    }

    private void Open()
    {
        if (_viewer != null || _sharer == null) return;
        _waiting.Text = "Waiting for the next key frame…";
        _waiting.IsVisible = true;
        try
        {
            var frames = new LocalFrames();
            var viewer = new ScreenViewer(frames, preview: Size);
            viewer.PictureReceived += OnPicture;
            viewer.Failed += ex => Dispatcher.UIThread.Post(() => { _waiting.Text = "The preview stopped: " + ex.Message; _waiting.IsVisible = true; });
            _viewer = viewer;
            _frames = frames;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _waiting.Text = "The preview could not start: " + ex.Message;
        }
    }

    private void Close()
    {
        _frames = null;
        var viewer = _viewer;
        _viewer = null;
        if (viewer != null) { viewer.PictureReceived -= OnPicture; _decoderKind = viewer.DecoderKind; viewer.Dispose(); _decoderCpuDone += viewer.DecoderCpu; }
        Interlocked.Exchange(ref _latest, null);
        _bitmap = null;
        _picture.Source = null;
    }

    // On the thread that sends the share: cheap, and nothing at all when the preview is closed.
    private void OnEncoded(byte[] frame, bool isKey) => _frames?.Push(frame, isKey);

    // On the decoder's thread: keep only the newest picture and draw it on the interface's thread.
    private void OnPicture(Picture picture)
    {
        var old = Interlocked.Exchange(ref _latest, picture);
        if (old != null) _viewer?.Return(old);
        if (Interlocked.Exchange(ref _pending, 1) == 0) Dispatcher.UIThread.Post(Draw);
    }

    private void Draw()
    {
        Interlocked.Exchange(ref _pending, 0);
        var picture = Interlocked.Exchange(ref _latest, null);
        if (picture == null) return;
        if (_viewer == null) return;
        if (_bitmap == null || _bitmap.PixelSize.Width != picture.Width || _bitmap.PixelSize.Height != picture.Height)
        {
            _bitmap = new WriteableBitmap(new PixelSize(picture.Width, picture.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _picture.Source = _bitmap;
        }
        using (var fb = _bitmap.Lock())
        {
            int row = picture.Width * 4;
            if (fb.RowBytes == row) Marshal.Copy(picture.Bgra, 0, fb.Address, row * picture.Height);
            else
                for (int y = 0; y < picture.Height; y++)
                    Marshal.Copy(picture.Bgra, y * row, fb.Address + y * fb.RowBytes, row);
        }
        _picture.InvalidateVisual();
        _waiting.IsVisible = false;
        _viewer?.Return(picture);
    }
}
