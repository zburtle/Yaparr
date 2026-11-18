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

    /// <summary>The first line of the tooltip: how this person is reached, in a few words.</summary>
    public static string Title(PeerLink link, bool online, bool relayed, bool usesCloud) => link switch
    {
        PeerLink.Direct => "Connected directly",
        PeerLink.ViaServer => "Through the relay server",
        PeerLink.Relayed => "Through another member",
        PeerLink.Reconnecting => "Reconnecting",
        PeerLink.NotResponding => "Not responding",
        _ when online => relayed ? "Through another member" : "Online",
        _ => usesCloud ? "Offline, on Yaparr Cloud" : "Offline",
    };

    /// <summary>What it means, one or two short sentences (nothing about when they were last seen: see <see cref="HasSeen"/>).</summary>
    /// <param name="iUseCloud">Whether you are signed in to a server yourself: messages only wait for them if you are.</param>
    public static string Detail(PeerLink link, bool online, bool relayed, bool usesCloud, bool iUseCloud) => link switch
    {
        PeerLink.Direct => "The fastest way.",
        PeerLink.ViaServer => "It works but is slower. Yaparr keeps looking for a better way.",
        PeerLink.Relayed => "A member passes things on. They can't read them.",
        PeerLink.Reconnecting => "The connection dropped. Trying again.",
        PeerLink.NotResponding => "They haven't confirmed your last message.",
        _ when online => relayed ? "A member passes things on. They can't read them." : "Here now.",
        _ when usesCloud => iUseCloud
            ? "What you write waits for them on the server for up to 7 days."
            : "Sign in to Yaparr Cloud too, and what you write waits for them there.",
        _ => "Not connected. What you write goes to them when they are back.",
    };

    /// <summary>Whether the tooltip says when they were last seen (only for someone who is not connected).</summary>
    public static bool HasSeen(PeerLink link, bool online) => !online && link == PeerLink.Offline;

    public static string Seen(DateTimeOffset lastSeen) => "Last seen " + lastSeen.LocalDateTime.ToString("g");

    /// <summary>A 64-digit id in two lines of four groups of eight, easier to read and to compare than one long wrapped line.</summary>
    public static string IdLines(string id) =>
        id.Length == 64
            ? string.Join(' ', Enumerable.Range(0, 4).Select(i => id.Substring(i * 8, 8))) + "\n" + string.Join(' ', Enumerable.Range(4, 4).Select(i => id.Substring(i * 8, 8)))
            : id;
}
