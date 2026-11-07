using System.Reflection;
using System.Text;

namespace Yaparr.Chat;

/// <summary>One emoji of the set reactions can use, with its name and the words people search it by.</summary>
public sealed class EmojiEntry
{
    public EmojiEntry(string emoji, string name, IReadOnlyList<string> keywords)
    {
        Emoji = emoji;
        Name = name;
        Keywords = keywords;
        NameWords = name.ToLowerInvariant().Split(new[] { ' ', '-', ':', ',', '\'', '’', '(', ')', '.' }, StringSplitOptions.RemoveEmptyEntries);
        LowerName = name.ToLowerInvariant();
        LowerKeywords = keywords.Select(k => k.ToLowerInvariant()).ToArray();
    }

    /// <summary>The emoji, in its fully qualified form (with the variation selector where there is one).</summary>
    public string Emoji { get; }

    /// <summary>The name in English, for example "thumbs up".</summary>
    public string Name { get; }

    public IReadOnlyList<string> Keywords { get; }

    internal string LowerName { get; }
    internal string[] NameWords { get; }
    internal string[] LowerKeywords { get; }
}

public sealed record EmojiGroup(string Name, IReadOnlyList<EmojiEntry> Entries);

/// <summary>
/// The emoji that reactions can use: Unicode's emoji up to a fixed version (13.1: older systems draw newer ones as an empty box), with their English
/// names and keywords from CLDR (see <c>tools/emoji/build_emoji_data.py</c>, which makes <c>emoji.tsv</c> from Unicode's files). A reaction is the
/// emoji itself on the wire; a receiver accepts only these, so a reaction cannot be any string. <see cref="Quick"/> are the ones offered first.
/// </summary>
public static class EmojiData
{
    /// <summary>The longest reaction, in UTF-16 characters (a family or a couple with skin tones is about thirty).</summary>
    public const int MaxLength = 40;

    private const char VariationSelector = '️';

    /// <summary>The thirteen offered first, in this order: the most used in chat (👍 ❤️ 😂 🔥 🙏 😭 😍 🎉 👀 🤔 😮 👎) and 💯.</summary>
    public static IReadOnlyList<string> Quick { get; } = new[]
    {
        "\U0001F44D",             // thumbs up
        "\U0001F4AF",             // hundred points
        "❤️",           // red heart
        "\U0001F602",             // face with tears of joy
        "\U0001F62D",             // loudly crying face
        "\U0001F525",             // fire
        "\U0001F64F",             // folded hands
        "\U0001F60D",             // smiling face with heart-eyes
        "\U0001F389",             // party popper
        "\U0001F440",             // eyes
        "\U0001F914",             // thinking face
        "\U0001F62E",             // face with open mouth
        "\U0001F44E",             // thumbs down
    };

    private sealed class Data
    {
        public List<EmojiGroup> Groups { get; } = new();
        public Dictionary<string, EmojiEntry> ByStripped { get; } = new(StringComparer.Ordinal);
    }

    private static readonly Lazy<Data> Loaded = new(Load);

    /// <summary>The emoji by group (smileys, people, animals and so on), in the order a picker shows them.</summary>
    public static IReadOnlyList<EmojiGroup> Groups => Loaded.Value.Groups;

    /// <summary>How many emoji can be used.</summary>
    public static int Count => Loaded.Value.ByStripped.Count;

    private static Data Load()
    {
        var data = new Data();
        var assembly = typeof(EmojiData).Assembly;
        using var stream = assembly.GetManifestResourceStream("Yaparr.Chat.Emoji.emoji.tsv")
                           ?? throw new InvalidOperationException("The emoji data is missing from the library.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? groupName = null;
        List<EmojiEntry>? entries = null;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '@')
            {
                groupName = line[1..];
                entries = new List<EmojiEntry>();
                data.Groups.Add(new EmojiGroup(groupName, entries));
                continue;
            }
            var parts = line.Split('\t');
            if (parts.Length < 2 || entries == null) continue;
            var keywords = parts.Length > 2 && parts[2].Length > 0 ? parts[2].Split('|') : Array.Empty<string>();
            var entry = new EmojiEntry(parts[0], parts[1], keywords);
            entries.Add(entry);
            data.ByStripped[Strip(parts[0])] = entry;
        }
        return data;
    }

    private static bool IsTone(char high, char low) => high == '\uD83C' && low is >= '\uDFFB' and <= '\uDFFF';

    // The emoji without its variation selectors and skin tones: what identifies it whichever way it was written.
    private static string Strip(string emoji, out int tones)
    {
        tones = 0;
        var sb = new StringBuilder(emoji.Length);
        for (int i = 0; i < emoji.Length; i++)
        {
            char c = emoji[i];
            if (c == VariationSelector) continue;
            if (i + 1 < emoji.Length && IsTone(c, emoji[i + 1])) { tones++; i++; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Strip(string emoji) => Strip(emoji, out _);

    /// <summary>
    /// Whether this is an emoji reactions can use. <paramref name="emoji"/> is then the same emoji in its standard form (the variation selector where it
    /// belongs); one with skin tones is accepted when the emoji without them is in the set. Anything else, text included, is refused.
    /// </summary>
    public static bool TryNormalize(string? text, out string emoji)
    {
        emoji = "";
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return false;
        var stripped = Strip(text, out int tones);
        if (!Loaded.Value.ByStripped.TryGetValue(stripped, out var entry)) return false;
        emoji = tones == 0 ? entry.Emoji : text;
        return true;
    }

    /// <summary>The entry for an emoji, or null (a skin tone variant is the entry of the emoji without the tone).</summary>
    public static EmojiEntry? Find(string emoji) =>
        !string.IsNullOrEmpty(emoji) && emoji.Length <= MaxLength && Loaded.Value.ByStripped.TryGetValue(Strip(emoji), out var entry) ? entry : null;

    /// <summary>
    /// Emoji matching what was typed: every word of the query has to start a word of the name or of a keyword (or be a part of the name). The best
    /// matches come first: the name itself, the name starting with the query, then words of the name, then keywords.
    /// </summary>
    public static IReadOnlyList<EmojiEntry> Search(string? query, int max = 80)
    {
        var words = (query ?? "").ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Array.Empty<EmojiEntry>();
        var found = new List<(int Score, int Order, EmojiEntry Entry)>();
        int order = 0;
        foreach (var group in Groups)
            foreach (var entry in group.Entries)
            {
                order++;
                int total = 0;
                bool all = true;
                foreach (var word in words)
                {
                    int score = ScoreWord(entry, word);
                    if (score < 0) { all = false; break; }
                    total += score;
                }
                if (all) found.Add((total + (entry.LowerName == string.Join(' ', words) ? -10 : 0), order, entry));
            }
        return found.OrderBy(f => f.Score).ThenBy(f => f.Order).Take(max).Select(f => f.Entry).ToList();
    }

    // -1: no match. Lower is better.
    private static int ScoreWord(EmojiEntry entry, string word)
    {
        if (entry.LowerName.StartsWith(word, StringComparison.Ordinal)) return 0;
        if (entry.NameWords.Any(w => w == word)) return 1;
        if (entry.NameWords.Any(w => w.StartsWith(word, StringComparison.Ordinal))) return 2;
        if (entry.LowerKeywords.Any(k => k == word)) return 3;
        if (entry.LowerKeywords.Any(k => k.StartsWith(word, StringComparison.Ordinal))) return 4;
        if (word.Length >= 3 && entry.LowerName.Contains(word, StringComparison.Ordinal)) return 5;
        return -1;
    }
}
