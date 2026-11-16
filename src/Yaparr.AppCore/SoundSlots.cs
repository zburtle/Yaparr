using System.Globalization;

namespace Yaparr.AppCore;

/// <summary>
/// The ten keys of a soundboard (1 to 9, then 0), each holding one sound of the room or none. Every person arranges their own: the keys are kept in the
/// person's settings, by room (a sound belongs to a room), and nobody else sees or is told about them. A board nobody has arranged has the first ten sounds
/// in the order they are listed; the first change writes all ten keys down, so they stay as they were left from then on (a sound that is removed leaves
/// its key empty). Pure, so the rules can be checked without a window.
/// </summary>
public static class SoundSlots
{
    /// <summary>How many keys there are.</summary>
    public const int Count = 10;

    /// <summary>The label of a key: "1" to "9" for the first nine, "0" for the tenth; empty for anything else.</summary>
    public static string KeyLabel(int slot) => slot is >= 0 and < Count ? ((slot + 1) % 10).ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// What is on each key now: <paramref name="stored"/> (what the person arranged, or null if they never did) read against <paramref name="listed"/> (the
    /// ids of the room's sounds, in the order they are listed). Always <see cref="Count"/> entries, "" for an empty key.
    /// </summary>
    public static string[] Resolve(IReadOnlyList<string>? stored, IReadOnlyList<string> listed)
    {
        var slots = Enumerable.Repeat("", Count).ToArray();
        if (stored == null)
        {
            for (int i = 0; i < Count && i < listed.Count; i++) slots[i] = listed[i];
            return slots;
        }
        var exists = listed.ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Count && i < stored.Count; i++)
            if (stored[i] is { Length: > 0 } id && exists.Contains(id) && used.Add(id)) slots[i] = id;
        return slots;
    }

    /// <summary>
    /// Puts a sound on a key. A sound is on one key only: one that is on another key moves, and what was on the key it moves to takes the place it left
    /// (so dragging a key onto another swaps them); a sound that was on no key takes the key and what was there goes off the keys.
    /// </summary>
    public static string[] Put(IReadOnlyList<string> slots, string soundId, int slot)
    {
        var next = Resolve(slots, slots.Where(s => s.Length > 0).ToList());
        if (slot < 0 || slot >= Count || soundId.Length == 0) return next;
        int from = Array.IndexOf(next, soundId);
        if (from == slot) return next;
        if (from >= 0) next[from] = next[slot];
        next[slot] = soundId;
        return next;
    }

    /// <summary>Takes the sound off a key.</summary>
    public static string[] Clear(IReadOnlyList<string> slots, int slot)
    {
        var next = Resolve(slots, slots.Where(s => s.Length > 0).ToList());
        if (slot >= 0 && slot < Count) next[slot] = "";
        return next;
    }
}
