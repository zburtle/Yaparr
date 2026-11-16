using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Yaparr.AppCore;

/// <summary>A record as it goes to and from the server: the body is locked (see <see cref="SyncSealer"/>), and empty for a deleted record. <c>Cursor</c> is the server's and is ignored when sending.</summary>
public sealed record SyncWireRecord(string Kind, string Id, string Clock, string Device, bool Deleted, string Body, long Cursor = 0);

public sealed record SyncPullResult(long Cursor, bool More, IReadOnlyList<SyncWireRecord> Records);

/// <summary>What the server said to the records sent: where its cursor stands, and the <c>kind/id</c> of those it did not take because it holds a newer write.</summary>
public sealed record SyncPushResult(long Cursor, IReadOnlyList<string> Stale);

/// <summary>The two calls of the sync part of a Yaparr Cloud server (see its README, "Synced state"); a test puts its own in place of the server.</summary>
public interface ISyncTransport
{
    /// <summary>The records changed after <paramref name="since"/> (0 for all), oldest change first, a page at a time.</summary>
    Task<SyncPullResult> PullAsync(long since, CancellationToken cancel = default);

    /// <summary>Sends records (at most <see cref="SyncClient.MaxBatch"/>).</summary>
    Task<SyncPushResult> PushAsync(IReadOnlyList<SyncWireRecord> records, CancellationToken cancel = default);
}

/// <summary>Talks to the sync part of a Yaparr Cloud server. Needs this device to be signed in to the server (the same token as for backups).</summary>
public sealed class SyncClient : ISyncTransport, IDisposable
{
    /// <summary>The most records the server takes in one call.</summary>
    public const int MaxBatch = 100;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _token;
    private readonly Uri _server;

    public SyncClient(Uri server, string token, HttpClient? http = null)
    {
        _server = server;
        _token = token;
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancel)
    {
        var request = new HttpRequestMessage(method, new Uri(_server, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        try
        {
            var response = await _http.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await BackupServerClient.FailureAsync(response, cancel);
                response.Dispose();
                throw failure;
            }
            return response;
        }
        catch (HttpRequestException ex) { throw new BackupServerException("Couldn't reach the server: " + ex.Message, inner: ex); }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested) { throw new BackupServerException("The server took too long to answer.", inner: ex); }
    }

    private sealed record PullJson(long Cursor, bool More, List<SyncWireRecord>? Records);
    private sealed record PushJson(long Cursor, List<string>? Stale);

    public async Task<SyncPullResult> PullAsync(long since, CancellationToken cancel = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/sync?since=" + Math.Max(0, since), null, cancel);
        var page = await response.Content.ReadFromJsonAsync<PullJson>(BackupServerClient.Json, cancel) ?? throw new BackupServerException("The server answered with nothing.");
        return new SyncPullResult(page.Cursor, page.More, page.Records ?? new());
    }

    public async Task<SyncPushResult> PushAsync(IReadOnlyList<SyncWireRecord> records, CancellationToken cancel = default)
    {
        if (records.Count is 0 or > MaxBatch) throw new ArgumentException($"Send 1 to {MaxBatch} records.", nameof(records));
        using var response = await SendAsync(HttpMethod.Put, "/v1/sync", JsonContent.Create(new { records }, options: BackupServerClient.Json), cancel);
        var result = await response.Content.ReadFromJsonAsync<PushJson>(BackupServerClient.Json, cancel) ?? throw new BackupServerException("The server answered with nothing.");
        return new SyncPushResult(result.Cursor, result.Stale ?? new());
    }
}
