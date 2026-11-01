using System.Text.RegularExpressions;

namespace Yaparr.Diagnostics;

/// <summary>
/// Makes text safe to paste into a bug report: other people's addresses and ids are shortened and anything that looks like a key or a
/// password is removed. The logs are not meant to hold secrets; this is a second line of defence.
/// </summary>
public static partial class LogScrubber
{
    // A peer or account id: 64 hex digits. Eight are enough to tell people apart, as everywhere else in the app.
    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex FullId();

    // Anything else that is long and made of key-like characters (an invite, a secret, a token).
    [GeneratedRegex(@"[A-Za-z0-9+/_=\-]{40,}")]
    private static partial Regex LongToken();

    [GeneratedRegex(@"(?i)\b(password|passwd|credential|secret|token|private ?key|preshared ?key|username)\b(\s*[:=]\s*)\S+")]
    private static partial Regex NamedSecret();

    [GeneratedRegex(@"\b(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b")]
    private static partial Regex IPv4();

    // At least four groups, or a "::" gap, so that a time of day such as 12:34:56 is left alone.
    [GeneratedRegex(@"(?<![0-9A-Za-z:])(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){3,7}|(?:[0-9A-Fa-f]{1,4}:){1,6}:(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,5})?|::(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,6}))(?![0-9A-Za-z:])")]
    private static partial Regex IPv6();

    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = NamedSecret().Replace(text, "$1$2[removed]");
        text = FullId().Replace(text, m => m.Value[..8] + "…");
        // A long file path also looks like this; keys have digits and at most a slash or two, paths have more slashes.
        text = LongToken().Replace(text, m => m.Value.Any(char.IsDigit) && m.Value.Count(c => c == '/') <= 2 ? "[removed]" : m.Value);
        // The first two numbers still say which network it is (useful for a bug), the rest says where the person is.
        text = IPv4().Replace(text, "$1.$2.x.x");
        text = IPv6().Replace(text, m => m.Value.StartsWith(':') ? "::x" : m.Value.Split(':')[0] + ":x");
        return text;
    }
}
