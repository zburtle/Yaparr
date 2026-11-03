using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Identity;
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
    private readonly ChatRoom _room;
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(10);
    private readonly TextBlock _text = new() { FontSize = 12, Margin = new Avalonia.Thickness(10), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _frames, _bytes, _at = Environment.TickCount64, _retransmitted;

    public SharerStatsWindow(ScreenSharer sharer, int targetFps, ChatRoom room)
    {
        _sharer = sharer;
        _room = room;
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
        string encoder = _sharer.Codec == ScreenCodec.H264 && _sharer.Encoder is { } card ? $"{HardwareEncoders.Maker(card)} graphics card (H.264)" : "processor (VP8)";
        _text.Text = $"Encoder: {encoder}" + (_sharer is { FallbackReason: not null, Encoder: null } ? " - no graphics card encoder could be used" : "") + "\n"
            + $"Sending: {fps:0.0} of {_targetFps} frames/s, {kbps:0} kbit/s, {_sharer.KeyFrames} key frames\n"
            + $"Longest wait for a frame from FFmpeg: {gap} ms" + (behind ? "  <- capture or encoding is behind" : "") + "\n"
            + $"Watchers directly connected: {status.Children.Count}   packets sent again this second: {resent}   dropped: {status.Dropped}\n"
            + $"Watching: {Names(_sharer.Source.Viewers)}"
            + (_sharer.Source.Unreached(GiveUpAfter) is { Count: > 0 } stuck ? $"\nCannot reach you yet: {Names(stuck)}. Their connection may be blocked; the connection guide in the Help menu can help." : "");
    }

    // People, not devices: someone watching on two computers is named once.
    private string Names(IReadOnlyList<PeerId> devices)
    {
        if (devices.Count == 0) return "nobody yet";
        var members = _room.Members;
        var names = devices
            .Select(d => members.FirstOrDefault(m => m.Id == d) is { } m ? (Account: m.Account.Value, Name: m.DisplayName) : (Account: d.Value, Name: d.Short))
            .DistinctBy(x => x.Account).Select(x => x.Name).ToList();
        return names.Count <= 8 ? string.Join(", ", names) : string.Join(", ", names.Take(8)) + $" and {names.Count - 8} more";
    }
}
