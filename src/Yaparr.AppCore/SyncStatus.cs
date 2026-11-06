using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>Whether a device that has just been linked has found the device it was linked from.</summary>
public enum DeviceSearchState
{
    /// <summary>Not looking: nothing was linked, or the other device has been found and the rooms have arrived.</summary>
    None,

    /// <summary>Trying to reach the other device.</summary>
    Looking,

    /// <summary>Connected to the other device; the rooms and settings are coming over.</summary>
    Connected,

    /// <summary>Looked for a while and could not reach it, most likely because Yaparr is not open there.</summary>
    NotFound,
}

/// <summary>
/// Follows a newly linked device's first connection to the one it was linked from, so the screen can say what is happening: trying,
/// connected, or not reachable. It only runs after a link, because a device on its own is not looking for anything.
/// </summary>
public sealed class DeviceSearch
{
    private const long GiveUpMs = 90_000, SettleMs = 4_000;
    private long _since = -1, _connectedAt = -1;

    /// <summary>A device has just been linked: start looking.</summary>
    public void Begin() { _since = Environment.TickCount64; _connectedAt = -1; }

    public DeviceSearchState Update(ChatNode node)
    {
        if (_since < 0) return DeviceSearchState.None;
        long now = Environment.TickCount64;
        if (node.HasOwnDeviceConnected)
        {
            if (_connectedAt < 0) _connectedAt = now;
            if (now - _connectedAt < SettleMs) return DeviceSearchState.Connected;
            _since = -1;
            return DeviceSearchState.None;
        }
        _connectedAt = -1;
        return now - _since < GiveUpMs ? DeviceSearchState.Looking : DeviceSearchState.NotFound;
    }
}

/// <summary>What the room list says about getting going: a line under each room that is not ready, and one banner above the list.</summary>
public static class SyncStatus
{
    /// <summary>The short line under a room, empty when there is nothing to say.</summary>
    public static string RoomText(RoomSyncState state) => state switch
    {
        RoomSyncState.FindingMembers => "Finding members…",
        RoomSyncState.LoadingHistory => "Loading history…",
        RoomSyncState.NoOneOnline => "No one online",
        _ => "",
    };

    /// <summary>The room is working on it, so a spinner is worth showing.</summary>
    public static bool IsBusy(RoomSyncState state) => state is RoomSyncState.FindingMembers or RoomSyncState.LoadingHistory;

    /// <summary>The line for the banner above the list, or null when there is nothing to report. <paramref name="progress"/> is 0 to 1, or null when it cannot be told.</summary>
    public static string? Banner(DeviceSearchState device, IReadOnlyCollection<RoomSyncState> rooms, out double? progress)
    {
        progress = null;
        switch (device)
        {
            case DeviceSearchState.Looking: return "Looking for your other device…";
            case DeviceSearchState.Connected: return "Connected to your other device. Getting your rooms…";
            case DeviceSearchState.NotFound: return "Can't reach your other device. Open Yaparr on it and this will connect by itself.";
        }
        int busy = rooms.Count(IsBusy);
        if (busy == 0) return null;
        int total = rooms.Count(s => s != RoomSyncState.NoOneOnline);
        int done = total - busy;
        progress = total == 0 ? null : (double)done / total;
        return total == 1 ? "Syncing 1 room…" : $"Syncing {done} of {total} rooms…";
    }
}
