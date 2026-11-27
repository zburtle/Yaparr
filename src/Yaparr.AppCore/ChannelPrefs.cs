using System.Security.Cryptography;
using System.Text;

namespace Yaparr.AppCore;

/// <summary>What a person can do to a text channel for themselves, without changing it for anyone else.</summary>
public enum ChannelFlag
{
    /// <summary>Kept at the top of its group (its category, or the channels outside any) in the room's list.</summary>
    Pinned,
    /// <summary>Does not notify and does not add to the room's unread count, unless a message names you.</summary>
    Muted,
}

/// <summary>
/// How one person wants a room's channels laid out: pinned and muted channels, whether the rest are in the room's order or the most recently active first, and
/// which categories are folded away. These are kept in the settings and travel between the person's devices (see <see cref="SettingsSync"/>); nobody else sees
/// them. A channel is named by its room's id and its own id, separated by a slash; a category by its room's id and a short hash of its name.
/// </summary>
public static class ChannelPrefs
{
    private static string KeyOf(string roomId, string channelId) => roomId + "/" + channelId;

    private static List<string> ListOf(AppSettings s, ChannelFlag flag) => flag == ChannelFlag.Pinned ? s.PinnedChannels : s.MutedChannels;

    public static bool Has(AppSettings s, string roomId, string channelId, ChannelFlag flag) => ListOf(s, flag).Contains(KeyOf(roomId, channelId));

    /// <summary>Sets or clears a flag. True if that changed anything.</summary>
    public static bool Set(AppSettings s, string roomId, string channelId, ChannelFlag flag, bool on)
    {
        var list = ListOf(s, flag);
        var key = KeyOf(roomId, channelId);
        if (on) { if (list.Contains(key)) return false; list.Add(key); return true; }
        return list.Remove(key);
    }

    public static bool RecentFirst(AppSettings s, string roomId) => s.RecentFirstRooms.Contains(roomId);

    public static bool SetRecentFirst(AppSettings s, string roomId, bool on)
    {
        if (on) { if (s.RecentFirstRooms.Contains(roomId)) return false; s.RecentFirstRooms.Add(roomId); return true; }
        return s.RecentFirstRooms.Remove(roomId);
    }

    // A category is named by a short hash of its name (the same whatever its capitals), which keeps the entry that carries it short.
    private static string CategoryKeyOf(string roomId, string category) =>
        roomId + "/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(category.Trim().ToLowerInvariant())))[..8];

    /// <summary>Whether this person folded the category away (its channels are not listed under it).</summary>
    public static bool IsCollapsed(AppSettings s, string roomId, string category) => s.CollapsedCategories.Contains(CategoryKeyOf(roomId, category));

    public static bool SetCollapsed(AppSettings s, string roomId, string category, bool on)
    {
        var key = CategoryKeyOf(roomId, category);
        if (on) { if (s.CollapsedCategories.Contains(key)) return false; s.CollapsedCategories.Add(key); return true; }
        return s.CollapsedCategories.Remove(key);
    }

    /// <summary>Whether a message in this channel should notify and count: a muted channel only lets through a message that names you.</summary>
    public static bool Counts(AppSettings s, string roomId, string channelId, bool mentionsMe) => mentionsMe || !Has(s, roomId, channelId, ChannelFlag.Muted);

    /// <summary>
    /// A group of channels (a category's, or the ones in none) as the list should show them: pinned ones first, then the others, in the room's order or the most recently
    /// active first. <paramref name="inRoomOrder"/> is the room's own order.
    /// </summary>
    public static List<T> Arrange<T>(AppSettings s, string roomId, IEnumerable<T> inRoomOrder, Func<T, string> idOf, Func<T, long> lastActive)
    {
        var all = inRoomOrder.ToList();
        bool recent = RecentFirst(s, roomId);
        IEnumerable<T> Rest(IEnumerable<T> items) => recent ? items.OrderByDescending(lastActive) : items;   // OrderByDescending is stable: ties keep the room's order
        var pinned = all.Where(c => Has(s, roomId, idOf(c), ChannelFlag.Pinned));
        var others = all.Where(c => !Has(s, roomId, idOf(c), ChannelFlag.Pinned));
        return Rest(pinned).Concat(Rest(others)).ToList();
    }
}
