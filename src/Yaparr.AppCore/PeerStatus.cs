using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>
/// The words for how a person in the people list is reached, and what that means, for both apps. Short on purpose: the status is a word or two, the tip is one or two
/// sentences. A person who is not connected but uses Yaparr Cloud says so (a message written to them waits for them on the server).
/// </summary>
public static class PeerStatus
{
    public static string Text(PeerLink link, bool online, bool relayed, bool usesCloud) => link switch
    {
        PeerLink.Direct => "direct",
        PeerLink.ViaServer => "via relay server",
        PeerLink.Relayed => "via member",
        PeerLink.Reconnecting => "reconnecting…",
        PeerLink.NotResponding => "not responding",
        _ => online ? (relayed ? "via member" : "online") : usesCloud ? "offline · on Yaparr Cloud" : "offline",
    };

    /// <param name="iUseCloud">Whether you are signed in to a server yourself: messages only wait for them if you are.</param>
    public static string Tip(PeerLink link, bool online, bool relayed, bool usesCloud, bool iUseCloud, DateTimeOffset lastSeen) => link switch
    {
        PeerLink.Direct => "Connected straight to them. The fastest way.",
        PeerLink.ViaServer => "Through the shared relay server. It works but is slower; Yaparr keeps looking for a better path.",
        PeerLink.Relayed => "Through another member who passes things on. They can't read them.",
        PeerLink.Reconnecting => "The connection dropped. Trying again.",
        PeerLink.NotResponding => "Connected, but they haven't confirmed your last message.",
        _ when online => relayed ? "Through another member who passes things on. They can't read them." : "Online.",
        _ when usesCloud => iUseCloud
            ? $"Not connected, but they use Yaparr Cloud: what you write waits for them on the server for up to 30 days. Last seen {lastSeen.LocalDateTime:g}."
            : $"Not connected. They use Yaparr Cloud; sign in to Yaparr Cloud too and what you write waits for them there. Last seen {lastSeen.LocalDateTime:g}.",
        _ => $"Not connected. Last seen {lastSeen.LocalDateTime:g}.",
    };
}
