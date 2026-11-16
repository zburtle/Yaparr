using System.Text.RegularExpressions;

namespace Yaparr.Chat;

/// <summary>
/// A custom emoji a message uses: a small picture written as <c>:name:</c> in the words. The message carries what is needed to show it (the picture's
/// hash and type, as an attachment is described), so everyone sees the same picture whether it came from the sender's own emoji or from a room's, and
/// the file is fetched like a picture, from the sender or anyone else who has it.
/// </summary>
public sealed record CustomEmoji(string Name, string Hash, int Size, string Mime, int Width, int Height)
{
    /// <summary>The picture as an attachment, which is how it is fetched and stored.</summary>
    public Attachment AsAttachment() => new(Hash, Name, Size, Mime, Width, Height);
}

/// <summary>What a custom emoji can be: how it is named, how big it can be, and how to find the ones a text uses.</summary>
public static partial class CustomEmojiRules
{
    /// <summary>The largest file of a custom emoji (it is shown a line high, so there is no need for more).</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>The largest side of a custom emoji in pixels.</summary>
    public const int MaxSide = 256;

    /// <summary>At most this many different custom emoji in one message.</summary>
    public const int MaxPerMessage = 10;

    public const int MinNameLength = 2;
    public const int MaxNameLength = 32;

    private static readonly string[] Mimes = { "image/png", "image/gif", "image/webp", "image/jpeg" };

    // :name: where the colons do not touch a letter or digit on the outside, so "12:30:45" is not an emoji called "30".
    [GeneratedRegex(@"(?<![A-Za-z0-9]):([a-z0-9_]{2,32}):(?![A-Za-z0-9])")]
    private static partial Regex Token();

    /// <summary>The name for what was typed (spaces trimmed, lower case, colons dropped), or null if it is not a usable name: two to 32 letters, digits or underscores.</summary>
    public static string? CleanName(string? typed)
    {
        var name = (typed ?? "").Trim().Trim(':').ToLowerInvariant();
        return IsValidName(name) ? name : null;
    }

    public static bool IsValidName(string? name) =>
        name is { Length: >= MinNameLength and <= MaxNameLength } && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    /// <summary>The names written as <c>:name:</c> in a text, each once, in order of appearance.</summary>
    public static IReadOnlyList<string> NamesIn(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var names = new List<string>();
        foreach (Match m in Token().Matches(text))
            if (!names.Contains(m.Groups[1].Value)) names.Add(m.Groups[1].Value);
        return names;
    }

    /// <summary>
    /// The text cut at its <c>:name:</c>s: pieces of words (<c>Name</c> null) and the names written (<c>Name</c> set, <c>Text</c> the whole <c>:name:</c>), in order.
    /// Whether a name is an emoji anyone has is for the caller to say; one that is not is shown as the words it is.
    /// </summary>
    public static IReadOnlyList<(string Text, string? Name)> Pieces(string? text)
    {
        var pieces = new List<(string, string?)>();
        if (string.IsNullOrEmpty(text)) return pieces;
        int at = 0;
        foreach (Match m in Token().Matches(text))
        {
            if (m.Index > at) pieces.Add((text[at..m.Index], null));
            pieces.Add((m.Value, m.Groups[1].Value));
            at = m.Index + m.Length;
        }
        if (at < text.Length) pieces.Add((text[at..], null));
        return pieces;
    }

    /// <summary>Whether a text is nothing but custom emoji and standard emoji: one to <paramref name="max"/> of them, spaces between, and at least one custom.</summary>
    public static bool IsOnlyEmoji(string? text, IReadOnlyCollection<string> known, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return false;
        int count = 0, custom = 0;
        var rest = Token().Replace(text, m =>
        {
            if (!known.Contains(m.Groups[1].Value)) return m.Value;
            count++;
            custom++;
            return " ";
        });
        if (custom == 0) return false;
        // What is left must be spaces or standard emoji, counted together with the custom ones.
        foreach (var piece in rest.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!EmojiTyping.IsOnlyEmoji(piece, max)) return false;
            count += new System.Globalization.StringInfo(piece).LengthInTextElements;
        }
        return count >= 1 && count <= max;
    }

    /// <summary>
    /// Reads a picture for a custom emoji. False, with what to tell the person, when it is not a PNG, GIF, WebP or JPEG, or is bigger than a custom
    /// emoji can be (<see cref="MaxBytes"/> and <see cref="MaxSide"/> pixels on a side).
    /// </summary>
    public static bool TryRead(byte[] data, out ImageInfo info, out string problem)
    {
        info = new ImageInfo("", 0, 0, 0);
        problem = "";
        if (data.Length == 0) { problem = "That file is empty."; return false; }
        if (!ImageSniffer.TryRead(data, out var found) || !Mimes.Contains(found.Mime)) { problem = "That is not a PNG, GIF, WebP or JPEG picture."; return false; }
        if (data.Length > MaxBytes) { problem = $"That picture is {data.Length / 1024} KB; an emoji can be at most {MaxBytes / 1024} KB."; return false; }
        if (found.Width > MaxSide || found.Height > MaxSide) { problem = $"That picture is {found.Width}x{found.Height}; an emoji can be at most {MaxSide}x{MaxSide}."; return false; }
        info = found;
        return true;
    }

    /// <summary>Whether a custom emoji read from a message or a room's list is within the limits (the same ones a new one is held to).</summary>
    public static bool IsAcceptable(CustomEmoji e) =>
        IsValidName(e.Name) && e.Hash is { Length: 64 } && e.Hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        && e.Size is >= 1 and <= MaxBytes && Mimes.Contains(e.Mime) && e.Width is >= 1 and <= MaxSide && e.Height is >= 1 and <= MaxSide;
}
