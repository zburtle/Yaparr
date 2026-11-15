using System.Globalization;
using System.Text;

namespace Yaparr.Chat;

/// <summary>
/// Emoji in the words someone is typing: the <c>:shortcode:</c> an emoji can be written as (its name in lower case with underscores, like
/// <c>:thumbs_up:</c>), finding the shortcode being typed before the caret, completing it, and telling a message made only of emoji. The emoji are those
/// of <see cref="EmojiData"/>; the shortcodes come from their names, so there is nothing more to keep.
/// </summary>
public static class EmojiTyping
{
    /// <summary>The shortcode being typed: where its colon is, the name typed so far, and whether it was closed with a second colon.</summary>
    public readonly record struct Pending(int Start, string Name, bool Closed);

    private static readonly Lazy<(Dictionary<string, EmojiEntry> ByCode, List<(string Code, EmojiEntry Entry)> Ordered)> Codes = new(() =>
    {
        var byCode = new Dictionary<string, EmojiEntry>(StringComparer.Ordinal);
        var ordered = new List<(string, EmojiEntry)>();
        foreach (var group in EmojiData.Groups)
            foreach (var entry in group.Entries)
            {
                var code = Shortcode(entry);
                if (code.Length == 0 || !byCode.TryAdd(code, entry)) continue;
                ordered.Add((code, entry));
            }
        return (byCode, ordered);
    });

    /// <summary>An emoji's shortcode: its English name in lower case, anything that is not a letter or digit becoming an underscore ("thumbs up" is <c>thumbs_up</c>).</summary>
    public static string Shortcode(EmojiEntry entry)
    {
        var sb = new StringBuilder(entry.Name.Length);
        foreach (var c in entry.Name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c == '+') sb.Append("plus");
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        return sb.ToString().Trim('_');
    }

    /// <summary>The emoji with exactly this shortcode (case does not matter), or null.</summary>
    public static EmojiEntry? FromShortcode(string? code) =>
        !string.IsNullOrEmpty(code) && Codes.Value.ByCode.TryGetValue(code.ToLowerInvariant(), out var entry) ? entry : null;

    /// <summary>
    /// Emoji whose shortcode starts with what was typed, then those that contain it, then those with a keyword that starts with it; each in the order of the
    /// picker, at most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<(string Code, EmojiEntry Entry)> Complete(string? typed, int max = 8)
    {
        var prefix = (typed ?? "").ToLowerInvariant();
        if (prefix.Length == 0 || max <= 0) return Array.Empty<(string, EmojiEntry)>();
        var all = Codes.Value.Ordered;
        var found = new List<(string, EmojiEntry)>();
        void Add(Func<(string Code, EmojiEntry Entry), bool> match)
        {
            foreach (var item in all)
            {
                if (found.Count >= max) return;
                if (match(item) && !found.Any(f => f.Item1 == item.Code)) found.Add(item);
            }
        }
        Add(i => i.Code.StartsWith(prefix, StringComparison.Ordinal));
        Add(i => i.Code.Contains(prefix, StringComparison.Ordinal));
        Add(i => i.Entry.LowerKeywords.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)));
        return found;
    }

    private static bool IsCodeChar(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '+';

    /// <summary>
    /// The shortcode being typed just before the caret: a colon that starts a word (or the text), then at least two letters, digits or <c>_ - +</c>, with
    /// or without the closing colon. "12:30" and "http://" are not one, since the colon does not start a word. Null when there is none.
    /// </summary>
    public static Pending? PendingAt(string? text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return null;
        caret = Math.Clamp(caret, 0, text.Length);
        bool closed = caret > 0 && text[caret - 1] == ':';
        int from = closed ? caret - 2 : caret - 1;
        if (from < 0) return null;
        int colon = text.LastIndexOf(':', from);
        if (colon < 0 || (colon > 0 && !char.IsWhiteSpace(text[colon - 1]))) return null;
        var name = text[(colon + 1)..(closed ? caret - 1 : caret)];
        if (name.Length < (closed ? 1 : 2) || name.Length > 60 || !name.All(IsCodeChar)) return null;
        return new Pending(colon, name, closed);
    }

    /// <summary>
    /// Whether the words are nothing but one to <paramref name="max"/> emoji (spaces between are fine): such a message is shown large, the way chat apps
    /// do. A word, a number or an emoji the set does not have makes it false.
    /// </summary>
    public static bool IsOnlyEmoji(string? text, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80) return false;
        int count = 0;
        var elements = StringInfo.GetTextElementEnumerator(text.Trim());
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (element.All(char.IsWhiteSpace)) continue;
            // A plain character of ASCII (a digit, a # or a letter) is text, even where its keycap form is an emoji.
            if (element.Length == 1 && element[0] < 0x80 || EmojiData.Find(element) is null) return false;
            if (++count > max) return false;
        }
        return count > 0;
    }
}
