using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Identity;
using Yaparr.Screen;

namespace Yaparr.Desktop;

/// <summary>
/// A small live readout while you share a screen, to tell a sender that cannot keep up from a network that loses packets,
/// with the button that ends the share. It sits in the main window, in the room being shared to.
/// </summary>
public sealed class SharerStatsView : Border
{
    private readonly ScreenSharer _sharer;
    private readonly int _targetFps;
    private readonly ChatRoom _room;
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(10);
    private readonly TextBlock _how = new() { FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _rate = new() { FontSize = 13 };
    private readonly TextBlock _who = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _warning = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange, IsVisible = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _frames, _bytes, _at = Environment.TickCount64, _retransmitted;

    public event Action? StopRequested, ChangeRequested;

    /// <summary>People (not devices) who began or stopped watching since the last second; for the sounds.</summary>
    public event Action<int, int>? ViewersChanged;

    private readonly PresenceTracker _watchers = new();

    /// <summary>Which devices are the sharer's own, so that watching on your other computer makes no sound.</summary>
    public Func<PeerId, bool>? IsMine { get; set; }

    public SharerStatsView(ScreenSharer sharer, int targetFps, ChatRoom room)
    {
        _sharer = sharer;
        _room = room;
        _targetFps = targetFps;
        Margin = new Avalonia.Thickness(10, 6, 10, 10);
        var stop = new Button { Content = "Stop", MinWidth = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, Padding = new Avalonia.Thickness(6, 5) };
        ToolTip.SetTip(stop, "Stop sharing");
        stop.Click += (_, _) => StopRequested?.Invoke();
        var change = new Button { Content = "Change…", MinWidth = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, Padding = new Avalonia.Thickness(6, 5) };
        ToolTip.SetTip(change, "Share another monitor or window");
        change.Click += (_, _) => ChangeRequested?.Invoke();
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        Grid.SetColumn(stop, 1);
        buttons.Children.Add(change);
        buttons.Children.Add(stop);
        var title = new TextBlock { Text = "You are sharing", FontWeight = FontWeight.SemiBold };
        Child = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(12, 10, 12, 12), Children = { title, _how, _rate, _who, _warning, buttons } };
        CornerRadius = new Avalonia.CornerRadius(12);
        BorderThickness = new Avalonia.Thickness(1);
        this.Bind(BackgroundProperty, this.GetResourceObservable("BubbleOther"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Line"));
        _frames = sharer.Frames;
        _bytes = sharer.Bytes;
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        AttachedToVisualTree += (_, _) => { _timer.Start(); Update(); };
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
        bool card = _sharer.Codec is ScreenCodec.H264 or ScreenCodec.Av1 && _sharer.Encoder != null;
        string encoder = card ? $"{HardwareEncoders.Maker(_sharer.Encoder!.Value)} graphics card · {(_sharer.Codec == ScreenCodec.Av1 ? "AV1" : "H.264")}" : "Processor · VP8";
        _how.Text = encoder;
        _rate.Text = $"{fps:0} / {_targetFps} fps · {(kbps >= 1000 ? $"{kbps / 1000:0.0} Mbit/s" : $"{kbps:0} kbit/s")}";
        var viewers = _sharer.Source.Viewers;
        _who.Text = "Watching: " + Names(viewers);
        var (came, went) = _watchers.Update(PeopleOf(viewers));
        if (came.Count > 0 || went.Count > 0) ViewersChanged?.Invoke(came.Count, went.Count);

        var problems = new List<string>();
        if (behind) problems.Add($"Capture or encoding is behind (longest wait {gap} ms).");
        if (_sharer is { FallbackReason: not null, Encoder: null }) problems.Add("No graphics card encoder could be used.");
        if (resent > 0 || status.Dropped > 0) problems.Add($"Packets sent again: {resent}, dropped: {status.Dropped}.");
        if (_sharer.Source.Unreached(GiveUpAfter) is { Count: > 0 } stuck) problems.Add($"Cannot reach you yet: {Names(stuck)}. The connection guide in the Help menu can help.");
        _warning.Text = string.Join("\n", problems);
        _warning.IsVisible = problems.Count > 0;

        // The full figures, for anyone who wants them.
        ToolTip.SetTip(this, $"Key frames: {_sharer.KeyFrames}\nLongest wait for a frame from FFmpeg: {gap} ms\nWatchers directly connected: {status.Children.Count}\nPackets sent again this second: {resent}, dropped in all: {status.Dropped}");
    }

    // One entry per person, leaving out the sharer's own devices.
    private IEnumerable<string> PeopleOf(IReadOnlyList<PeerId> devices)
    {
        var members = _room.Members;
        return devices.Where(d => IsMine?.Invoke(d) != true)
            .Select(d => members.FirstOrDefault(m => m.Id == d) is { } m ? m.Account.Value : d.Value).Distinct();
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
