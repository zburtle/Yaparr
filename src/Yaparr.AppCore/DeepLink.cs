using Yaparr.Chat;

namespace Yaparr.AppCore;

internal enum DeepLinkKind { Join, Chat, BackupSignIn }

/// <summary>
/// A link to a room or a person, in either of two forms that mean the same:
/// <c>yaparr://join/&lt;invite&gt;</c> and <c>yaparr://chat/&lt;peer id&gt;</c> (what the operating system hands to the app), and
/// <c>https://yaparr.com/join#&lt;invite&gt;</c> and <c>https://yaparr.com/chat#&lt;peer id&gt;</c> (what people share).
/// The https form keeps the secret in the fragment, which a browser never sends to the site. A link is only ever a request:
/// nothing is joined or opened until the person has agreed to it.
///
/// A third, <c>yaparr://backup-signin/&lt;code&gt;/&lt;server, escaped&gt;</c>, is the button on a backup server's "You're in" page: it asks the app to sign
/// in to that server (see <see cref="TryGetBackupSignIn"/>). It only exists as the app form: there is no page on yaparr.com for it.
/// </summary>
internal sealed record DeepLink(DeepLinkKind Kind, string Value)
{
    internal const string Site = "https://yaparr.com";
    internal const string Scheme = "yaparr://";

    // The longest valid value is a rotated room key, 115 characters; anything much longer is not one of ours.
    private const int MaxLength = 400;

    /// <summary>Reads a link, or returns null for anything that is not a well-formed Yaparr link.</summary>
    public static DeepLink? Parse(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return null;

        string rest;
        char separator;
        if (text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) { rest = text[Scheme.Length..]; separator = '/'; }
        else if (text.StartsWith(Site + "/", StringComparison.OrdinalIgnoreCase)) { rest = text[(Site.Length + 1)..]; separator = '#'; }
        else return null;

        int cut = rest.IndexOf(separator);
        if (cut <= 0) return null;
        var verb = rest[..cut];
        var value = rest[(cut + 1)..].Trim('/');

        if (verb.Equals("join", StringComparison.OrdinalIgnoreCase))
        {
            try { RoomKey.Parse(value); }
            catch (FormatException) { return null; }
            return new DeepLink(DeepLinkKind.Join, value);
        }
        if (verb.Equals("backup-signin", StringComparison.OrdinalIgnoreCase))
        {
            // Only as the app link the operating system hands over, never as a link anyone shares from the web site.
            if (separator != '/') return null;
            int slash = value.IndexOf('/');
            if (slash <= 0) return null;
            var code = value[..slash];
            if (code.Length is < 20 or > 100 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
            string typed;
            try { typed = Uri.UnescapeDataString(value[(slash + 1)..]); }
            catch (UriFormatException) { return null; }
            // The server is a bare address (https, or http on this computer): nothing after the host that could point somewhere else.
            if (BackupServerClient.ParseServerAddress(typed) is not { } server) return null;
            return new DeepLink(DeepLinkKind.BackupSignIn, $"{code}/{server.ToString().TrimEnd('/')}");
        }
        if (verb.Equals("chat", StringComparison.OrdinalIgnoreCase))
        {
            value = value.ToLowerInvariant();
            return value.Length == 64 && value.All(Uri.IsHexDigit) ? new DeepLink(DeepLinkKind.Chat, value) : null;
        }
        return null;
    }

    /// <summary>The first command-line argument that is a Yaparr link: how the operating system opens one.</summary>
    public static DeepLink? FromArgs(IEnumerable<string> args) =>
        args.Where(a => a.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)).Select(Parse).FirstOrDefault(l => l != null);

    /// <summary>The form that is handed from one running copy of the app to another.</summary>
    public string ToAppUri()
    {
        if (Kind == DeepLinkKind.BackupSignIn && TryGetBackupSignIn(out var code, out var server))
            return $"{Scheme}backup-signin/{code}/{Uri.EscapeDataString(server.ToString().TrimEnd('/'))}";
        return $"{Scheme}{(Kind == DeepLinkKind.Join ? "join" : "chat")}/{Value}";
    }

    /// <summary>For a backup-signin link: the one-time code and the server to trade it with.</summary>
    public bool TryGetBackupSignIn(out string code, out Uri server)
    {
        code = "";
        server = null!;
        if (Kind != DeepLinkKind.BackupSignIn) return false;
        int slash = Value.IndexOf('/');
        if (slash <= 0 || BackupServerClient.ParseServerAddress(Value[(slash + 1)..]) is not { } found) return false;
        code = Value[..slash];
        server = found;
        return true;
    }

    /// <summary>The link to share for a room: its invite goes after the #, so it never reaches the web site.</summary>
    public static string RoomLink(string invite) => $"{Site}/join#{invite}";

    /// <summary>The link to share for a person.</summary>
    public static string ChatLink(string peerId) => $"{Site}/chat#{peerId}";
}
