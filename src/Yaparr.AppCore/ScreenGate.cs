using System.ComponentModel;
using Yaparr.Vpn;

namespace Yaparr.AppCore;

/// <summary>
/// Whether screens may be shared or watched now. While Yaparr Cloud is acting as the gateway (the network blocks UDP) they may not: the gateway carries messages and voice, and its
/// limits are far below what video needs (see the Yaparr.Cloud README, "UDP gateway"). The apps bind the share and watch buttons to <see cref="Allowed"/> (so they are greyed, with
/// <see cref="Reason"/> as their tip), and stop what is under way when it turns false. One gate for the whole app: it follows the node's gateway state (<see cref="Update"/>).
/// </summary>
public sealed class ScreenGate : INotifyPropertyChanged
{
    public const string Why = "Sharing and watching screens are off while Yaparr Cloud is acting as your gateway, because this network blocks UDP. The gateway carries messages and voice, not video.";

    public static ScreenGate Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Blocked { get; private set; }

    public bool Allowed => !Blocked;

    /// <summary>Why screens are off, for a tip or a note; null when they are not.</summary>
    public string? Reason => Blocked ? Why : null;

    /// <summary>Follows the gateway: any state but off (connecting too, as the traffic is going that way) turns screens off. Returns true if that changed.</summary>
    public bool Update(VpnState gateway)
    {
        bool blocked = gateway != VpnState.Disconnected;
        if (blocked == Blocked) return false;
        Blocked = blocked;
        foreach (var name in new[] { nameof(Blocked), nameof(Allowed), nameof(Reason) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
