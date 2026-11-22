using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.AppCore;

/// <summary>An item as it comes from a mailbox: the body is sealed (see <c>docs/mailbox.md</c>); the server's cursor says where it stands in the mailbox.</summary>
public sealed record MailboxItemWire(string Id, string Body, long At, long Cursor);

public sealed record MailboxPullResult(long Cursor, bool More, IReadOnlyList<MailboxItemWire> Items);

/// <summary>The two calls of the mailbox part of a Yaparr Cloud server (its README, "Mailboxes"); a test puts its own in place of the server.</summary>
public interface IMailboxTransport
{
    /// <summary>The items that arrived after <paramref name="since"/> (0 for everything kept), oldest first, a page at a time.</summary>
    Task<MailboxPullResult> PullAsync(string mailboxId, long since, CancellationToken cancel = default);

    /// <summary>Leaves sealed messages in a mailbox (at most <see cref="MailboxClient.MaxPost"/>). One that is already there is not stored again.</summary>
    Task PostAsync(string mailboxId, IReadOnlyList<MailboxPost> posts, CancellationToken cancel = default);
}

/// <summary>Talks to the mailbox part of a Yaparr Cloud server. Needs this device to be signed in to the server (the same token as for backups).</summary>
public sealed class MailboxClient : IMailboxTransport, IDisposable
{
    /// <summary>The most items the server takes in one call.</summary>
    public const int MaxPost = 20;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _token;
    private readonly Uri _server;

    public MailboxClient(Uri server, string token, HttpClient? http = null)
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

    private sealed record PullJson(long Cursor, bool More, List<MailboxItemWire>? Items);

    public async Task<MailboxPullResult> PullAsync(string mailboxId, long since, CancellationToken cancel = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/v1/mailbox/{mailboxId}?since={Math.Max(0, since)}", null, cancel);
        var page = await response.Content.ReadFromJsonAsync<PullJson>(BackupServerClient.Json, cancel) ?? throw new BackupServerException("The server answered with nothing.");
        return new MailboxPullResult(page.Cursor, page.More, page.Items ?? new());
    }

    public async Task PostAsync(string mailboxId, IReadOnlyList<MailboxPost> posts, CancellationToken cancel = default)
    {
        if (posts.Count is 0 or > MaxPost) throw new ArgumentException($"Send 1 to {MaxPost} items.", nameof(posts));
        var items = posts.Select(p => new { id = p.ItemId, body = p.Body }).ToArray();
        using var response = await SendAsync(HttpMethod.Post, $"/v1/mailbox/{mailboxId}", JsonContent.Create(new { items }, options: BackupServerClient.Json), cancel);
    }
}

/// <summary>Where each mailbox was read up to (the server's cursor), in a sealed file; in memory when there is no path. Losing it only means reading again what is kept, which the room skips.</summary>
public sealed class MailboxCursors
{
    private readonly string? _path;
    private readonly Dictionary<string, long> _cursors = new();
    private readonly object _gate = new();

