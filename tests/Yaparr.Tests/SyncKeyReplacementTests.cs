using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

/// <summary>Yaparr Cloud for the app's HTTP clients: the sync records, the kept pieces and the removal of backups, in memory.</summary>
internal sealed class FakeCloudHandler(FakeSyncServer records, FakeKeepServer keep) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/v1/sync")
        {
            if (request.Method == HttpMethod.Get)
            {
                var since = long.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["since"] ?? "0");
                var page = await records.PullAsync(since, cancel);
                return Json(new { cursor = page.Cursor, more = page.More, records = page.Records });
            }
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(BackupServerClient.Json, cancel);
            var sent = body.GetProperty("records").Deserialize<List<SyncWireRecord>>(BackupServerClient.Json)!;
            var result = await records.PushAsync(sent, cancel);
            return Json(new { cursor = result.Cursor, stale = result.Stale });
        }
        if (path.StartsWith("/v1/keep/", StringComparison.Ordinal))
        {
            var id = path["/v1/keep/".Length..];
            if (request.Method == HttpMethod.Put)
            {
                var put = await keep.PutAsync(id, await request.Content!.ReadAsByteArrayAsync(cancel), cancel);
                return new HttpResponseMessage(put == KeepPutResult.Stored ? HttpStatusCode.OK : HttpStatusCode.Conflict);
            }
            if (request.Method == HttpMethod.Delete) { await keep.DeleteAsync(id, cancel); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            var data = await keep.GetAsync(id, cancel);
            return data == null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: BackupServerClient.Json) };
}

public sealed class SyncKeyReplacementTests : IDisposable
{
    private const string OldCode = "K7QD-M3XF-9PLR-2WTE-HV8N-C4YB";
    private const string NewCode = "AAAA-BBBB-CCCC-DDDD-EEEE-FFFF";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaparr-keyreplace-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _records = new();
    private readonly FakeKeepServer _keep = new();
    private readonly FakeCloudHandler _cloud;
    private readonly HttpClient _http;
    private readonly List<IDisposable> _disposables = new();

    public SyncKeyReplacementTests()
    {
        _cloud = new FakeCloudHandler(_records, _keep);
        _http = new HttpClient(_cloud);
    }

    private T Track<T>(T item) where T : IDisposable { _disposables.Add(item); return item; }

    private int _backupDeletes;

