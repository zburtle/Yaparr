using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// When the connection fails while you are in a call: a bar across the top of the window, a short sound each way, and "reconnecting" next to
// the people you cannot reach. Outside a call the ordinary "reconnecting" under a person's name is all there is. ConnectionWatch decides
// what counts as lost.
public partial class MainWindow
{
    private readonly ConnectionWatch _call = new(minPeers: 1);
    private readonly Dictionary<string, long> _unreachableSince = new();

    private void SetUpConnectionWatch()
    {
        _call.Lost += () => UI(() =>
        {
            _log.Write("lost the connection to the call; trying again");
            ShowReconnectText();
            ReconnectBar.IsVisible = true;
            _sounds.ConnectionLost();
        });
        _call.Restored += () => UI(() =>
        {
            _log.Write("connection to the call restored");
            ReconnectBar.IsVisible = false;
            StatusText.Text = "Back in the call.";
            _sounds.ConnectionBack();
        });
    }

    // Called whenever the people connected or in the call change, and every few seconds.
    private void WatchConnection()
    {
        if (_node == null) return;
        var room = _voice != null && _voiceConv != null ? _roomConversations.FirstOrDefault(kv => kv.Value == _voiceConv).Key : null;
        if (room == null) { StopWatchingCall(); return; }

        long now = Environment.TickCount64;
        var reach = room.VoiceReach;
        // Nobody else is in the call: there is nobody to lose.
        if (reach.Expected == 0) { _call.Reset(); ReconnectBar.IsVisible = false; ClearReconnecting(_voiceConv!); return; }

        // The system says at once when the network is gone; without it, people are only noticed missing after a while.
        bool networkUp = ChatNode.NetworkAvailable;
        _call.Update(networkUp ? reach.Reachable : 0, now);
        if (_call.Reconnecting) ShowReconnectText();

        // Next to a person: only once they have been out of reach for a few seconds, so someone who has just joined does not flash it.
        var out_ = networkUp ? reach.Unreachable.Select(id => id.Value).ToHashSet() : _voiceConv!.VoiceMembers.Where(m => !m.IsSelf).Select(m => m.Id).ToHashSet();
        foreach (var gone in _unreachableSince.Keys.Where(k => !out_.Contains(k)).ToList()) _unreachableSince.Remove(gone);
        foreach (var id in out_) _unreachableSince.TryAdd(id, now);
        foreach (var member in _voiceConv!.VoiceMembers.Where(m => !m.IsSelf))
            member.Reconnecting = _unreachableSince.TryGetValue(member.Id, out var since) && now - since >= 5000;
    }

    private void ClearReconnecting(ConversationVm conv)
    {
        _unreachableSince.Clear();
        foreach (var member in conv.VoiceMembers) member.Reconnecting = false;
    }

    // Leaving the call, or the call ending for you, takes the warning away without a sound.
    private void StopWatchingCall()
    {
        _call.Reset();
        _unreachableSince.Clear();
        if (ReconnectBar.IsVisible) ReconnectBar.IsVisible = false;
    }

    private void ShowReconnectText() =>
        ReconnectText.Text = _call.LostFor(Environment.TickCount64) > TimeSpan.FromMinutes(1)
            ? "Still can't reach the call. Check your internet connection or VPN; Yaparr keeps trying."
            : "Lost the connection to the call. Yaparr is trying to reconnect…";
}
