using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.AppCore;

namespace Yaparr.Tests;

public sealed class SyncClockTests
{
    private static DateTimeOffset At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    [Fact]
    public void Clocks_sort_as_text_in_the_order_they_were_made_and_name_the_device()
    {
        long now = 1_800_000_000_000;
        var clock = new SyncClock("phone", () => At(now));
        var a = clock.Next();
        var b = clock.Next();   // the same millisecond: the counter
        now += 5;
        var c = clock.Next();
        Assert.True(string.CompareOrdinal(a, b) < 0 && string.CompareOrdinal(b, c) < 0, $"{a} {b} {c}");
        Assert.EndsWith("phone", a);
        Assert.Equal(12 + 4 + "phone".Length, a.Length);
    }

    [Fact]
    public void A_clock_that_is_behind_still_makes_a_write_newer_than_one_it_has_heard_of()
    {
        var ahead = new SyncClock("laptop", () => At(1_800_000_100_000));
        var behind = new SyncClock("phone", () => At(1_800_000_000_000));   // 100 seconds slow
        var heard = ahead.Next();
        behind.Receive(heard);
        Assert.True(SyncClock.IsNewer(behind.Next(), heard));
    }

    [Fact]
    public void A_clock_far_in_the_future_does_not_drag_this_one_along_and_text_that_is_no_clock_is_ignored()
    {
        long now = 1_800_000_000_000;
        var clock = new SyncClock("phone", () => At(now));
        var wild = new SyncClock("bad", () => At(now + 400L * 24 * 3600 * 1000)).Next();   // more than a year ahead
        clock.Receive(wild);
        clock.Receive("not a clock");
        clock.Receive(null);
        clock.Receive("ABCDEF0123456789phone");   // upper case does not sort as the number, so it is not ours
        Assert.True(SyncClock.IsNewer(wild, clock.Next()));   // the wild write is still newer than what this makes, but did not move it
        Assert.True(SyncClock.TryParse(clock.Next(), out var wall, out _));
        Assert.True(wall < now + 1000);
    }

    [Fact]
    public void The_device_id_is_checked()
    {
        Assert.Throws<ArgumentException>(() => new SyncClock(""));
        Assert.Throws<ArgumentException>(() => new SyncClock("has space"));
        Assert.Throws<ArgumentException>(() => new SyncClock(new string('a', 65)));
    }
}

public sealed class SyncSealerTests
{
    private const string Code = "K7QD-M3XF-9PLR-2WTE-HV8N-C4YB";

    [Fact]
    public void A_value_sealed_with_the_code_opens_with_the_code_however_it_is_typed()
    {
        var a = SyncSealer.FromRecoveryCode(Code);
        var b = SyncSealer.FromRecoveryCode("k7qdm3xf 9plr-2wte hv8nc4yb");
        var body = a.Seal("room", "r1", "0001", Encoding.UTF8.GetBytes("the room key"));
        Assert.Equal("the room key", Encoding.UTF8.GetString(b.Open("room", "r1", "0001", body)));
        Assert.DoesNotContain("room key", body);
    }

    [Fact]
    public void Sealing_twice_gives_different_bodies_and_a_wrong_code_does_not_open_one()
    {
        var sealer = SyncSealer.FromRecoveryCode(Code);
        Assert.NotEqual(sealer.Seal("room", "r1", "0001", new byte[] { 1 }), sealer.Seal("room", "r1", "0001", new byte[] { 1 }));
        var body = sealer.Seal("room", "r1", "0001", new byte[] { 1, 2, 3 });
        Assert.ThrowsAny<CryptographicException>(() => SyncSealer.FromRecoveryCode("AAAA-BBBB-CCCC-DDDD-EEEE-FFFF").Open("room", "r1", "0001", body));
    }

