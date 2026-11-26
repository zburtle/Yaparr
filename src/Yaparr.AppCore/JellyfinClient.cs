using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Yaparr.AppCore;

/// <summary>
/// What this device remembers about the Jellyfin server it is connected to: where it is, who is signed in and the token that proves it. Sealed like the
/// other files with secrets, and not part of a backup (a restored profile on another computer signs in again).
/// </summary>
public sealed class JellyfinState
{
    public string Url { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";

    /// <summary>What the server gave this device when the person signed in. Empty when signed out.</summary>
    public string Token { get; set; } = "";

    /// <summary>Names this device to the server (a random id made once, kept when signing out).</summary>
    public string DeviceId { get; set; } = "";

    public bool SignedIn => Url.Length > 0 && Token.Length > 0 && UserId.Length > 0;
}

public enum JellyfinKind { Movie, Series, Season, Episode, Other }

/// <summary>A movie, a show, a season or an episode, as the browse screens list it.</summary>
public sealed record JellyfinItem(
    string Id, string Name, JellyfinKind Kind, int? Year, long RuntimeTicks, string Overview, string? ImageTag,
    string SeriesName, string? SeriesId, int? SeasonNumber, int? Number, double PlayedPercent)
{
    public int RuntimeMinutes => (int)Math.Round(RuntimeTicks / (double)TimeSpan.TicksPerMinute);

    /// <summary>The second line under a title: the year, or for an episode its place in the show.</summary>
    public string Subtitle => Kind switch
    {
        JellyfinKind.Episode => (SeasonNumber is { } s && Number is { } n ? $"S{s}E{n}" : "") + (SeriesName.Length > 0 ? (SeasonNumber != null ? " · " : "") + SeriesName : ""),
        JellyfinKind.Season => SeriesName,
        _ => Year?.ToString() ?? "",
    };
}

public sealed record JellyfinLibrary(string Id, string Name, string CollectionType);

public sealed record JellyfinPage(IReadOnlyList<JellyfinItem> Items, int Total);

public sealed record JellyfinServerInfo(string Name, string Version, string Id);

/// <summary>The code to enter in Jellyfin (Profile, Quick Connect) and the secret that lets this device collect the answer.</summary>
public sealed record QuickConnectTicket(string Code, string Secret);

public sealed record JellyfinLogin(string UserId, string UserName, string Token);

/// <summary>Something the server refused or could not do. <see cref="Unauthorized"/> says that the token is no good any more.</summary>
public sealed class JellyfinException : Exception
{
    public HttpStatusCode? Status { get; }
    public bool Unauthorized => Status == HttpStatusCode.Unauthorized;

    public JellyfinException(string message, HttpStatusCode? status = null, Exception? inner = null) : base(message, inner) => Status = status;
}

/// <summary>
/// The part of a Jellyfin server's API that Yaparr uses to find something to watch: who the server is, signing in (with a name and password, or with Quick
/// Connect), the libraries, and what is in them. Talking to the server is done by this device only; nothing here goes to Yaparr's servers or to the people in a
/// room. A certificate that does not check out is refused, as in any browser.
/// </summary>
public sealed class JellyfinClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _deviceId;
    private string _token;
    private string _userId;

    public Uri Server { get; }

