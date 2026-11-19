using System.Collections.ObjectModel;
using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>
/// Keeps pinned rooms and chats at the top of a list, in the order they were pinned and moved to, with the others left in the order they have. The computer app and
/// the phone app both use it (their lists differ in how the others are ordered: as they came on the computer, newest message first on the phone). Pure, apart from
/// <see cref="Apply{T}"/> moving the items of the collection it is given.
/// </summary>
public sealed class PinnedFirst
{
    private readonly Dictionary<(bool IsRoom, string Id), int> _rank = new();

    /// <summary>Takes the pins of the account, first to last (<see cref="ChatNode.Pins"/>).</summary>
    public PinnedFirst(IReadOnlyList<PinnedConversation> pins)
    {
        for (int i = 0; i < pins.Count; i++) _rank[(pins[i].IsRoom, pins[i].Id)] = i;
    }

    /// <summary>The place of a room or chat among the pinned ones (0 is the first); null if it is not pinned (or has no id).</summary>
    public int? RankOf(bool isRoom, string? id) => id != null && _rank.TryGetValue((isRoom, id), out var rank) ? rank : null;

    public bool IsPinned(bool isRoom, string? id) => RankOf(isRoom, id) != null;

    /// <summary>The items in the order they should be shown: the pinned ones first, by their place, and then the rest in the order they were given.</summary>
    public static List<T> Arrange<T>(IReadOnlyList<T> items, Func<T, int?> rankOf, Func<T, DateTimeOffset>? activity = null)
    {
        var rest = items.Where(i => rankOf(i) == null);
        if (activity != null) rest = rest.OrderByDescending(activity);   // newest message first; a stable sort, so ties keep their order
        return items.Where(i => rankOf(i) != null).OrderBy(i => rankOf(i)).Concat(rest).ToList();
    }

    /// <summary>Puts the items of the list in that order, by moving them, so that what is shown (and which one is selected) is not rebuilt. With <paramref name="activity"/> the unpinned ones go newest message first.</summary>
    public static void Apply<T>(ObservableCollection<T> list, Func<T, int?> rankOf, Func<T, DateTimeOffset>? activity = null)
    {
        var want = Arrange(list.ToList(), rankOf, activity);
        for (int i = 0; i < want.Count; i++)
        {
            int at = list.IndexOf(want[i]);
            if (at != i) list.Move(at, i);
        }
    }

    /// <summary>
    /// What <see cref="ChatNode.MovePin"/> needs to put <paramref name="moved"/> at place <paramref name="index"/> of the pinned ones as shown: the position of the one it
    /// will come after and of the one it will come before (null at an end). <paramref name="shown"/> is the pinned ones in the order they are shown, the moved one in it;
    /// an index past either end is the end.
    /// </summary>
    public static (string? After, string? Before) Neighbours(IReadOnlyList<PinnedConversation> shown, PinnedConversation moved, int index)
    {
        var others = shown.Where(p => p.IsRoom != moved.IsRoom || p.Id != moved.Id).ToList();
        index = Math.Clamp(index, 0, others.Count);
        return (index > 0 ? others[index - 1].Order : null, index < others.Count ? others[index].Order : null);
    }
}
