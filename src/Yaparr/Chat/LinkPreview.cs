using System.Text;
using System.Text.RegularExpressions;

namespace Yaparr.Chat;

/// <summary>
/// A card for a link in a message: the title, a short description and a small picture of the page. The sender's app fetches the page and
/// attaches this to the message (inside the signed envelope, see <see cref="MessageEnvelope"/>), so the people reading it never contact the
/// site and it never sees their address. The link itself stays in the words of the message; this is shown below it.
/// Whoever sent it chose what it says, so a reader should treat it as the sender's word about the page and show where the link really goes.
/// </summary>
/// <param name="Url">The link the card is for: the first web address in the words, as written.</param>
public sealed partial record LinkPreview(string Url, string Title, string Description = "", byte[]? Thumbnail = null)
{
    public const int MaxUrl = 600;
    public const int MaxTitle = 120;
    public const int MaxDescription = 220;
    public const int MaxThumbnailBytes = 3000;
    public const int MaxThumbnailDimension = 128;

    /// <summary>The site the link goes to, for showing next to the card (without www.).</summary>
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host) : "";

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    /// <summary>
    /// The first web address in some words, as it was written (without the full stop, bracket or comma that ends a sentence), or null.
    /// Only https and http, with a host, and not longer than <see cref="MaxUrl"/>.
    /// </summary>
    public static string? FirstLink(string text)
    {
        foreach (Match m in LinkPattern().Matches(text))
        {
            var candidate = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '*');
            if (candidate.Length is > 0 and <= MaxUrl && IsWebAddress(candidate)) return candidate;
        }
        return null;
    }

    public static bool IsWebAddress(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>Tidies the words of a card: no control characters or line breaks, one space between words, cut to <paramref name="max"/>.</summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        bool space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; if (sb.Length >= max) break; }
            sb.Append(c);
            if (sb.Length >= max) break;
        }
        var clean = sb.ToString().Trim();
        return clean.Length < max || clean.Length == 0 ? clean : clean[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>The same card with every part held to its limit, or null when it cannot be one (no web address, or nothing to say).</summary>
    public LinkPreview? Normalized()
    {
        var url = Url?.Trim() ?? "";
        if (url.Length > MaxUrl || !IsWebAddress(url)) return null;
        var title = Clean(Title, MaxTitle);
        var description = Clean(Description, MaxDescription);
        if (title.Length == 0 && description.Length == 0) return null;
        var thumb = Thumbnail is { Length: > 0 and <= MaxThumbnailBytes } t
                    && ImageSniffer.TryRead(t, out var info) && info.Frames == 1 && info.Width <= MaxThumbnailDimension && info.Height <= MaxThumbnailDimension
            ? t : null;
        return new LinkPreview(url, title, description, thumb);
    }
}
