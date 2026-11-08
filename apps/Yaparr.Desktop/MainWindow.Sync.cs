using Yaparr.Chat;
using Avalonia.Threading;

namespace Yaparr.Desktop;

// Shows that rooms are getting going: a line under each room that is not ready yet. The room decides the words; this only asks once a second.
public partial class MainWindow
{
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private void StartSyncIndicator()
    {
        _syncTimer.Tick += (_, _) => UpdateSyncIndicator();
        _syncTimer.Start();
    }

    private void UpdateSyncIndicator()
    {
        foreach (var (room, conv) in _roomConversations.ToList()) conv.UpdateSync(room);
    }
}
