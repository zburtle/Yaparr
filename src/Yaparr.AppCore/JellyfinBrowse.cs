using System.Text.Json;

namespace Yaparr.AppCore;

/// <summary>
/// What the Jellyfin window needs from a library to show it: the same whether the library is the person's own server (<see cref="JellyfinClient"/>) or the streamer's, reached
/// through the streamer's app (<see cref="RemoteJellyfinLibrary"/>), so that one window serves both.
/// </summary>
public interface IJellyfinLibrary : IDisposable
{
    Task<IReadOnlyList<JellyfinLibrary>> GetLibrariesAsync(CancellationToken ct = default);
    Task<JellyfinPage> GetItemsAsync(string? libraryId = null, string? search = null, int start = 0, int limit = 60, CancellationToken ct = default);
    Task<JellyfinPage> GetResumeAsync(int limit = 30, CancellationToken ct = default);
    Task<JellyfinPage> GetSeasonsAsync(string seriesId, CancellationToken ct = default);
    Task<JellyfinPage> GetEpisodesAsync(string seriesId, string seasonId, CancellationToken ct = default);
    Task<byte[]?> GetPosterAsync(string itemId, string? tag, int maxWidth, CancellationToken ct = default);
}

/// <summary>A question about the streamer's library, as it goes over the room.</summary>
internal sealed record BrowseQuestion(string Op, string? A = null, string? B = null, int Start = 0, int Limit = 60, int Width = 0);

/// <summary>
/// Answers the questions others ask about the library the person streams from (see <see cref="RemoteJellyfinLibrary"/>): the streaming app reads what is asked, asks its own
/// Jellyfin client, and sends the answer back as text. Only these few things can be asked (the libraries, a page of items, the seasons and episodes of a show, a poster);
/// everything is read-only, and what comes back is cut to sizes that suit a list.
/// </summary>
public static class JellyfinBrowseServer
{
    internal const int MaxPage = 60, MaxPoster = 300 * 1024, MaxOverview = 300, MaxPosterWidth = 300;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The answer to a question, as text. A question that makes no sense, or fails on the server, is answered with an error message.</summary>
    public static async Task<string> AnswerAsync(IJellyfinLibrary library, string question, CancellationToken ct = default)
    {
        try
        {
            var q = JsonSerializer.Deserialize<BrowseQuestion>(question, Json) ?? throw new JsonException();
            int limit = Math.Clamp(q.Limit, 1, MaxPage), start = Math.Max(0, q.Start);
            switch (q.Op)
            {
                case "libraries":
                    return JsonSerializer.Serialize(new { libraries = await library.GetLibrariesAsync(ct).ConfigureAwait(false) }, Json);
                case "items":
                    return Page(await library.GetItemsAsync(q.A, q.B, start, limit, ct).ConfigureAwait(false));
                case "resume":
                    return Page(await library.GetResumeAsync(limit, ct).ConfigureAwait(false));
                case "seasons" when !string.IsNullOrEmpty(q.A):
                    return Page(await library.GetSeasonsAsync(q.A, ct).ConfigureAwait(false));
                case "episodes" when !string.IsNullOrEmpty(q.A) && !string.IsNullOrEmpty(q.B):
                    return Page(await library.GetEpisodesAsync(q.A, q.B, ct).ConfigureAwait(false));
                case "poster" when !string.IsNullOrEmpty(q.A):
                    var bytes = await library.GetPosterAsync(q.A, q.B, Math.Clamp(q.Width, 32, MaxPosterWidth), ct).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { poster = bytes != null && bytes.Length <= MaxPoster ? Convert.ToBase64String(bytes) : null }, Json);
                default:
                    return Error("That cannot be asked.");
            }
        }
        catch (JsonException) { return Error("That cannot be asked."); }
        catch (JellyfinException ex) { return Error(ex.Unauthorized ? "The streamer's Jellyfin sign-in has run out." : ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return Error("The streamer's server could not be reached."); }
    }