    private Task Backups(CancellationToken cancel) { _backupDeletes++; return Task.CompletedTask; }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_disposables)) d.Dispose();   // nodes before the identities they use
        _http.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private (AppData Data, ChatNode Node) NewDevice(string folder, PeerIdentity identity, string code)
    {
        var data = new AppData(Path.Combine(_root, folder));
        data.SaveIdentity(identity);
        data.SaveBackupServer(new BackupServerState { Url = "https://cloud.test", Username = "zed", Token = "t-" + folder, RecoveryCode = code });
        var node = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: identity);
        _disposables.Add(node);
        return (data, node);
    }

    private static bool Opens(SyncWireRecord record, SyncSealer sealer)
    {
        try { sealer.Open(record.Kind, record.Id, record.Clock, record.Body); return true; }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    [Fact]
    public async Task Replacing_the_key_locks_everything_again_and_removes_what_the_old_key_locked()
    {
        var identity = Track(PeerIdentity.Generate());
        var (data, node) = NewDevice("laptop", identity, OldCode);
        await node.JoinRoomAsync(RoomKey.Generate(), "book club");
        Assert.Equal(AccountSyncResult.Synced, await AccountCloudSync.RunAsync(data, node, _http));
        var oldPiece = Assert.Single(_keep.Pieces).Key;
        var oldSealer = SyncSealer.FromRecoveryCode(OldCode);
        Assert.All(_records.Held, r => Assert.True(Opens(r, oldSealer)));

        var result = await SyncKeyReplacement.ReplaceAsync(data, node, NewCode, _http, removeBackups: Backups);

        Assert.True(result.Done, result.Problem);
        var newSealer = SyncSealer.FromRecoveryCode(NewCode);
        Assert.NotEmpty(_records.Held);
        Assert.All(_records.Held.Where(r => !r.Deleted), r => Assert.True(Opens(r, newSealer)));      // everything that is held opens with the new key only
        Assert.DoesNotContain(_records.Held, r => !r.Deleted && Opens(r, oldSealer));
        Assert.DoesNotContain(oldPiece, _keep.Pieces.Keys);                                              // the identity piece was written again under the new key
        Assert.Single(_keep.Pieces);
        Assert.Equal(1, _backupDeletes);                                                           // the whole-profile backups made with the old key are gone
        var state = data.LoadBackupServer();
        Assert.Equal(RecoveryCode.Normalize(NewCode), state.RecoveryCode);
        Assert.Equal(AccountSyncResult.Synced, await AccountCloudSync.RunAsync(data, node, _http));     // and it carries on with the new key
    }

    [Fact]
    public async Task A_device_still_on_the_old_key_stops_instead_of_writing_over_and_carries_on_once_given_the_new_one()
    {
        var identity = Track(PeerIdentity.Generate());
        var (data, node) = NewDevice("laptop", identity, OldCode);
        var pass = DeviceLink.GeneratePassphrase();
        var linked = DeviceLink.Open(DeviceLink.Create(identity, node.DisplayName, node.AccountEntries, pass), pass);
        var phoneData = new AppData(Path.Combine(_root, "phone"));
        phoneData.SaveIdentity(linked.Identity);
        phoneData.SaveBackupServer(new BackupServerState { Url = "https://cloud.test", Username = "zed", Token = "t-phone", RecoveryCode = OldCode });
        var phone = TestUtil.NewNode(new InMemoryDiscoveryHub(), linked.DisplayName, identity: linked.Identity, accountEntries: linked.Entries);
        _disposables.Add(phone);

        await AccountCloudSync.RunAsync(data, node, _http);
        await AccountCloudSync.RunAsync(phoneData, phone, _http);
        Assert.True((await SyncKeyReplacement.ReplaceAsync(data, node, NewCode, _http, removeBackups: Backups)).Done);

        // A change made on the laptop after the replacement: the phone, on the old key, cannot read it and sends nothing.
        var key = RoomKey.Generate();
        await node.JoinRoomAsync(key, "late room");
        await AccountCloudSync.RunAsync(data, node, _http);
        var heldBefore = _records.Held.Count;
        phone.Block(PeerIdentity.Generate().Id);
        Assert.Equal(AccountSyncResult.KeyChanged, await AccountCloudSync.RunAsync(phoneData, phone, _http));
        Assert.Equal(heldBefore, _records.Held.Count);
        Assert.All(_records.Held.Where(r => !r.Deleted), r => Assert.True(Opens(r, SyncSealer.FromRecoveryCode(NewCode))));
        Assert.Equal(AccountCloudSync.KeyChangedNote, phoneData.LoadBackupServer().AutoNote);

        // A key that does not open what the server holds is refused; the new one is taken.
        Assert.NotEqual("", await SyncKeyReplacement.AdoptAsync(phoneData, "ZZZZ-YYYY-XXXX-WWWW-VVVV-UUUU", _http));
        Assert.Equal("", await SyncKeyReplacement.AdoptAsync(phoneData, NewCode, _http));
        Assert.Equal(AccountSyncResult.Synced, await AccountCloudSync.RunAsync(phoneData, phone, _http));
        Assert.Contains(phone.AccountEntries, e => e.Key.StartsWith("room:", StringComparison.Ordinal) && !e.Deleted);
    }

    [Fact]
    public async Task The_same_key_a_malformed_one_and_a_signed_out_device_are_refused_and_nothing_changes()
    {
        var identity = Track(PeerIdentity.Generate());
        var (data, node) = NewDevice("laptop", identity, OldCode);
        Assert.False((await SyncKeyReplacement.ReplaceAsync(data, node, OldCode, _http)).Done);
        Assert.False((await SyncKeyReplacement.ReplaceAsync(data, node, "nope", _http)).Done);
        Assert.Equal(RecoveryCode.Normalize(OldCode), data.LoadBackupServer().RecoveryCode);
        Assert.Empty(_records.Held);

        var (outData, outNode) = NewDevice("tablet", identity, OldCode);
        var state = outData.LoadBackupServer();
        state.Token = "";
        outData.SaveBackupServer(state);
        Assert.False((await SyncKeyReplacement.ReplaceAsync(outData, outNode, NewCode, _http)).Done);
    }
}
