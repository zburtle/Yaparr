using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace Yaparr.AndroidApp;

/// <summary>
/// An image that plays a GIF. Avalonia only draws the first frame of one, so when <see cref="Animation"/> holds the bytes of an animated GIF
/// the frames are decoded one at a time and shown in turn, for as long as the control is on screen. Until then, and for anything that is
/// not animated, it is the ordinary Image with its Source.
/// </summary>
public sealed class AnimatedImage : Image
{
    private const int MaxAnimatedPixels = 1_500_000;

    public static readonly StyledProperty<byte[]?> AnimationProperty = AvaloniaProperty.Register<AnimatedImage, byte[]?>(nameof(Animation));

    private int _version;
    private SKCodec? _codec;
    private SKBitmap? _buffer;
    private SKBitmap? _scaled;
    private SKImageInfo _info;
    private SKCodecFrameInfo[] _frames = Array.Empty<SKCodecFrameInfo>();
    private WriteableBitmap? _frameBitmap;
    private int _frame, _bufferFrame = -1;
    private DispatcherTimer? _timer;

    /// <summary>When above zero, the frames are kept at no more than this many pixels on a side (for small players).</summary>
    public int MaxFrameSide { get; init; }

    /// <summary>The bytes of the GIF to play, or null.</summary>
    public byte[]? Animation { get => GetValue(AnimationProperty); set => SetValue(AnimationProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AnimationProperty) Reload();
        else if (change.Property == IsVisibleProperty) { if (IsVisible) StartTimer(); else _timer?.Stop(); }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StartTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }

    private void Reload()
    {
        _timer?.Stop();
        _timer = null;
        _codec?.Dispose();
        _codec = null;
        _buffer?.Dispose();
        _buffer = null;
        _scaled?.Dispose();
        _scaled = null;
        _frameBitmap = null;
        _bufferFrame = -1;
        _frame = 0;
        if (Animation is { Length: > 0 } data) _ = DecodeAsync(data, ++_version);
    }

    private async Task DecodeAsync(byte[] data, int version)
    {
        try
        {
            var codec = await Task.Run(() => SKCodec.Create(SKData.CreateCopy(data))).ConfigureAwait(true);
            if (version != _version || codec == null) { codec?.Dispose(); return; }
            if (codec.FrameCount < 2 || (long)codec.Info.Width * codec.Info.Height > MaxAnimatedPixels) { codec.Dispose(); return; }
            _codec = codec;
            _info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            _buffer = new SKBitmap(_info);
            _frames = codec.FrameInfo;
            // Small players (the shelf) keep the frames at the size they are shown, so many of them can play at once.
            int shownWidth = _info.Width, shownHeight = _info.Height;
            if (MaxFrameSide > 0 && Math.Max(_info.Width, _info.Height) > MaxFrameSide)
            {
                double scale = (double)MaxFrameSide / Math.Max(_info.Width, _info.Height);
                shownWidth = Math.Max(1, (int)(_info.Width * scale));
                shownHeight = Math.Max(1, (int)(_info.Height * scale));
                _scaled = new SKBitmap(new SKImageInfo(shownWidth, shownHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            }
            _frameBitmap = new WriteableBitmap(new PixelSize(shownWidth, shownHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            ShowFrame(0);
            SetCurrentValue(SourceProperty, _frameBitmap);
            StartTimer();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { }
    }

    private void StartTimer()
    {
        if (_codec == null || _frames.Length < 2 || VisualRoot == null || !IsEffectivelyVisible) return;
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

        var shown = _buffer;
        if (_scaled != null)
        {
            shown.ScalePixels(_scaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            shown = _scaled;
        }
        using var fb = _frameBitmap.Lock();
        var source = shown.GetPixels();
        int row = Math.Min(shown.RowBytes, fb.RowBytes);
        var line = new byte[row];
        for (int y = 0; y < shown.Height; y++)
        {
            Marshal.Copy(source + y * shown.RowBytes, line, 0, row);
            Marshal.Copy(line, 0, fb.Address + y * fb.RowBytes, row);
        }
    }
}