    [Theory]
    [InlineData("block", "r1", "0001")]   // a body given to another kind
    [InlineData("room", "r2", "0001")]    // to another record
    [InlineData("room", "r1", "0002")]    // or under a newer clock
    public void A_body_does_not_open_for_any_record_but_its_own(string kind, string id, string clock)
    {
        var sealer = SyncSealer.FromRecoveryCode(Code);
        var body = sealer.Seal("room", "r1", "0001", new byte[] { 9, 9 });
        Assert.ThrowsAny<CryptographicException>(() => sealer.Open(kind, id, clock, body));
    }

    [Fact]
    public void A_changed_or_short_or_foreign_body_is_refused_and_a_short_code_is_not_a_code()
    {
        var sealer = SyncSealer.FromRecoveryCode(Code);
        var bytes = Convert.FromBase64String(sealer.Seal("room", "r1", "0001", new byte[] { 1, 2, 3 }));
        bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => sealer.Open("room", "r1", "0001", Convert.ToBase64String(bytes)));
        Assert.ThrowsAny<CryptographicException>(() => sealer.Open("room", "r1", "0001", "AAAA"));
        Assert.ThrowsAny<CryptographicException>(() => sealer.Open("room", "r1", "0001", "not base64!"));
        Assert.Throws<ArgumentException>(() => SyncSealer.FromRecoveryCode("short"));
    }
}

/// <summary>A server that keeps records the way the real one does (the newest clock by text order, a cursor for each change), for the engine to talk to.</summary>
internal sealed class FakeSyncServer : ISyncTransport
{
    private readonly Dictionary<string, SyncWireRecord> _held = new();
    private long _counter;
    public int Pushes { get; private set; }
    public Func<IReadOnlyList<SyncWireRecord>, bool>? BeforePush { get; set; }
    public int PageSize { get; set; } = 100;

    public IReadOnlyCollection<SyncWireRecord> Held => _held.Values;

    public Task<SyncPullResult> PullAsync(long since, CancellationToken cancel = default)
    {
        var all = _held.Values.Where(r => r.Cursor > since).OrderBy(r => r.Cursor).ToList();
        var page = all.Take(PageSize).ToList();
        return Task.FromResult(new SyncPullResult(page.Count > 0 ? page[^1].Cursor : since, all.Count > page.Count, page));
    }

    public Task<SyncPushResult> PushAsync(IReadOnlyList<SyncWireRecord> records, CancellationToken cancel = default)
    {
        Pushes++;
        BeforePush?.Invoke(records);
        var stale = new List<string>();
        foreach (var r in records)
        {
            var key = r.Kind + "/" + r.Id;
            if (_held.TryGetValue(key, out var held))
            {
                int order = string.CompareOrdinal(r.Clock, held.Clock);
                if (order < 0) { stale.Add(key); continue; }
                if (order == 0) continue;
            }
            _held[key] = r with { Cursor = ++_counter };
        }
        return Task.FromResult(new SyncPushResult(_counter, stale));
    }
}

public sealed class SyncEngineTests
{
    private const string Code = "K7QD-M3XF-9PLR-2WTE-HV8N-C4YB";
    private long _now = 1_800_000_000_000;