    public MailboxCursors(string? path = null)
    {
        _path = path;
        if (path == null || !File.Exists(path)) return;
        try { _cursors = JsonSerializer.Deserialize<Dictionary<string, long>>(SecretStore.ReadAllBytes(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or CryptographicException) { _cursors = new(); }
    }

    public long Get(string mailboxId) { lock (_gate) return _cursors.GetValueOrDefault(mailboxId); }

    public void Set(string mailboxId, long cursor)
    {
        lock (_gate)
        {
            _cursors[mailboxId] = cursor;
            if (_path == null) return;
            try { SecretStore.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(_cursors)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

public enum MailboxSyncResult { NotSetUp, Synced, SignedOut, Failed }

/// <summary>What one run did: how many messages were left in mailboxes, and how many items came back that were good.</summary>
public sealed record MailboxSyncReport(MailboxSyncResult Result, int Posted = 0, int Received = 0);

/// <summary>
/// Leaves the messages this device wrote in the mailboxes of their rooms, and reads the mailboxes for what others left while this device was away, handing it to the rooms
/// (<see cref="ChatNode.ReceiveFromMailbox"/>). Runs soon after the app opens, every few minutes, a few seconds after something was written
/// (<see cref="ChatNode.MailboxQueued"/>) and when a push wakes the phone. It does nothing until this device is signed in to a server.
/// </summary>
public sealed class MailboxSync
{
    private readonly ChatNode _node;
    private readonly IMailboxTransport _transport;
    private readonly MailboxCursors _cursors;
    private readonly Action<string>? _log;

    public MailboxSync(ChatNode node, IMailboxTransport transport, MailboxCursors cursors, Action<string>? log = null)
    {
        _node = node;
        _transport = transport;
        _cursors = cursors;
        _log = log;
    }

    public async Task<MailboxSyncReport> SyncAsync(CancellationToken cancel = default)
    {
        int posted = 0, received = 0;

        // What was written here goes first, so a friend who is waiting does not wait for the reading.
        while (true)
        {
            var batch = _node.TakeMailboxPosts(MailboxClient.MaxPost * 5);
            if (batch.Count == 0) break;
            var unsent = new List<MailboxPost>(batch);
            try
            {
                foreach (var group in batch.GroupBy(p => p.MailboxId))
                    foreach (var chunk in group.Chunk(MailboxClient.MaxPost))
                    {
                        try { await _transport.PostAsync(group.Key, chunk, cancel); }
                        catch (BackupServerException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict || ex.Status == System.Net.HttpStatusCode.BadRequest)
                        {
                            // The mailbox is full, or the server will not take these: trying again would not change that, so they are dropped.
                            _log?.Invoke("mailbox: not taken, dropped: " + ex.Message);
                            foreach (var p in chunk) unsent.Remove(p);
                            continue;
                        }
                        posted += chunk.Length;
                        foreach (var p in chunk) unsent.Remove(p);
                    }
            }
            finally { _node.ReturnMailboxPosts(unsent); }
        }

        foreach (var id in _node.MailboxIds())
        {
            var cursor = _cursors.Get(id);
            while (true)
            {
                var page = await _transport.PullAsync(id, cursor, cancel);
                if (page.Items.Count > 0) received += _node.ReceiveFromMailbox(id, page.Items.Select(i => i.Body).ToList());
                if (page.Cursor != cursor) { cursor = page.Cursor; _cursors.Set(id, cursor); }
                if (!page.More || page.Items.Count == 0) break;
            }
        }
        return new MailboxSyncReport(MailboxSyncResult.Synced, posted, received);
    }

    /// <summary>One run with this device's own sign-in. Does nothing when it is not signed in (and stops the node queueing messages for a mailbox).</summary>
    public static async Task<MailboxSyncReport> RunAsync(AppData data, ChatNode node, HttpClient? http = null, Action<string>? log = null, CancellationToken cancel = default)
    {
        var server = data.LoadBackupServer();
        node.MailboxEnabled = server.SignedIn;
        // Pictures are left on the same server, locked (ChatNode.RelayAttachmentAsync) and fetched from it (see docs/mailbox.md).
        node.BlobRelay = server.SignedIn && Uri.TryCreate(server.Url, UriKind.Absolute, out var serverUri) ? new BlobRelayClient(serverUri, server.Token) : null;
        if (!server.SignedIn) return new MailboxSyncReport(MailboxSyncResult.NotSetUp);
        if (server.SubscriptionPaused(DateTimeOffset.UtcNow)) return new MailboxSyncReport(MailboxSyncResult.Failed);   // the subscription ended: left alone until the pause is over
        // Another server or another account is another set of mailboxes: its cursors are kept in a file of their own.
        var which = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server.Url + "|" + server.Username)))[..12];
        var cursors = new MailboxCursors(Path.Combine(data.Directory, $"mailbox-{which}.json"));
        try
        {
            using var client = new MailboxClient(new Uri(server.Url), server.Token, http);
            return await new MailboxSync(node, client, cursors, log).SyncAsync(cancel);
        }
        catch (BackupServerException ex) when (ex.SignedOut)
        {
            server.Token = "";
            server.AutoNote = "This device is no longer signed in to Yaparr Cloud. Sign in again.";
            data.SaveBackupServer(server);
            node.MailboxEnabled = false;
            node.BlobRelay = null;
            log?.Invoke("mailbox: no longer signed in");
            return new MailboxSyncReport(MailboxSyncResult.SignedOut);
        }
        catch (BackupServerException ex) when (ex.SubscriptionEnded)
        {
            server.PauseForSubscription(DateTimeOffset.UtcNow);
            data.SaveBackupServer(server);
            log?.Invoke("mailbox: the subscription has ended");
            return new MailboxSyncReport(MailboxSyncResult.Failed);
        }
        catch (Exception ex) when (ex is BackupServerException or IOException or UriFormatException)
        {
            if (cancel.IsCancellationRequested) throw;
            log?.Invoke("mailbox failed: " + ex.Message);
            return new MailboxSyncReport(MailboxSyncResult.Failed);
        }
    }
}
