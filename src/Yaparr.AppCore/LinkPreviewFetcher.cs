using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>
/// Makes the card (<see cref="LinkPreview"/>) for a link that is about to be sent: reads the page's title, description and picture
/// (the Open Graph tags that sites put there for this) and shrinks the picture so that it travels inside the message.
/// This is the only place the page is requested, and it is the sender's app that does it; the readers never contact the site.
/// </summary>
internal sealed partial class LinkPreviewFetcher
{
    /// <summary>How much of a page is read: the tags are at the top, so there is no need for the rest.</summary>
    public const int MaxPageBytes = 512 * 1024;
    public const int MaxPictureBytes = 2 * 1024 * 1024;

    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private readonly HttpClient _http;

    /// <param name="http">For tests; the app uses a client that refuses addresses inside the local network (<see cref="PublicWeb"/>).</param>
    public LinkPreviewFetcher(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(PublicWeb.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };
        if (http == null)
        {
            // Sent like a browser: many news sites turn away anything that says it is a program, even for one page the person just linked.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        }
    }

    /// <summary>The card for a link, or null when there is none to make (the page has nothing to show, is not a page, or could not be reached in time).</summary>
    public async Task<LinkPreview?> FetchAsync(string url, CancellationToken ct)
    {
        if (!LinkPreview.IsWebAddress(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        // An invitation to join a room or a chat is Yaparr's own link, not a page to describe.
        if (uri.Host.EndsWith("yaparr.com", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.1");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.Equals("text/html", StringComparison.OrdinalIgnoreCase) && !type.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)) return null;

            var html = await ReadPageAsync(response, ct).ConfigureAwait(false);
            var final = response.RequestMessage?.RequestUri ?? uri;
            var found = Read(html, final);
            if (found.Title.Length == 0 && found.Description.Length == 0) return null;

            byte[]? thumbnail = null;
            if (found.Image != null) thumbnail = await FetchPictureAsync(found.Image, ct).ConfigureAwait(false);
            return new LinkPreview(url, found.Title, found.Description, thumbnail).Normalized();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string> ReadPageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while (ms.Length < MaxPageBytes && (n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            ms.Write(buffer, 0, (int)Math.Min(n, MaxPageBytes - ms.Length));
        var encoding = Encoding.UTF8;
        try { if (response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset) encoding = Encoding.GetEncoding(charset.Trim('"')); }
        catch (ArgumentException) { }
        return encoding.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private async Task<byte[]?> FetchPictureAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("image/jpeg,image/png,image/webp,image/gif;q=0.8,image/*;q=0.5");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxPictureBytes) return null;
            if (response.Content.Headers.ContentType?.MediaType is { } t && !t.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[32 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > MaxPictureBytes) return null;
                ms.Write(buffer, 0, n);
            }
            return ImageTools.CardThumbnail(ms.ToArray());
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    public sealed record Page(string Title, string Description, Uri? Image);

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaTag();

    [GeneratedRegex("""([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+))""", RegexOptions.Singleline)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<!--.*?-->|<script\b.*?</script>|<style\b.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Skipped();

    /// <summary>The title, description and picture a page names for itself. The Open Graph tags win; the page's own title and description are the fallback.</summary>
    public static Page Read(string html, Uri baseUri)
    {
        // Only the top of the page matters.
        var head = html;
        int bodyAt = head.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyAt > 0) head = head[..bodyAt];
        head = Skipped().Replace(head, "");

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match meta in MetaTag().Matches(head))
        {
            string? key = null, content = null;
            foreach (Match a in Attribute().Matches(meta.Value))
            {
                var name = a.Groups[1].Value.ToLowerInvariant();
                var value = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                if (name is "property" or "name") key ??= value.Trim().ToLowerInvariant();
                else if (name == "content") content = value;
            }
            if (key != null && content != null) tags.TryAdd(key, WebUtility.HtmlDecode(content));
        }

        string Pick(params string[] keys) => keys.Select(k => tags.TryGetValue(k, out var v) ? LinkPreview.Clean(v, 2000) : "").FirstOrDefault(v => v.Length > 0) ?? "";

        var title = Pick("og:title", "twitter:title");
        if (title.Length == 0 && TitleTag().Match(head) is { Success: true } t) title = LinkPreview.Clean(WebUtility.HtmlDecode(t.Groups[1].Value), 2000);
        var description = Pick("og:description", "twitter:description", "description");

        Uri? image = null;
        var source = Pick("og:image", "og:image:url", "og:image:secure_url", "twitter:image", "twitter:image:src");
        if (source.Length > 0 && Uri.TryCreate(baseUri, source, out var resolved) && resolved.Scheme is "http" or "https") image = resolved;
        return new Page(LinkPreview.Clean(title, LinkPreview.MaxTitle), LinkPreview.Clean(description, LinkPreview.MaxDescription), image);
    }
}