    private (SyncEngine Engine, MemorySyncState State, List<SyncEntry> Remote) Device(FakeSyncServer server, string id, string code = Code)
    {
        var state = new MemorySyncState();
        var engine = new SyncEngine(state, server, SyncSealer.FromRecoveryCode(code), new SyncClock(id, () => DateTimeOffset.FromUnixTimeMilliseconds(_now)), id);
        var remote = new List<SyncEntry>();
        engine.Remote += remote.Add;
        return (engine, state, remote);
    }

    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);
    private static string S(byte[]? bytes) => Encoding.UTF8.GetString(bytes ?? Array.Empty<byte>());

    [Fact]
    public async Task What_one_device_writes_arrives_on_another_and_the_server_sees_only_locked_bodies()
    {
        var server = new FakeSyncServer();
        var phone = Device(server, "phone");
        var laptop = Device(server, "laptop");

        phone.Engine.Set("room", "r1", B("key one"));
        phone.Engine.Set("profile", "name", B("Sam"));
        var first = await phone.Engine.SyncAsync();
        Assert.Equal((0, 2, true), (first.Received, first.Sent, first.Complete));
        Assert.All(server.Held, r => Assert.DoesNotContain("key one", r.Body));

        var second = await laptop.Engine.SyncAsync();
        Assert.Equal((2, 0), (second.Received, second.Sent));
        Assert.Equal("key one", S(laptop.Engine.Get("room", "r1")?.Value));
        Assert.Equal(new[] { "room/r1", "profile/name" }, laptop.Remote.Select(e => e.Key));
    }

    [Fact]
    public async Task The_newer_write_wins_on_every_device_whatever_order_they_sync_in()
    {
        var server = new FakeSyncServer();
        var phone = Device(server, "phone");
        var laptop = Device(server, "laptop");

        phone.Engine.Set("profile", "name", B("Sam"));
        await phone.Engine.SyncAsync();
        await laptop.Engine.SyncAsync();

        // Both change the name while apart; the laptop's is the later write.
        phone.Engine.Set("profile", "name", B("Sammy"));
        _now += 1000;
        laptop.Engine.Set("profile", "name", B("Samuel"));

        await laptop.Engine.SyncAsync();
        await phone.Engine.SyncAsync();
        await laptop.Engine.SyncAsync();

        Assert.Equal("Samuel", S(phone.Engine.Get("profile", "name")?.Value));
        Assert.Equal("Samuel", S(laptop.Engine.Get("profile", "name")?.Value));
        Assert.Equal(new[] { "profile/name" }, phone.State.All().Select(e => e.Key));
        Assert.Empty(phone.State.Dirty());
        Assert.Empty(laptop.State.Dirty());
    }

    [Fact]
    public async Task A_device_with_a_slow_clock_still_wins_when_it_writes_after_hearing_of_the_others()
    {
        var server = new FakeSyncServer();
        var fast = Device(server, "fast");
        fast.Engine.Set("profile", "name", B("Old"));
        await fast.Engine.SyncAsync();

        _now -= 5 * 60 * 1000;   // the next device's clock is five minutes behind
        var slow = Device(server, "slow");
        await slow.Engine.SyncAsync();
        slow.Engine.Set("profile", "name", B("New"));
        await slow.Engine.SyncAsync();
        _now += 5 * 60 * 1000;
        await fast.Engine.SyncAsync();

        Assert.Equal("New", S(fast.Engine.Get("profile", "name")?.Value));
    }

    [Fact]
    public async Task Leaving_a_room_on_one_device_deletes_it_on_the_others_and_joining_again_brings_it_back()
    {
        var server = new FakeSyncServer();
        var phone = Device(server, "phone");
        var laptop = Device(server, "laptop");
        phone.Engine.Set("room", "r1", B("key one"));
        await phone.Engine.SyncAsync();
        await laptop.Engine.SyncAsync();

        _now += 1000;
        phone.Engine.Remove("room", "r1");
        await phone.Engine.SyncAsync();
        await laptop.Engine.SyncAsync();
        var left = laptop.Remote.Last();
        Assert.Equal(("room/r1", true), (left.Key, left.Deleted));
        Assert.True(laptop.Engine.Get("room", "r1")!.Deleted);

        _now += 1000;
        laptop.Engine.Set("room", "r1", B("key two"));   // joins again
        await laptop.Engine.SyncAsync();
        await phone.Engine.SyncAsync();
        Assert.Equal(("key two", false), (S(phone.Engine.Get("room", "r1")!.Value), phone.Engine.Get("room", "r1")!.Deleted));
    }

    [Fact]
    public async Task A_device_that_was_away_does_not_bring_back_what_was_deleted_meanwhile()
    {
        var server = new FakeSyncServer();
        var phone = Device(server, "phone");
        var laptop = Device(server, "laptop");
        phone.Engine.Set("room", "r1", B("key one"));
        await phone.Engine.SyncAsync();
        await laptop.Engine.SyncAsync();

        // The laptop writes (an older clock) while, unknown to it, the phone leaves the room later.
        laptop.Engine.Set("room", "r1", B("key one again"));
        _now += 1000;
        phone.Engine.Remove("room", "r1");
        await phone.Engine.SyncAsync();
        var report = await laptop.Engine.SyncAsync();

        Assert.True(report.Complete);
        Assert.True(laptop.Engine.Get("room", "r1")!.Deleted);
        Assert.Empty(laptop.State.Dirty());
    }

    [Fact]
    public async Task What_the_server_turns_down_as_stale_is_settled_by_reading_again()
    {
        var server = new FakeSyncServer();
        var phone = Device(server, "phone");
        var laptop = Device(server, "laptop");
        phone.Engine.Set("profile", "name", B("Sam"));
        // While the phone is about to send its write, the laptop's later write for the same entry gets to the server first.
        _now += 1000;
        laptop.Engine.Set("profile", "name", B("Samuel"));
        await laptop.Engine.SyncAsync();

        var report = await phone.Engine.SyncAsync();
        Assert.True(report.Complete);
        Assert.Equal("Samuel", S(phone.Engine.Get("profile", "name")?.Value));
        Assert.Empty(phone.State.Dirty());
    }

    [Fact]
    public async Task A_write_made_while_a_sync_runs_is_kept_for_the_next_one_and_the_same_write_is_not_sent_twice()
    {
        var server = new FakeSyncServer();
        var phone = Device(server, "phone");
        phone.Engine.Set("profile", "name", B("Sam"));
        server.BeforePush = _ => { phone.Engine.Set("room", "r1", B("made during")); return true; };
        var first = await phone.Engine.SyncAsync();
        Assert.Equal(1, first.Sent);
        Assert.Equal(new[] { "room/r1" }, phone.State.Dirty().Select(e => e.Key));

        server.BeforePush = null;
        var second = await phone.Engine.SyncAsync();
        Assert.Equal(1, second.Sent);
        var third = await phone.Engine.SyncAsync();
        Assert.Equal(0, third.Sent);
    }

    [Fact]
    public async Task Records_made_with_another_accounts_key_are_not_believed_and_are_counted()
    {
        var server = new FakeSyncServer();
        var stranger = Device(server, "stranger", "AAAA-BBBB-CCCC-DDDD-EEEE-FFFF");
        stranger.Engine.Set("room", "r1", B("not yours"));
        await stranger.Engine.SyncAsync();

        var phone = Device(server, "phone");
        var report = await phone.Engine.SyncAsync();
        Assert.Equal((0, 1), (report.Received, report.Unreadable));
        Assert.Null(phone.Engine.Get("room", "r1"));
        Assert.Empty(phone.Remote);
    }

    [Fact]
    public async Task A_body_swapped_between_records_on_the_server_is_not_believed()
    {
        var server = new FakeSyncServer();
        var phone = Device(server, "phone");
        phone.Engine.Set("room", "r1", B("key one"));
        phone.Engine.Set("room", "r2", B("key two"));
        await phone.Engine.SyncAsync();

        var swapped = new FakeSyncServer();
        var held = server.Held.ToDictionary(r => r.Id);
        await swapped.PushAsync(new[] { held["r1"] with { Body = held["r2"].Body }, held["r2"] with { Body = held["r1"].Body } });
        var laptop = Device(swapped, "laptop");
        var report = await laptop.Engine.SyncAsync();
        Assert.Equal((0, 2), (report.Received, report.Unreadable));
    }

    [Fact]
    public async Task A_long_list_is_read_page_after_page_and_sent_in_batches()
    {
        var server = new FakeSyncServer { PageSize = 7 };
        var phone = Device(server, "phone");
        for (int i = 0; i < 250; i++) phone.Engine.Set("room", "r" + i, B("key" + i));
        var sent = await phone.Engine.SyncAsync();
        Assert.Equal((250, true), (sent.Sent, sent.Complete));
        Assert.Equal(3, server.Pushes);   // 100 + 100 + 50

        var laptop = Device(server, "laptop");
        var read = await laptop.Engine.SyncAsync();
        Assert.Equal(250, read.Received);
        Assert.Equal(250, laptop.State.All().Count);
        Assert.Equal(250, laptop.State.Cursor);
    }

    [Fact]
    public async Task Sync_calls_take_turns()
    {
        var server = new FakeSyncServer();
        var phone = Device(server, "phone");
        phone.Engine.Set("room", "r1", B("key one"));
        var reports = await Task.WhenAll(phone.Engine.SyncAsync(), phone.Engine.SyncAsync(), phone.Engine.SyncAsync());
        Assert.Equal(1, reports.Sum(r => r.Sent));
    }
}

