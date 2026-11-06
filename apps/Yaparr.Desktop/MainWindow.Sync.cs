using Yaparr.Chat;
using Avalonia.Threading;

namespace Yaparr.Desktop;

// Shows that rooms are getting going: a bar above the lists ("Syncing 2 of 5 rooms…", or that this device is looking for the one it was
// linked from) and a line under each room that is not ready yet. SyncStatus decides the words; this only asks once a second.
public partial class MainWindow
{
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DeviceSearch _deviceSearch = new();

    private void StartSyncIndicator()
    {
        // This computer was linked to an account when it started: it looks for the device it was linked from.
        if (_linkedEntries.Count > 0) _deviceSearch.Begin();
        _syncTimer.Tick += (_, _) => UpdateSyncIndicator();
        _syncTimer.Start();
    }

    private void UpdateSyncIndicator()
    {
        if (_node is not { } node) return;
        var states = new List<RoomSyncState>();
        foreach (var (room, conv) in _roomConversations.ToList()) states.Add(conv.UpdateSync(room));
        var text = SyncStatus.Banner(_deviceSearch.Update(node), states, out var progress);
        SyncBar.IsVisible = text != null;
        SyncBarText.Text = text ?? "";
        SyncProgress.IsIndeterminate = progress == null;
        SyncProgress.Value = progress ?? 0;
    }
}
