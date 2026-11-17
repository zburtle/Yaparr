using Avalonia.Input;
using Avalonia.Threading;
using Yaparr.Chat;

namespace Yaparr.Desktop;

// Telling the person's other devices which chat is in front of them, so that the phone does not notify for it (ChatNode.SetViewing). A chat counts as in front when it is
// the one shown, the window is the active one and not minimized, and the person has used the computer in the last couple of minutes.
public partial class MainWindow
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ViewingRepeat = TimeSpan.FromSeconds(60);

    private readonly DispatcherTimer _viewTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ViewTarget? _viewSent;
    private long _viewSentAt = long.MinValue;
    private long _lastInputAt = Environment.TickCount64;

    // Called once the account is known.
    private void StartViewing()
    {
        void Touched(object? s, Avalonia.Interactivity.RoutedEventArgs e) => _lastInputAt = Environment.TickCount64;
        AddHandler(PointerMovedEvent, Touched, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, Touched, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, Touched, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        Activated += (_, _) => { _lastInputAt = Environment.TickCount64; ReportViewing(); };
        Deactivated += (_, _) => ReportViewing();
        _viewTimer.Tick += (_, _) => ReportViewing();
        _viewTimer.Start();
    }

    private ViewTarget? CurrentView()
    {
        if (!IsActive || !IsVisible || WindowState == Avalonia.Controls.WindowState.Minimized || _selected is not { } conv) return null;
        if (Environment.TickCount64 - _lastInputAt > IdleAfter.TotalMilliseconds) return null;
        if (conv.IsRoom) return RoomFor(conv) is { } room ? new ViewTarget(room.RoomId, conv.CurrentChannel, null) : null;
        var direct = _directConversations.FirstOrDefault(kv => kv.Value == conv);
        return direct.Value == conv ? new ViewTarget(null, null, direct.Key) : null;
    }

    // Sent when what is shown changes, and again every minute while something is shown (the others forget it after three).
    private void ReportViewing()
    {
        if (_node is not { } node) return;
        var now = CurrentView();
        long tick = Environment.TickCount64;
        if (Equals(now, _viewSent) && (now == null || tick - _viewSentAt < ViewingRepeat.TotalMilliseconds)) return;
        _viewSent = now;
        _viewSentAt = tick;
        node.SetViewing(now);
    }
}
