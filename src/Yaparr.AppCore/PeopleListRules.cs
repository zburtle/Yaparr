using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>
/// Who the list of people of a room shows. People who are on are always there. Someone who is off is listed with when they were last on, and a person who has
/// not been on for <see cref="StaleAfter"/> is dropped from the list (it would only grow, with names of people who are gone); they are listed again as soon as
/// they are on. The owner and the admins stay listed however long they have been away, so the room's owner can always be seen. This only decides what the
/// list shows: the device still remembers everyone it has seen in the room (for handing over a new secret to people who come back), and people who are banned
/// or blocked have lines of their own so that they can be unbanned or unblocked. Pure: the time is passed in.
/// </summary>
public static class PeopleListRules
{
    /// <summary>How long someone can be away before they are no longer listed.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    /// <summary>Whether a person is in the list of people: on now, an owner or admin, or on within <see cref="StaleAfter"/>.</summary>
    public static bool ShowsInList(bool online, DateTimeOffset lastSeen, DateTimeOffset now, RoomRole role) =>
        online || role >= RoomRole.Admin || now - lastSeen <= StaleAfter;
}