public sealed class SyncClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel);
            Seen.Add((request, body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_pull_asks_after_the_cursor_with_the_token_and_reads_the_records()
    {
        var handler = new Handler((_, _) => Json("""{"cursor":7,"more":true,"records":[{"kind":"room","id":"r1","clock":"c1","device":"phone","deleted":false,"body":"QQ==","cursor":7}]}"""));
        using var client = new SyncClient(new Uri("https://cloud.example.org"), "tok", new HttpClient(handler));
        var page = await client.PullAsync(5);
        var request = handler.Seen.Single().Request;
        Assert.Equal("https://cloud.example.org/v1/sync?since=5", request.RequestUri!.ToString());
        Assert.Equal(("Bearer", "tok"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.Equal((7L, true), (page.Cursor, page.More));
        Assert.Equal(new SyncWireRecord("room", "r1", "c1", "phone", false, "QQ==", 7), Assert.Single(page.Records));
    }

    [Fact]
    public async Task A_push_sends_the_records_and_reads_what_was_stale()
    {
        var handler = new Handler((_, _) => Json("""{"cursor":9,"stale":["room/r1"]}"""));
        using var client = new SyncClient(new Uri("https://cloud.example.org"), "tok", new HttpClient(handler));
        var result = await client.PushAsync(new[] { new SyncWireRecord("room", "r1", "c1", "phone", false, "QQ==") });
        var (request, body) = handler.Seen.Single();
        Assert.Equal((HttpMethod.Put, "https://cloud.example.org/v1/sync"), (request.Method, request.RequestUri!.ToString()));
        var sent = JsonDocument.Parse(body).RootElement.GetProperty("records")[0];
        Assert.Equal(("room", "r1", "c1", "phone", false, "QQ=="), (sent.GetProperty("kind").GetString(), sent.GetProperty("id").GetString(), sent.GetProperty("clock").GetString(),
            sent.GetProperty("device").GetString(), sent.GetProperty("deleted").GetBoolean(), sent.GetProperty("body").GetString()));
        Assert.Equal(9L, result.Cursor);
        Assert.Equal(new[] { "room/r1" }, result.Stale);
    }

    [Fact]
    public async Task A_refusal_from_the_server_is_an_error_that_says_why_and_nothing_is_sent_for_an_empty_or_huge_list()
    {
        var handler = new Handler((_, _) => Json("""{"error":"This account already holds 2000 synced records. Nothing was stored."}""", HttpStatusCode.Conflict));
        using var client = new SyncClient(new Uri("https://cloud.example.org"), "tok", new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<BackupServerException>(() => client.PushAsync(new[] { new SyncWireRecord("room", "r1", "c1", "phone", false, "QQ==") }));
        Assert.Contains("2000 synced records", ex.Message);
        Assert.Equal(HttpStatusCode.Conflict, ex.Status);

        await Assert.ThrowsAsync<ArgumentException>(() => client.PushAsync(Array.Empty<SyncWireRecord>()));
        await Assert.ThrowsAsync<ArgumentException>(() => client.PushAsync(Enumerable.Range(0, 101).Select(i => new SyncWireRecord("room", "r" + i, "c", "d", false, "QQ==")).ToList()));
        Assert.Single(handler.Seen);
    }
}
