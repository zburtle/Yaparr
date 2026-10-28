using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace Yaparr.Desktop;

/// <summary>
/// Shows a picture from its bytes, with a tiny preview until the real one has loaded. GIFs are played frame by frame
/// while the control is on screen; Avalonia itself only draws the first frame.
/// </summary>
public sealed class PictureView : Control
{
    private const double MaxShownWidth = 320, MaxShownHeight = 260;
    private const int MaxAnimatedPixels = 1_500_000;

    public static readonly StyledProperty<byte[]?> DataProperty = AvaloniaProperty.Register<PictureView, byte[]?>(nameof(Data));
    public static readonly StyledProperty<byte[]?> ThumbnailProperty = AvaloniaProperty.Register<PictureView, byte[]?>(nameof(Thumbnail));
    public static readonly StyledProperty<int> PixelWidthProperty = AvaloniaProperty.Register<PictureView, int>(nameof(PixelWidth));
    public static readonly StyledProperty<int> PixelHeightProperty = AvaloniaProperty.Register<PictureView, int>(nameof(PixelHeight));

    private int _version;
    private IImage? _thumb, _full;
    private SKCodec? _codec;
    private SKBitmap? _buffer;
    private SKImageInfo _info;
    private SKCodecFrameInfo[] _frames = Array.Empty<SKCodecFrameInfo>();
    private WriteableBitmap? _frameBitmap;
    private int _frame, _bufferFrame = -1;
    private DispatcherTimer? _timer;

    static PictureView()
    {
        AffectsMeasure<PictureView>(PixelWidthProperty, PixelHeightProperty);
        AffectsRender<PictureView>(DataProperty, ThumbnailProperty);
    }

    public byte[]? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public byte[]? Thumbnail { get => GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }
    public int PixelWidth { get => GetValue(PixelWidthProperty); set => SetValue(PixelWidthProperty, value); }
    public int PixelHeight { get => GetValue(PixelHeightProperty); set => SetValue(PixelHeightProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty) Reload();
        else if (change.Property == ThumbnailProperty) LoadThumbnail();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_thumb == null) LoadThumbnail();
        StartAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }

    private void LoadThumbnail()
    {
        _thumb = null;
        if (Thumbnail is { Length: > 0 } bytes)
        {
            try { _thumb = new Bitmap(new MemoryStream(bytes)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { }
        }
        InvalidateVisual();
    }

    private void Reload()
    {
        _timer?.Stop();
        _timer = null;
        _codec?.Dispose();
        _codec = null;
        _buffer?.Dispose();
        _buffer = null;
        _frameBitmap = null;
        _full = null;
        _bufferFrame = -1;
        _frame = 0;
        InvalidateVisual();
        if (Data is { Length: > 0 } data) _ = DecodeAsync(data, ++_version);
    }

    private async Task DecodeAsync(byte[] data, int version)
    {
        try
        {
            bool gif = data.Length > 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F';
            if (gif)
            {
                var codec = await Task.Run(() => SKCodec.Create(SKData.CreateCopy(data))).ConfigureAwait(true);
                if (version != _version || codec == null) { codec?.Dispose(); if (codec == null) await ShowStillAsync(data, version); return; }
                if (codec.FrameCount > 1 && (long)codec.Info.Width * codec.Info.Height <= MaxAnimatedPixels)
                {
                    StartGif(codec);
                    return;
                }
                codec.Dispose();
            }
            await ShowStillAsync(data, version);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException) { }
    }

    private async Task ShowStillAsync(byte[] data, int version)
    {
        var bitmap = await Task.Run(() => new Bitmap(new MemoryStream(data))).ConfigureAwait(true);
        if (version != _version) { bitmap.Dispose(); return; }
        _full = bitmap;
        InvalidateVisual();
    }

    private void StartGif(SKCodec codec)
    {
        _codec = codec;
        _info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _buffer = new SKBitmap(_info);
        _frames = codec.FrameInfo;
        _frameBitmap = new WriteableBitmap(new PixelSize(_info.Width, _info.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        ShowFrame(0);
        _full = _frameBitmap;
        StartAnimation();
    }

    private void StartAnimation()
    {
        if (_codec == null || _frames.Length < 2 || VisualRoot == null) return;
        _timer ??= new DispatcherTimer();
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Interval = FrameDelay(_frame);
        _timer.Start();
    }

    private TimeSpan FrameDelay(int frame) => TimeSpan.FromMilliseconds(Math.Max(20, _frames[frame].Duration <= 0 ? 100 : _frames[frame].Duration));

    private void OnTick(object? sender, EventArgs e)
    {
        if (_codec == null) return;
        try { ShowFrame((_frame + 1) % _frames.Length); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { _timer?.Stop(); return; }
        if (_timer != null) _timer.Interval = FrameDelay(_frame);
        InvalidateVisual();
    }

    // Frames build on the one before them, so the buffer keeps the last decoded frame and is reused when it is the one needed.
    private void ShowFrame(int index)
    {
        if (_codec == null || _buffer == null || _frameBitmap == null) return;
        int required = _frames[index].RequiredFrame;
        bool reuse = required >= 0 && required == _bufferFrame;
        if (!reuse) _buffer.Erase(SKColors.Transparent);
        var options = new SKCodecOptions(index, reuse ? required : -1);
        if (_codec.GetPixels(_info, _buffer.GetPixels(), options) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            _bufferFrame = -1;
            return;
        }
        _bufferFrame = index;
        _frame = index;

        using var fb = _frameBitmap.Lock();
        var source = _buffer.GetPixels();
        int row = Math.Min(_info.RowBytes, fb.RowBytes);
        var line = new byte[row];
        for (int y = 0; y < _info.Height; y++)
        {
            Marshal.Copy(source + y * _info.RowBytes, line, 0, row);
            Marshal.Copy(line, 0, fb.Address + y * fb.RowBytes, row);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int w = PixelWidth, h = PixelHeight;
        if (w <= 0 || h <= 0) return default;
        double scale = Math.Min(1.0, Math.Min(MaxShownWidth / w, MaxShownHeight / h));
        return new Size(Math.Max(24, w * scale), Math.Max(24, h * scale));
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var clip = new RoundedRect(bounds, 10);
        var image = _full ?? _thumb;
        if (image == null)
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), null, clip);
            return;
        }
        using (context.PushClip(clip))
            context.DrawImage(image, new Rect(image.Size), bounds);
    }
}
