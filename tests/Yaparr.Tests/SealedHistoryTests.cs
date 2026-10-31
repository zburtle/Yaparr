using System.Text;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Tests;

// Direct messages say who wrote them, and the history files on disk are encrypted.
public class SealedHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-sealed-" + Guid.NewGuid().ToString("N"));

    public SealedHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    // ---- who wrote a direct message ----

    private static DirectRecord Sent(PeerIdentity me, PeerId to, string text = "hello", long ts = 1000)
    {
        var proof = DirectProof.Create(me, to, "m1", ts, text);
        return new DirectRecord("m1", to.Value, "them", true, ts, text, proof.Pub, proof.Acct, proof.Cert, proof.Sig, ts);
    }

    // The same message as the person it was for keeps it.
    private static DirectRecord AsReceived(DirectRecord sent, PeerId authorAccount) => sent with { Mine = false, Peer = authorAccount.Value };

    [Fact]
    public void A_signed_message_checks_out_for_the_sender_and_for_the_person_it_was_for()
    {
        using var alice = PeerIdentity.Generate();
        using var phone = alice.CreateDevice();
        using var bob = PeerIdentity.Generate();

        var sent = Sent(phone, bob.AccountId);   // written on a linked device
        Assert.True(DirectProof.Verify(sent, alice.AccountId));
        Assert.True(DirectProof.Verify(AsReceived(sent, alice.AccountId), bob.AccountId));

        var first = Sent(alice, bob.AccountId);  // written on the device that is the account itself
        Assert.True(DirectProof.Verify(first, alice.AccountId));
        Assert.True(DirectProof.Verify(AsReceived(first, alice.AccountId), bob.AccountId));
    }

    [Fact]
    public void A_changed_message_or_one_for_someone_else_does_not_check_out()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var carol = PeerIdentity.Generate();
        var sent = Sent(alice, bob.AccountId);

        Assert.False(DirectProof.Verify(sent with { Text = "something else" }, alice.AccountId));
        Assert.False(DirectProof.Verify(sent with { Id = "m2" }, alice.AccountId));
        Assert.False(DirectProof.Verify(sent with { St = 2000 }, alice.AccountId));
        // A message to Bob cannot be passed off as one to Carol, or shown to Carol as if it was for her.
        Assert.False(DirectProof.Verify(sent with { Peer = carol.AccountId.Value }, alice.AccountId));
        Assert.False(DirectProof.Verify(AsReceived(sent, alice.AccountId), carol.AccountId));
    }

    [Fact]
    public void A_message_cannot_be_claimed_to_come_from_someone_who_did_not_write_it()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var mallory = PeerIdentity.Generate();
        var fromMallory = Sent(mallory, bob.AccountId);

        // Said to be from Alice, or to be one of your own.
        Assert.False(DirectProof.Verify(AsReceived(fromMallory, alice.AccountId), bob.AccountId));
        Assert.False(DirectProof.Verify(fromMallory, alice.AccountId));
        Assert.True(DirectProof.Verify(AsReceived(fromMallory, mallory.AccountId), bob.AccountId));
    }

    [Fact]
    public void A_device_certificate_from_another_account_is_not_accepted()
    {
        using var alice = PeerIdentity.Generate();
        using var mallory = PeerIdentity.Generate();
        using var malloryPhone = mallory.CreateDevice();
        using var bob = PeerIdentity.Generate();
        var forged = Sent(malloryPhone, bob.AccountId);

        // Claims Alice's account while carrying Mallory's certificate for the device.
        var claimed = forged with { Acct = Convert.ToBase64String(alice.AccountPublicKey) };
        Assert.False(DirectProof.Verify(AsReceived(claimed, alice.AccountId), bob.AccountId));
        // Dropping the certificate to look like a first device does not work either.
        Assert.False(DirectProof.Verify(AsReceived(forged with { Cert = "" }, mallory.AccountId), bob.AccountId));
        Assert.False(DirectProof.Verify(AsReceived(forged with { Acct = "", Cert = "" }, mallory.AccountId), bob.AccountId));
    }

    [Fact]
    public void An_unsigned_or_garbled_message_does_not_check_out()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var sent = Sent(alice, bob.AccountId);

        Assert.False(DirectProof.Verify(sent with { Sig = "", Pub = "" }, alice.AccountId));
        Assert.False(DirectProof.Verify(sent with { Sig = "not base64!" }, alice.AccountId));
        Assert.False(DirectProof.Verify(sent with { Pub = Convert.ToBase64String(new byte[] { 1, 2, 3 }) }, alice.AccountId));
    }

    // ---- history files ----

    [Fact]
    public void A_line_opens_only_in_the_file_it_was_written_for_and_only_unchanged()
    {
        var cipher = new HistoryCipher(new byte[32]);
        var line = cipher.Seal("{\"text\":\"secret\"}", "room-a.jsonl");

        Assert.DoesNotContain("secret", line);
        Assert.Equal("{\"text\":\"secret\"}", cipher.Open(line, "room-a.jsonl", out bool plain));
        Assert.False(plain);
        Assert.Null(cipher.Open(line, "room-b.jsonl", out _));
        Assert.Null(cipher.Open(line[..^3] + "AAA", "room-a.jsonl", out _));
        Assert.Null(new HistoryCipher([.. Enumerable.Repeat((byte)1, 32)]).Open(line, "room-a.jsonl", out _));
        Assert.Null(cipher.Open("enc1:!!", "room-a.jsonl", out _));
        Assert.Null(cipher.Open("garbage", "room-a.jsonl", out _));
    }

    [Fact]
    public void A_line_an_earlier_version_wrote_is_still_read_and_marked_as_plain()
    {
        var cipher = new HistoryCipher(new byte[32]);
        Assert.Equal("{\"a\":1}", cipher.Open("{\"a\":1}", "x.jsonl", out bool plain));
        Assert.True(plain);
    }

    [Fact]
    public void The_key_is_made_once_and_kept_and_one_that_cannot_be_read_is_set_aside()
    {
        var path = Path.Combine(_dir, "history.key");
        var first = HistoryCipher.LoadOrCreate(path);
        var line = first.Seal("hello", "f");
        Assert.Equal("hello", HistoryCipher.LoadOrCreate(path).Open(line, "f", out _));

        File.WriteAllBytes(path, [.. "YPSEC1"u8.ToArray(), .. new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }]);   // sealed for someone else
        var fresh = HistoryCipher.LoadOrCreate(path);
        Assert.Null(fresh.Open(line, "f", out _));
        Assert.True(File.Exists(path + ".unreadable"));
        Assert.Equal("again", fresh.Open(fresh.Seal("again", "f"), "f", out _));
    }

    [Fact]
    public void Room_history_is_kept_sealed_on_disk_and_reads_back()
    {
        var cipher = new HistoryCipher(new byte[32]);
        using var author = PeerIdentity.Generate();
        var message = SignedMessage.Create(author, "room1", "alice", "a private sentence");
        var store = new FileHistoryStore(_dir, cipher);
        store.Append("room1", message);
        store.Append("room1", SignedMessage.Create(author, "room1", "alice", "and another"));

        var onDisk = File.ReadAllText(Path.Combine(_dir, "room1.jsonl"));
        Assert.DoesNotContain("private sentence", onDisk);
        Assert.DoesNotContain("alice", onDisk);
        Assert.Equal(2, new FileHistoryStore(_dir, cipher).Load("room1").Count);
    }

    [Fact]
    public void Room_history_from_an_earlier_version_is_read_and_then_sealed()
    {
        using var author = PeerIdentity.Generate();
        var plainStore = new FileHistoryStore(_dir);
        plainStore.Append("room2", SignedMessage.Create(author, "room2", "alice", "written before sealing"));
        Assert.Contains("written before sealing", File.ReadAllText(Path.Combine(_dir, "room2.jsonl")));

        var cipher = new HistoryCipher(new byte[32]);
        var loaded = new FileHistoryStore(_dir, cipher).Load("room2");

        Assert.Single(loaded);
        Assert.DoesNotContain("written before sealing", File.ReadAllText(Path.Combine(_dir, "room2.jsonl")));
        Assert.Single(new FileHistoryStore(_dir, cipher).Load("room2"));
    }

    [Fact]
    public void A_room_file_with_lines_moved_in_from_another_room_does_not_load_them()
    {
        var cipher = new HistoryCipher(new byte[32]);
        using var author = PeerIdentity.Generate();
        var store = new FileHistoryStore(_dir, cipher);
        store.Append("roomA", SignedMessage.Create(author, "roomA", "alice", "for room A"));
        File.Copy(Path.Combine(_dir, "roomA.jsonl"), Path.Combine(_dir, "roomB.jsonl"));

        Assert.Single(store.Load("roomA"));
        Assert.Empty(store.Load("roomB"));
    }

    [Fact]
    public void Direct_history_is_sealed_on_disk_and_a_different_key_leaves_the_files_alone()
    {
        var cipher = new HistoryCipher(new byte[32]);
        var peer = new string('a', 64);
        var record = new DirectRecord("m1", peer, "bob", true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "meet me at the usual place");
        var history = new DirectHistory(_dir, 500, TimeSpan.FromDays(7), cipher);
        Assert.True(history.TryAdd(record));

        var file = Path.Combine(_dir, $"dm-{peer}.jsonl");
        var onDisk = File.ReadAllText(file);
        Assert.DoesNotContain("usual place", onDisk);
        Assert.DoesNotContain("bob", onDisk);
        Assert.Single(new DirectHistory(_dir, 500, TimeSpan.FromDays(7), cipher).For(peer));

        var other = new DirectHistory(_dir, 500, TimeSpan.FromDays(7), new HistoryCipher([.. Enumerable.Repeat((byte)7, 32)]));
        Assert.Empty(other.For(peer));
        Assert.Equal(onDisk, File.ReadAllText(file));   // not deleted or rewritten: the right key can still open it
    }

    [Fact]
    public void Direct_history_from_before_sealing_is_read_and_then_sealed()
    {
        var peer = new string('b', 64);
        var plain = new DirectHistory(_dir, 500, TimeSpan.FromDays(7));
        plain.TryAdd(new DirectRecord("m1", peer, "bob", false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "an older message"));
        var file = Path.Combine(_dir, $"dm-{peer}.jsonl");
        Assert.Contains("an older message", File.ReadAllText(file));

        var sealedHistory = new DirectHistory(_dir, 500, TimeSpan.FromDays(7), new HistoryCipher(new byte[32]));

        Assert.Single(sealedHistory.For(peer));
        Assert.DoesNotContain("an older message", File.ReadAllText(file));
    }
}
