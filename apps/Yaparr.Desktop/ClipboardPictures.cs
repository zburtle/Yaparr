using System.Net;
using System.Net.Sockets;
using Yaparr.AppCore;
using System.Text.RegularExpressions;

namespace Yaparr.Desktop;

/// <summary>
/// Pictures that come with a paste but not as a file or a bitmap: the address of an image in the HTML a browser puts on the clipboard.
/// A browser's "Copy image" on an animated GIF puts only the first frame on the clipboard as a bitmap, so the GIF itself is fetched
/// from the address named in the HTML, and only for a GIF.
/// </summary>
internal static partial class ClipboardPictures
{
    [GeneratedRegex("<img\\b[^>]*?\\bsrc\\s*=\\s*(?:\"([^\"]+)\"|'([^']+)')", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImageTag();

    /// <summary>The address (or data: URL) of the first picture in the HTML of a copied selection.</summary>
    public static string? FirstImageSource(string html)
    {
        var match = ImageTag().Match(html);
        if (!match.Success) return null;
        var src = WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
        return src.Length == 0 ? null : src;
    }

    public static bool IsGifDataUrl(string src) => src.StartsWith("data:image/gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>The bytes of an image carried inside the address itself, or null.</summary>
    public static byte[]? FromDataUrl(string src, long limit)
    {
        int comma = src.IndexOf(',');
        if (comma < 0 || !src.AsSpan(0, comma).Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
        // Four characters make three bytes, so a too-long string can be refused before it is decoded.
        if ((src.Length - comma) / 4 * 3 > limit) return null;
        try { return Convert.FromBase64String(src[(comma + 1)..]); }
        catch (FormatException) { return null; }
    }

    /// <summary>A web address that names a GIF: only those are fetched, since any other picture is already on the clipboard whole.</summary>
    public static bool IsGifAddress(string src, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(src, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps) return false;
        uri = parsed;
        return parsed.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
               || parsed.AbsolutePath.Contains(".gif/", StringComparison.OrdinalIgnoreCase)
               || parsed.Query.Contains(".gif", StringComparison.OrdinalIgnoreCase);
    }

    // The connection is made by PublicWeb, so that an address inside a home or office network is refused.
    private static readonly HttpClient Client = new(PublicWeb.CreateHandler()) { Timeout = TimeSpan.FromSeconds(20) };

    static ClipboardPictures()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Yaparr");
        Client.DefaultRequestHeaders.Accept.ParseAdd("image/gif,image/*;q=0.8");
    }

    /// <summary>Downloads a GIF, or returns null if it cannot be had (not reachable, not a picture, or over the limit).</summary>
    public static async Task<byte[]?> DownloadAsync(Uri uri, long limit, CancellationToken ct)
    {
        try
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > limit) return null;
                ms.Write(buffer, 0, n);
            }
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or SocketException) { return null; }
    }
}
