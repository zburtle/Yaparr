using System.Net;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class WakeAddressTests
{
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde";

    [Fact]
    public void A_wake_address_is_https_a_name_and_the_path_of_a_token()
    {
        Assert.Equal(43, Token.Length);
        Assert.True(WakeAddress.TryParse("https://cloud.yaparr.com/v1/push/wake/" + Token, out var uri));
        Assert.Equal("cloud.yaparr.com", uri.Host);
        Assert.Equal("https://cloud.yaparr.com/v1/push/wake/" + Token, WakeAddress.For(new Uri("https://cloud.yaparr.com/"), Token));
        Assert.True(WakeAddress.TryParse(WakeAddress.For(new Uri("https://self.example.org:8443"), Token), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://cloud.yaparr.com/v1/push/wake/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde")]
    [InlineData("https://127.0.0.1/v1/push/wake/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde")]
    [InlineData("https://localhost/v1/push/wake/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde")]
    [InlineData("https://intranet/v1/push/wake/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde")]
    [InlineData("https://user:pw@cloud.yaparr.com/v1/push/wake/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde")]
    [InlineData("https://cloud.yaparr.com/v1/push/wake/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde?x=1")]
    [InlineData("https://cloud.yaparr.com/v1/push/wake/short")]
    [InlineData("https://cloud.yaparr.com/v1/backup")]
    [InlineData("https://cloud.yaparr.com/v1/push/wake/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_Ab/..")]
    [InlineData("not a url")]
    public void Anything_else_is_not(string? address) => Assert.False(WakeAddress.TryParse(address, out _));
}

public class WakeBookTests
{
    private const string Url = "https://cloud.yaparr.com/v1/push/wake/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde";
    private const string Url2 = "https://cloud.yaparr.com/v1/push/wake/ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ";

    [Fact]
    public void The_book_finds_people_by_room_and_by_account_and_forgets_them()
    {
        var sam = PeerIdentity.Generate().Id;
        var pat = PeerIdentity.Generate().Id;
        var book = new WakeBook(null);
        book.Remember(sam, sam, Url, "room-1");
        book.Remember(sam, sam, Url, "room-2");
        book.Remember(pat, pat, Url2, "room-1");
        Assert.Equal(2, book.ForRoom("room-1").Count);
        Assert.Equal(sam.Value, book.ForRoom("room-2").Single().Device);
        Assert.Equal(Url, book.ForAccount(sam).Single().Url);
        Assert.Empty(book.ForRoom("nothing"));

        book.ForgetUrl(Url);
        Assert.Equal(pat.Value, book.ForRoom("room-1").Single().Device);
        book.ForgetDevice(pat);
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void A_changed_address_replaces_the_old_one_and_old_entries_run_out()
    {
        var sam = PeerIdentity.Generate().Id;
        long now = 1_000_000;
        var book = new WakeBook(null, () => now);
        book.Remember(sam, sam, Url, "r");
        book.Remember(sam, sam, Url2, "r");
        Assert.Equal(Url2, book.ForRoom("r").Single().Url);
        now += (long)TimeSpan.FromDays(61).TotalMilliseconds;
        Assert.Empty(book.ForRoom("r"));
    }

    [Fact]
    public void The_book_survives_a_restart_and_ignores_a_damaged_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-wake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "wake.json");
            var sam = PeerIdentity.Generate().Id;
            var book = new WakeBook(path);
            book.Remember(sam, sam, Url, "r");
            book.Save();
            Assert.Equal(Url, new WakeBook(path).ForRoom("r").Single().Url);

            File.WriteAllText(path, "{ not json");
            Assert.Equal(0, new WakeBook(path).Count);
            File.WriteAllText(path, "[{\"device\":\"x\",\"url\":\"http://evil.example/\",\"rooms\":[\"r\"],\"seen\":9999999999999}]");
            Assert.Equal(0, new WakeBook(path).Count);   // an address that is not one is not kept
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class WakeTests
{
    private static ChatNode Node(InMemoryDiscoveryHub hub, string name, Func<string, Task<WakeResult>>? wake = null, string? book = null, TimeSpan? cooldown = null, string? wakeAddress = null) => new(new ChatNodeOptions
    {
        DisplayName = name,
        DiscoveryFactory = mux => hub.CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = TestUtil.FastNetwork,
        WakePeer = wake,
        WakePeersPath = book,
        WakeCooldown = cooldown ?? TimeSpan.FromMinutes(3),
    }) { };

    private const string Url = "https://cloud.yaparr.com/v1/push/wake/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde";

    [Fact]
    public async Task A_member_who_went_away_is_woken_by_the_next_message_in_the_room()
    {
        var hub = new InMemoryDiscoveryHub();
        var asked = new List<string>();
        using var sender = Node(hub, "sender", u => { lock (asked) asked.Add(u); return Task.FromResult(WakeResult.Sent); });
        var sleeper = Node(hub, "sleeper");
        sleeper.WakeAddressOfMine = Url;
        var rs = await sender.JoinRoomAsync("Home");
        await sleeper.JoinRoomAsync("Home");
        await TestUtil.WaitFor(() => rs.Members.Count == 1);

        // Connected: no wake-up.
        await rs.SendAsync("hello");
        await Task.Delay(300);
        Assert.Empty(asked);

        sleeper.Dispose();
        await TestUtil.WaitFor(() => rs.Members.Count == 0, 20_000);
        await rs.SendAsync("are you there");
        await TestUtil.WaitFor(() => { lock (asked) return asked.Count == 1; });
        Assert.Equal(Url, asked[0]);

        // And not again for every message: once in a while.
        await rs.SendAsync("hello?");
        await Task.Delay(300);
        lock (asked) Assert.Single(asked);
    }

    [Fact]
    public async Task A_person_who_turned_push_off_is_forgotten_and_one_who_cannot_be_reached_is_tried_again()
    {
        var hub = new InMemoryDiscoveryHub();
        var asked = new List<string>();
        var answer = WakeResult.Failed;
        using var sender = Node(hub, "sender", u => { lock (asked) asked.Add(u); return Task.FromResult(answer); });
        var sleeper = Node(hub, "sleeper");
        sleeper.WakeAddressOfMine = Url;
        var rs = await sender.JoinRoomAsync("Home");
        await sleeper.JoinRoomAsync("Home");
        await TestUtil.WaitFor(() => rs.Members.Count == 1);
        sleeper.Dispose();
        await TestUtil.WaitFor(() => rs.Members.Count == 0, 20_000);

        await rs.SendAsync("one");
        await TestUtil.WaitFor(() => { lock (asked) return asked.Count == 1; });
        await Task.Delay(200);
        await rs.SendAsync("two");                  // it failed, so the next message tries again at once
        await TestUtil.WaitFor(() => { lock (asked) return asked.Count == 2; });

        answer = WakeResult.Gone;
        await Task.Delay(200);
        await rs.SendAsync("three");
        await TestUtil.WaitFor(() => { lock (asked) return asked.Count == 3; });
        await Task.Delay(300);
        await rs.SendAsync("four");                 // gone: forgotten
        await Task.Delay(400);
        lock (asked) Assert.Equal(3, asked.Count);
        Assert.Equal(0, sender.KnownWakeAddresses);
    }

    [Fact]
    public async Task Someone_who_never_gave_an_address_is_not_woken_and_a_node_with_no_waker_asks_nobody()
    {
        var hub = new InMemoryDiscoveryHub();
        var asked = new List<string>();
        using var sender = Node(hub, "sender", u => { lock (asked) asked.Add(u); return Task.FromResult(WakeResult.Sent); });
        var plain = Node(hub, "plain");
        var rs = await sender.JoinRoomAsync("Home");
        await plain.JoinRoomAsync("Home");
        await TestUtil.WaitFor(() => rs.Members.Count == 1);
        plain.Dispose();
        await TestUtil.WaitFor(() => rs.Members.Count == 0, 20_000);
        await rs.SendAsync("hi");
        await Task.Delay(400);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Turning_the_address_off_tells_the_people_who_had_it()
    {
        var hub = new InMemoryDiscoveryHub();
        var asked = new List<string>();
        using var sender = Node(hub, "sender", u => { lock (asked) asked.Add(u); return Task.FromResult(WakeResult.Sent); });
        using var other = Node(hub, "other");
        other.WakeAddressOfMine = Url;
        var rs = await sender.JoinRoomAsync("Home");
        await other.JoinRoomAsync("Home");
        await TestUtil.WaitFor(() => rs.Members.Count == 1);
        await TestUtil.WaitFor(() => sender.KnownWakeAddresses == 1);
        other.WakeAddressOfMine = null;
        await TestUtil.WaitFor(() => sender.KnownWakeAddresses == 0);
        Assert.Throws<ArgumentException>(() => other.WakeAddressOfMine = "http://nope.example/");
    }

    [Fact]
    public async Task A_direct_message_to_someone_who_is_away_wakes_them()
    {
        var hub = new InMemoryDiscoveryHub();
        var asked = new List<string>();
        using var sender = Node(hub, "sender", u => { lock (asked) asked.Add(u); return Task.FromResult(WakeResult.Sent); });
        var sleeper = Node(hub, "sleeper");
        sleeper.WakeAddressOfMine = Url;
        var rs = await sender.JoinRoomAsync("Home");
        await sleeper.JoinRoomAsync("Home");
        await TestUtil.WaitFor(() => rs.Members.Count == 1);
        var sleeperId = sleeper.Id;
        var chat = sender.GetDirectChat(sleeperId);
        await chat.SendAsync("first");              // opens the conversation while they are here
        sleeper.Dispose();
        await TestUtil.WaitFor(() => rs.Members.Count == 0, 20_000);
        await chat.SendAsync("you there?");
        await TestUtil.WaitFor(() => { lock (asked) return asked.Count == 1; });
        Assert.Equal(Url, asked[0]);
    }
}
