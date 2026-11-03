using System.Text.RegularExpressions;

namespace Yaparr.Updates;

/// <summary>One group of changes in a release, such as the new things or the fixes.</summary>
public sealed record NoteSection(string Title, IReadOnlyList<string> Items);

/// <summary>
/// Turns the text of a GitHub release, as semantic-release writes it (a heading with a compare link, groups such as "### Features",
/// and bullets that end in links to the issue and the commit), into plain groups of short lines to show in a dialog.
/// </summary>
public static partial class ReleaseNotes
{
    private const int MaxItems = 40;
    private const int MaxItemLength = 300;

    public static IReadOnlyList<NoteSection> Parse(string? markdown)
    {
        var titles = new List<string>();
        var items = new List<List<string>>();
        int current = -1;
        bool continuing = false;

        void Start(string title)
        {
            titles.Add(title);
            items.Add(new List<string>());
            current = titles.Count - 1;
        }

        void Add(string text)
        {
            if (current < 0) Start("");
            items[current].Add(text);
        }

        foreach (var raw in (markdown ?? "").ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) { continuing = false; continue; }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                int level = trimmed.TakeWhile(c => c == '#').Count();
                continuing = false;
                // The version heading ("# [1.2.0](compare link) (date)") is already said in the dialog's own words.
                if (level <= 2) continue;
                Start(Friendly(Clean(trimmed.TrimStart('#'))));
                continue;
            }

            if (trimmed.StartsWith("* ") || trimmed.StartsWith("- "))
            {
                var item = Clean(trimmed[2..]);
                if (item.Length > 0) { Add(item); continuing = true; }
                continue;
            }

            // An indented line under a bullet goes on that bullet; other lines are an item of their own.
            bool onto = continuing && char.IsWhiteSpace(line[0]) && current >= 0 && items[current].Count > 0;
            var text = Clean(trimmed, capital: !onto);
            if (text.Length == 0) continue;
            if (onto) items[current][^1] += " " + text;
            else { Add(text); continuing = false; }
        }

        var result = new List<NoteSection>();
        for (int i = 0; i < titles.Count; i++)
            if (items[i].Count > 0) result.Add(new NoteSection(titles[i], items[i].Take(MaxItems).Select(Shorten).ToList()));
        return result;
    }

    private static string Friendly(string title) => title.ToLowerInvariant() switch
    {
        "features" => "New",
        "bug fixes" => "Fixed",
        "performance improvements" => "Faster",
        "reverts" => "Taken back",
        "breaking changes" => "Changes to be aware of",
        _ => title,
    };

    // Plain words: no links (the issue and the commit that semantic-release adds at the end go), no emphasis, no code marks.
    private static string Clean(string text, bool capital = true)
    {
        text = TrailingLinks().Replace(text, "");
        text = Link().Replace(text, "$1");
        text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        text = HtmlTag().Replace(text, "");
        text = text.Trim();
        return text.Length == 0 || !capital ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string Shorten(string item) => item.Length <= MaxItemLength ? item : item[..(MaxItemLength - 1)].TrimEnd() + "…";

    // " ([#80](https://…)) ([5e86701](https://…))" at the end of a line.
    [GeneratedRegex(@"(\s*\(\[[^\]]*\]\([^)]*\)\))+\s*$")]
    private static partial Regex TrailingLinks();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();
}
