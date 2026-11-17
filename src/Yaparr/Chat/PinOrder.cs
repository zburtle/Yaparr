namespace Yaparr.Chat;

/// <summary>
/// Where a pinned room or chat goes among the pinned ones, as text that sorts (ordinally) into the order they are shown in. A position can always be made
/// between two others (<see cref="Between"/>), so moving one pinned conversation changes only its own entry, and two devices that each move a different one never
/// overwrite each other. A position is lowercase base 36 and never ends in a 0 (which is what leaves room between any two). Pure.
/// </summary>
public static class PinOrder
{
    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>The longest position that is accepted: thousands of times over what pinning and moving by hand can reach (see <see cref="Between"/>), and well inside what an account entry may hold.</summary>
    public const int MaxLength = 200;

    /// <summary>Whether this is a position <see cref="Between"/> could have made.</summary>
    public static bool IsValid(string? position) =>
        !string.IsNullOrEmpty(position) && position.Length <= MaxLength && position[^1] != '0' && position.All(c => Digits.Contains(c));

    /// <summary>
    /// A position that sorts after <paramref name="before"/> and before <paramref name="after"/>; null for either means there is nothing on that side (the very
    /// start or the very end). Both given must be valid and <paramref name="before"/> must sort first.
    /// </summary>
    public static string Between(string? before, string? after)
    {
        if (before != null && !IsValid(before)) throw new ArgumentException("Not a pin position.", nameof(before));
        if (after != null && !IsValid(after)) throw new ArgumentException("Not a pin position.", nameof(after));
        if (before != null && after != null && string.CompareOrdinal(before, after) >= 0)
            throw new ArgumentException("The position to go after must sort before the one to go ahead of.", nameof(before));
        return Midpoint(before ?? "", after);
    }

    // The usual way to put a text between two others: skip what they share, then take a digit between theirs; where theirs are next to each other, go a digit deeper.
    private static string Midpoint(string a, string? b)
    {
        if (b != null)
        {
            // The start both share (a is read as if it went on with zeros), which the result shares too.
            int shared = 0;
            while (shared < b.Length && (shared < a.Length ? a[shared] : '0') == b[shared]) shared++;
            if (shared > 0) return b[..shared] + Midpoint(a.Length > shared ? a[shared..] : "", b[shared..]);
        }
        int low = a.Length > 0 ? Digits.IndexOf(a[0]) : 0;
        int high = b != null ? Digits.IndexOf(b[0]) : Digits.Length;
        // Going on from one end with nothing beyond it (pinning one more, or moving one to the top) takes one digit's step rather than half the way, so that doing it
        // over and over makes a position one character longer only about every 17th time, not every 5th.
        if (b == null && a.Length > 0 && low < Digits.Length - 1) return Digits[low + 1].ToString();
        if (a.Length == 0 && b != null && high >= 2) return Digits[high - 1].ToString();
        if (high - low > 1) return Digits[(low + high + 1) / 2].ToString();
        // The two digits are next to each other.
        if (b != null && b.Length > 1) return b[..1];
        return Digits[low] + Midpoint(a.Length > 1 ? a[1..] : "", null);
    }
}
