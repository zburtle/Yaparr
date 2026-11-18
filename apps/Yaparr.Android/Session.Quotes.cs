using Yaparr.Chat;

namespace Yaparr.AndroidApp;

// A quote whose original this phone does not have on the screen: it is brought from the history this phone holds, or asked for from the other members of the room (or,
// in a direct chat, from your other devices and the person's), and it arrives like any message (a room's and a chat's MessageReceived).
public sealed partial class Session
{
    /// <summary>Asks for the message a quote names. How many were asked, 0 if none could be, or -1 when it was held here and is on the screen now.</summary>
    public int RequestOriginal(Chat chat, string key) =>
        chat.Room is { } room ? room.RequestMessages(new[] { key })
        : chat.Direct is { } direct ? direct.RequestMessages(new[] { key })
        : 0;
}