    public JellyfinClient(Uri server, string deviceId, string token = "", string userId = "", HttpClient? http = null)
    {
        Server = WithSlash(server);
        _deviceId = deviceId;
        _token = token;
        _userId = userId;
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private static Uri WithSlash(Uri u) => u.AbsolutePath.EndsWith('/') ? u : new UriBuilder(u) { Path = u.AbsolutePath + "/" }.Uri;

    /// <summary>
    /// The addresses to try for what a person typed: the one given when it has https:// or http://, and otherwise https first and then http (a server at home
    /// is usually plain http on port 8096, and the person should not have to know).
    /// </summary>
    public static IReadOnlyList<Uri> AddressesFor(string typed)
    {
        var text = typed.Trim().TrimEnd('/');
        if (text.Length == 0) return Array.Empty<Uri>();
        var list = new List<Uri>();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var given) && given.Scheme is "http" or "https" && given.Host.Length > 0) list.Add(WithSlash(given));
            return list;
        }
        foreach (var scheme in new[] { "https", "http" })
            if (Uri.TryCreate(scheme + "://" + text, UriKind.Absolute, out var u) && u.Host.Length > 0) list.Add(WithSlash(u));
        return list;
    }

    /// <summary>Finds the server a person typed the address of: the first address that answers like a Jellyfin server. Returns null when none does.</summary>
    public static async Task<(Uri Address, JellyfinServerInfo Info)?> FindServerAsync(string typed, string deviceId, HttpClient? http = null, CancellationToken ct = default)
    {
        foreach (var address in AddressesFor(typed))
        {
            using var client = new JellyfinClient(address, deviceId, http: http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) });
            try
            {
                var info = await client.GetPublicInfoAsync(ct).ConfigureAwait(false);
                return (address, info);
            }
            catch (Exception ex) when (ex is JellyfinException or HttpRequestException or TaskCanceledException or InvalidOperationException) { }
        }
        return null;
    }

    // ---- the server and signing in ----

    public async Task<JellyfinServerInfo> GetPublicInfoAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("System/Info/Public", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var id = Str(root, "Id");
        // Anything else that answers with some JSON is not a Jellyfin server.
        if (id.Length == 0 && Str(root, "ServerName").Length == 0) throw new JellyfinException("That does not look like a Jellyfin server.");
        return new JellyfinServerInfo(Str(root, "ServerName"), Str(root, "Version"), id);
    }

    public async Task<bool> QuickConnectEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "QuickConnect/Enabled", null, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            return (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException) { return false; }
    }

    public async Task<QuickConnectTicket> StartQuickConnectAsync(CancellationToken ct = default)
    {
        using var doc = await PostJsonAsync("QuickConnect/Initiate", null, ct).ConfigureAwait(false);
        var code = Str(doc.RootElement, "Code");
        var secret = Str(doc.RootElement, "Secret");
        if (code.Length == 0 || secret.Length == 0) throw new JellyfinException("The server did not give a Quick Connect code.");
        return new QuickConnectTicket(code, secret);
    }

    /// <summary>Whether someone has entered the code in Jellyfin yet.</summary>
    public async Task<bool> QuickConnectApprovedAsync(string secret, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("QuickConnect/Connect?secret=" + Uri.EscapeDataString(secret), ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("Authenticated", out var a) && a.ValueKind == JsonValueKind.True;
    }

    public async Task<JellyfinLogin> SignInWithQuickConnectAsync(string secret, CancellationToken ct = default)
    {
        using var doc = await PostJsonAsync("Users/AuthenticateWithQuickConnect", new { Secret = secret }, ct).ConfigureAwait(false);
        return TakeLogin(doc.RootElement);
    }

    public async Task<JellyfinLogin> SignInAsync(string user, string password, CancellationToken ct = default)
    {
        try
        {
            using var doc = await PostJsonAsync("Users/AuthenticateByName", new { Username = user, Pw = password }, ct).ConfigureAwait(false);
            return TakeLogin(doc.RootElement);
        }
        catch (JellyfinException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            throw new JellyfinException("The name or the password is wrong.", ex.Status, ex);
        }
    }

    private JellyfinLogin TakeLogin(JsonElement root)
    {
        var token = Str(root, "AccessToken");
        var user = root.TryGetProperty("User", out var u) ? u : default;
        var id = user.ValueKind == JsonValueKind.Object ? Str(user, "Id") : "";
        var name = user.ValueKind == JsonValueKind.Object ? Str(user, "Name") : "";
        if (token.Length == 0 || id.Length == 0) throw new JellyfinException("The server did not sign this device in.");
        _token = token;
        _userId = id;
        return new JellyfinLogin(id, name, token);
    }

    /// <summary>Signs this device out on the server, so the token stops working there too. A server that cannot be reached is no reason to stay signed in here.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        try { using var _ = await SendAsync(HttpMethod.Post, "Sessions/Logout", null, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        _token = "";
    }

    // ---- what is in it ----

    public async Task<IReadOnlyList<JellyfinLibrary>> GetLibrariesAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"UserViews?userId={_userId}", ct).ConfigureAwait(false);
        var list = new List<JellyfinLibrary>();
        if (doc.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var e in items.EnumerateArray())
            {
                var type = Str(e, "CollectionType");
                // Only what has films and shows in it: music, books and photos are not something to watch.
                if (type is "movies" or "tvshows" or "homevideos" or "boxsets" or "") list.Add(new JellyfinLibrary(Str(e, "Id"), Str(e, "Name"), type));
            }
        return list;
    }

    private const string Fields = "Overview,ProductionYear,RunTimeTicks";

    /// <summary>The movies and shows of a library (or of all of them), by name; with <paramref name="search"/> only those that match.</summary>
    public Task<JellyfinPage> GetItemsAsync(string? libraryId = null, string? search = null, int start = 0, int limit = 60, CancellationToken ct = default)
    {
        var q = new StringBuilder($"Items?userId={_userId}&recursive=true&includeItemTypes=Movie,Series&sortBy=SortName&sortOrder=Ascending&fields={Fields}&enableImageTypes=Primary&startIndex={start}&limit={limit}");
        if (!string.IsNullOrEmpty(libraryId)) q.Append("&parentId=").Append(Uri.EscapeDataString(libraryId));
        if (!string.IsNullOrWhiteSpace(search)) q.Append("&searchTerm=").Append(Uri.EscapeDataString(search.Trim()));
        return GetPageAsync(q.ToString(), ct);
    }

    /// <summary>What was started and not finished (Jellyfin's "continue watching").</summary>
    public async Task<JellyfinPage> GetResumeAsync(int limit = 30, CancellationToken ct = default)
    {
        var query = $"mediaTypes=Video&includeItemTypes=Movie,Episode&fields={Fields}&limit={limit}";
        try { return await GetPageAsync($"UserItems/Resume?userId={_userId}&{query}", ct).ConfigureAwait(false); }
        // Servers before 10.9 only know the older address.
        catch (JellyfinException ex) when (ex.Status == HttpStatusCode.NotFound) { return await GetPageAsync($"Users/{_userId}/Items/Resume?{query}", ct).ConfigureAwait(false); }
    }

    public Task<JellyfinPage> GetSeasonsAsync(string seriesId, CancellationToken ct = default) =>
        GetPageAsync($"Shows/{Uri.EscapeDataString(seriesId)}/Seasons?userId={_userId}&fields={Fields}", ct);

    public Task<JellyfinPage> GetEpisodesAsync(string seriesId, string seasonId, CancellationToken ct = default) =>
        GetPageAsync($"Shows/{Uri.EscapeDataString(seriesId)}/Episodes?userId={_userId}&seasonId={Uri.EscapeDataString(seasonId)}&fields={Fields}", ct);

    /// <summary>The poster of an item, scaled by the server to about this wide; null when it has none or cannot be fetched.</summary>
    public async Task<byte[]?> GetPosterAsync(string itemId, string? tag, int maxWidth, CancellationToken ct = default)
    {
        if (tag == null) return null;
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"Items/{Uri.EscapeDataString(itemId)}/Images/Primary?maxWidth={maxWidth}&tag={Uri.EscapeDataString(tag)}&quality=85", null, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    private async Task<JellyfinPage> GetPageAsync(string path, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(path, ct).ConfigureAwait(false);
        return ParsePage(doc.RootElement);
    }

    internal static JellyfinPage ParsePage(JsonElement root)
    {
        var list = new List<JellyfinItem>();
        if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var e in items.EnumerateArray())
                if (ParseItem(e) is { } item) list.Add(item);
        int total = root.TryGetProperty("TotalRecordCount", out var t) && t.TryGetInt32(out var n) ? n : list.Count;
        return new JellyfinPage(list, total);
    }

    internal static JellyfinItem? ParseItem(JsonElement e)
    {
        var id = Str(e, "Id");
        if (id.Length == 0) return null;
        var kind = Str(e, "Type") switch { "Movie" => JellyfinKind.Movie, "Series" => JellyfinKind.Series, "Season" => JellyfinKind.Season, "Episode" => JellyfinKind.Episode, _ => JellyfinKind.Other };
        string? tag = null;
        if (e.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("Primary", out var p) && p.ValueKind == JsonValueKind.String) tag = p.GetString();
        double played = 0;
        if (e.TryGetProperty("UserData", out var ud) && ud.ValueKind == JsonValueKind.Object && ud.TryGetProperty("PlayedPercentage", out var pp) && pp.TryGetDouble(out var d)) played = d;
        return new JellyfinItem(id, Str(e, "Name"), kind, Int(e, "ProductionYear"), Long(e, "RunTimeTicks"), Str(e, "Overview"), tag,
            Str(e, "SeriesName"), e.TryGetProperty("SeriesId", out _) ? Str(e, "SeriesId") : null, Int(e, "ParentIndexNumber"), Int(e, "IndexNumber"), played);
    }

    // ---- the requests ----

    private string Authorization()
    {
        string Clean(string s) => s.Replace("\"", "").Replace(",", " ");
        var header = $"MediaBrowser Client=\"Yaparr\", Device=\"{Clean(Environment.MachineName)}\", DeviceId=\"{Clean(_deviceId)}\", Version=\"{typeof(JellyfinClient).Assembly.GetName().Version?.ToString(3) ?? "1"}\"";
        return _token.Length > 0 ? header + $", Token=\"{Clean(_token)}\"" : header;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(Server, path));
        request.Headers.TryAddWithoutValidation("Authorization", Authorization());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        else if (method == HttpMethod.Post) request.Content = new StringContent("", Encoding.UTF8, "application/json");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var why = response.StatusCode == HttpStatusCode.Unauthorized ? "The server did not accept this sign-in." : $"The server answered {(int)response.StatusCode}.";
                throw new JellyfinException(why, response.StatusCode);
            }
            try { return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false); }
            catch (JsonException ex) { throw new JellyfinException("That does not look like a Jellyfin server.", response.StatusCode, ex); }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct) =>
        await ReadAsync(await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    private async Task<JsonDocument> PostJsonAsync(string path, object? body, CancellationToken ct) =>
        await ReadAsync(await SendAsync(HttpMethod.Post, path, body, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
