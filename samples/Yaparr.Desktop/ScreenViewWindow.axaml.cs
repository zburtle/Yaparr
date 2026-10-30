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
        _timer.Tick += (_, _) => UpdateInfo();
        _timer.Start();
        Closed += (_, _) => Stop();
    }

    /// <summary>Raised when the window is closed, so the owner can mark the share as not watched any more.</summary>
    public event Action? Stopped;

    private void OnPicture(Picture picture)
    {
        // Pictures arrive faster than they can be drawn only if the window is busy; draw the newest and skip the rest.
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            Show(picture);
        });
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
        Info.Text = $"{_name}  -  {_shown} pictures, {_view.FramesLost} lost" + (st.Attached ? "" : "  -  connecting to the sharer...");
    }

    private void Stop()
    {
        _timer.Stop();
        _viewer?.Dispose();
        _view?.Dispose();
        Stopped?.Invoke();
    }
}
