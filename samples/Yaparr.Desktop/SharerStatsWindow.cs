using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Yaparr.Screen;

namespace Yaparr.Desktop;

/// <summary>
/// A small live readout while you share a screen, to tell a sender that cannot keep up from a network that loses packets.
/// Closing it does not stop the share.
/// </summary>
public sealed class SharerStatsWindow : Window
{
    private readonly ScreenSharer _sharer;
    private readonly int _targetFps;
    private readonly TextBlock _text = new() { FontSize = 12, Margin = new Avalonia.Thickness(10), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _frames, _bytes, _at = Environment.TickCount64, _retransmitted;

    public SharerStatsWindow(ScreenSharer sharer, int targetFps)
    {
        _sharer = sharer;
        _targetFps = targetFps;
        Title = "Sharing";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Content = _text;
        _frames = sharer.Frames;
        _bytes = sharer.Bytes;
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Update();
    }

    private void Update()
    {
        long now = Environment.TickCount64;
        double seconds = Math.Max(0.1, (now - _at) / 1000.0);
        long frames = _sharer.Frames, bytes = _sharer.Bytes;
        double fps = (frames - _frames) / seconds, kbps = (bytes - _bytes) * 8 / 1000.0 / seconds;
        (_frames, _bytes, _at) = (frames, bytes, now);

        var status = _sharer.Source.Status;
        long resent = status.Retransmitted - _retransmitted;
        _retransmitted = status.Retransmitted;
        long gap = _sharer.TakeMaxFrameGapMs();

        // A gap far over the frame time means FFmpeg itself stalled (capture or encoding), whatever the network does.
        bool behind = fps < _targetFps * 0.9 || gap > 3000.0 / Math.Max(1, _targetFps);
        string encoder = _sharer.Codec == ScreenCodec.H264 ? "graphics card (H.264)" : "processor (VP8)";
        _text.Text = $"Encoder: {encoder}" + (_sharer.FallbackReason != null ? " - the graphics card could not be used" : "") + "\n"
            + $"Sending: {fps:0.0} of {_targetFps} frames/s, {kbps:0} kbit/s, {_sharer.KeyFrames} key frames\n"
            + $"Longest wait for a frame from FFmpeg: {gap} ms" + (behind ? "  <- capture or encoding is behind" : "") + "\n"
            + $"Watchers directly connected: {status.Children.Count}   packets sent again this second: {resent}   dropped: {status.Dropped}";
    }
}
