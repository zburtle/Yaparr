using System.Security.Cryptography;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Tests;

// Pictures and sounds are encrypted on disk, in pieces so that a range of a large file can still be served.
public class SealedBlobTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-blobs-" + Guid.NewGuid().ToString("N"));
    private static readonly HistoryCipher Cipher = new(new byte[32]);

    public SealedBlobTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private static byte[] Data(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string HashOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private string PathOf(string hash) => Path.Combine(_dir, hash);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(300_000)]
    public void A_file_reads_back_whole_and_is_not_readable_on_disk(int length)
    {
        var data = Data(length);
        var hash = HashOf(data);
        var store = new BlobStore(_dir, Cipher);
        store.Save(hash, data);

        Assert.True(store.Has(hash));
        Assert.Equal(data, store.Read(hash));
        var onDisk = File.ReadAllBytes(PathOf(hash));
        Assert.True(HistoryCipher.IsSealedFile(onDisk));
        if (length >= 64) Assert.False(onDisk.AsSpan().IndexOf(data.AsSpan(0, 64)) >= 0, "the picture's bytes should not appear in the file");
    }

    [Fact]
    public void A_range_in_the_middle_of_a_large_file_comes_back_right_including_across_pieces()
    {
        var data = Data(400_000);
        var hash = HashOf(data);
        var store = new BlobStore(_dir, Cipher);
        store.Save(hash, data);

        foreach (var (offset, length) in new[] { (0L, 10), (65_530L, 20), (65_536L, 65_536), (100_000L, 200_000), (399_990L, 100), (400_000L, 10) })
        {
            var expected = data.AsSpan((int)offset, (int)Math.Min(length, data.Length - offset)).ToArray();
            Assert.Equal(expected, store.ReadRange(hash, offset, length));
        }
        Assert.Null(store.ReadRange(hash, 400_001, 10));
    }

    [Fact]
    public void A_changed_file_is_refused_not_returned_wrong()
    {
        var data = Data(150_000);
        var hash = HashOf(data);
        var store = new BlobStore(_dir, Cipher);
        store.Save(hash, data);

        var bytes = File.ReadAllBytes(PathOf(hash));
        bytes[bytes.Length / 2] ^= 1;
        File.WriteAllBytes(PathOf(hash), bytes);
        Assert.Null(store.Read(hash));
        Assert.NotNull(store.ReadRange(hash, 0, 100));   // the pieces that were not touched still open
    }

    [Fact]
    public void A_file_cut_short_or_with_pieces_swapped_is_refused()
    {
        var data = Data(200_000);
        var hash = HashOf(data);
        var store = new BlobStore(_dir, Cipher);
        store.Save(hash, data);

        var good = File.ReadAllBytes(PathOf(hash));
        File.WriteAllBytes(PathOf(hash), good[..^(65_536 + 28)]);   // dropped from the end
        Assert.Null(store.Read(hash));

        var swapped = (byte[])good.Clone();
        int piece = 65_536 + 28, header = 15;
        Array.Copy(good, header + piece, swapped, header, piece);          // second piece in place of the first
        Array.Copy(good, header, swapped, header + piece, piece);
        File.WriteAllBytes(PathOf(hash), swapped);
        Assert.Null(store.Read(hash));
    }

    [Fact]
    public void A_file_copied_under_another_name_does_not_open()
    {
        var data = Data(5_000);
        var hash = HashOf(data);
        var store = new BlobStore(_dir, Cipher);
        store.Save(hash, data);

        var other = HashOf(Data(5_000, seed: 2));
        File.Copy(PathOf(hash), PathOf(other));
        Assert.Null(store.Read(other));
    }

    [Fact]
    public void A_different_key_or_no_key_cannot_open_a_sealed_file()
    {
        var data = Data(5_000);
        var hash = HashOf(data);
        new BlobStore(_dir, Cipher).Save(hash, data);

        Assert.Null(new BlobStore(_dir, new HistoryCipher([.. Enumerable.Repeat((byte)5, 32)])).Read(hash));
        Assert.Null(new BlobStore(_dir).Read(hash));
    }

    [Fact]
    public void Without_a_cipher_files_are_stored_as_before()
    {
        var data = Data(5_000);
        var hash = HashOf(data);
        var store = new BlobStore(_dir);
        store.Save(hash, data);

        Assert.Equal(data, File.ReadAllBytes(PathOf(hash)));
        Assert.Equal(data, store.Read(hash));
    }

    // A picture goes from one app to another: the sender serves it out of its sealed file, the receiver stores it sealed.
    [Fact]
    public async Task A_picture_is_sent_between_two_apps_that_both_keep_it_sealed()
    {
        var hub = new Yaparr.Discovery.InMemoryDiscoveryHub();
        var dirA = Path.Combine(_dir, "a");
        var dirB = Path.Combine(_dir, "b");
        using var a = TestUtil.NewNode(hub, "a", attachmentDirectory: dirA, historyCipher: Cipher);
        using var b = TestUtil.NewNode(hub, "b", attachmentDirectory: dirB, historyCipher: Cipher);
        var ra = await a.JoinRoomAsync("Pics");
        var rb = await b.JoinRoomAsync("Pics");
        await TestUtil.WaitFor(() => ra.MemberStates.Count == 1 && rb.MemberStates.Count == 1);

        // A small GIF with 300 KB of image data, so the transfer takes several pieces.
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        ms.Write(new byte[] { 8, 0, 8, 0, 0, 0, 0 });
        ms.WriteByte(0x2C);
        ms.Write(new byte[] { 0, 0, 0, 0, 8, 0, 8, 0, 0 });
        ms.WriteByte(2);
        for (int left = 300_000; left > 0;)
        {
            int n = Math.Min(255, left);
            ms.WriteByte((byte)n);
            ms.Write(Data(n, seed: left));
            left -= n;
        }
        ms.WriteByte(0);
        ms.WriteByte(0x3B);
        var file = ms.ToArray();
        var attachment = a.PrepareAttachment(file, "pic.gif");

        var got = new TaskCompletionSource<ChatMessage>();
        rb.MessageReceived += m => { if (m.Attachment != null) got.TrySetResult(m); };
        await ra.SendAsync(attachment.ToText("look"));
        await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await TestUtil.WaitFor(() => b.HasAttachment(attachment.Hash), timeoutMs: 30_000);

        Assert.Equal(file, b.ReadAttachment(attachment.Hash));
        Assert.Equal(file, a.ReadAttachment(attachment.Hash));
        Assert.True(HistoryCipher.IsSealedFile(File.ReadAllBytes(Path.Combine(dirA, attachment.Hash))));
        Assert.True(HistoryCipher.IsSealedFile(File.ReadAllBytes(Path.Combine(dirB, attachment.Hash))));
    }

    [Fact]
    public void Files_from_an_earlier_version_are_read_and_then_sealed_keeping_their_age()
    {
        var data = Data(100_000);
        var hash = HashOf(data);
        new BlobStore(_dir).Save(hash, data);
        var old = DateTime.UtcNow.AddDays(-3);
        File.SetLastWriteTimeUtc(PathOf(hash), old);
        var store = new BlobStore(_dir, Cipher);

        Assert.Equal(data, store.Read(hash));   // still readable before it is sealed
        store.SealExisting();

        Assert.True(HistoryCipher.IsSealedFile(File.ReadAllBytes(PathOf(hash))));
        Assert.Equal(data, store.Read(hash));
        Assert.True(Math.Abs((File.GetLastWriteTimeUtc(PathOf(hash)) - old).TotalSeconds) < 2);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        store.SealExisting();   // nothing left to do
        Assert.Equal(data, store.Read(hash));
    }
}