    private static string Page(JellyfinPage page) =>
        JsonSerializer.Serialize(new { items = page.Items.Select(i => i.Overview.Length > MaxOverview ? i with { Overview = i.Overview[..MaxOverview] } : i), total = page.Total }, Json);

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json);
}

/// <summary>
/// The streamer's library, looked at from another computer: every question goes over the room to the streamer's app and the answer comes back (see
/// <see cref="JellyfinBrowseServer"/>). It works while the streamer streams and lets others browse, and it only reads.
/// </summary>
public sealed class RemoteJellyfinLibrary : IJellyfinLibrary
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly Func<string, CancellationToken, Task<string>> _ask;

    /// <param name="ask">Sends a question to the streamer's app and returns its answer (for instance <c>ChatRoom.AskLibraryAsync</c>).</param>
    public RemoteJellyfinLibrary(Func<string, CancellationToken, Task<string>> ask) => _ask = ask;

    public void Dispose() { }

    private async Task<JsonDocument> AskAsync(BrowseQuestion question, CancellationToken ct)
    {
        string text;
        try { text = await _ask(JsonSerializer.Serialize(question, Json), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new JellyfinException("The streamer's computer did not answer. They may have stopped streaming."); }
        catch (InvalidOperationException ex) { throw new JellyfinException(ex.Message); }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { throw new JellyfinException("The streamer's computer sent an answer that could not be read."); }
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            var message = error.GetString() ?? "";
            doc.Dispose();
            throw new JellyfinException(message);
        }
        return doc;
    }

    private static JellyfinPage ReadPage(JsonElement root)
    {
        var items = new List<JellyfinItem>();
        if (root.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var e in list.EnumerateArray())
                if (e.Deserialize<JellyfinItem>(Json) is { Id.Length: > 0 } item) items.Add(item);
        int total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : items.Count;
        return new JellyfinPage(items, total);
    }

    public async Task<IReadOnlyList<JellyfinLibrary>> GetLibrariesAsync(CancellationToken ct = default)
    {
        using var doc = await AskAsync(new BrowseQuestion("libraries"), ct).ConfigureAwait(false);
        var libraries = new List<JellyfinLibrary>();
        if (doc.RootElement.TryGetProperty("libraries", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var e in list.EnumerateArray())
                if (e.Deserialize<JellyfinLibrary>(Json) is { Id.Length: > 0 } library) libraries.Add(library);
        return libraries;
    }

    public async Task<JellyfinPage> GetItemsAsync(string? libraryId = null, string? search = null, int start = 0, int limit = 60, CancellationToken ct = default)
    {
        using var doc = await AskAsync(new BrowseQuestion("items", libraryId, search, start, limit), ct).ConfigureAwait(false);
        return ReadPage(doc.RootElement);
    }

    public async Task<JellyfinPage> GetResumeAsync(int limit = 30, CancellationToken ct = default)
    {
        using var doc = await AskAsync(new BrowseQuestion("resume", Limit: limit), ct).ConfigureAwait(false);
        return ReadPage(doc.RootElement);
    }

    public async Task<JellyfinPage> GetSeasonsAsync(string seriesId, CancellationToken ct = default)
    {
        using var doc = await AskAsync(new BrowseQuestion("seasons", seriesId), ct).ConfigureAwait(false);
        return ReadPage(doc.RootElement);
    }

    public async Task<JellyfinPage> GetEpisodesAsync(string seriesId, string seasonId, CancellationToken ct = default)
    {
        using var doc = await AskAsync(new BrowseQuestion("episodes", seriesId, seasonId), ct).ConfigureAwait(false);
        return ReadPage(doc.RootElement);
    }

    public async Task<byte[]?> GetPosterAsync(string itemId, string? tag, int maxWidth, CancellationToken ct = default)
    {
        if (tag == null) return null;
        try
        {
            using var doc = await AskAsync(new BrowseQuestion("poster", itemId, tag, Width: maxWidth), ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("poster", out var p) && p.ValueKind == JsonValueKind.String ? Convert.FromBase64String(p.GetString() ?? "") : null;
        }
        catch (Exception ex) when (ex is JellyfinException or FormatException) { return null; }   // the letter stays
    }
}
