using System.Security.Cryptography;
using Yaparr.AppCore;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class ProfileCipherTests
{
    private const string Passphrase = "correct horse battery staple";

    // Small settings: the real ones take a quarter of a second each, which adds up over many cases.
    private static byte[] Lock(byte[] plain, string passphrase = Passphrase)
    {
        var file = new MemoryStream();
        using (var stream = ProfileCipher.Encrypt(file, passphrase, 8 * 1024, 1, 1))
            stream.Write(plain);
        return file.ToArray();
    }

    private static byte[] Unlock(byte[] locked, string passphrase = Passphrase)
    {
        var plain = new MemoryStream();
        ProfileCipher.Decrypt(new MemoryStream(locked), plain, passphrase);
        return plain.ToArray();
    }

    private static byte[] Random(int length) => RandomNumberGenerator.GetBytes(length);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ProfileCipher.ChunkSize - 1)]
    [InlineData(ProfileCipher.ChunkSize)]
    [InlineData(ProfileCipher.ChunkSize + 1)]
    [InlineData(ProfileCipher.ChunkSize * 3)]
    [InlineData(ProfileCipher.ChunkSize * 3 + 123)]
    public void What_is_locked_comes_back_the_same(int length)
    {
        var plain = Random(length);
        Assert.Equal(plain, Unlock(Lock(plain)));
    }

    [Fact]
    public void Writing_in_small_pieces_gives_the_same_result_as_writing_at_once()
    {
        var plain = Random(ProfileCipher.ChunkSize * 2 + 500);
        var file = new MemoryStream();
        using (var stream = ProfileCipher.Encrypt(file, Passphrase, 8 * 1024, 1, 1))
            for (int i = 0; i < plain.Length; i += 777) stream.Write(plain, i, Math.Min(777, plain.Length - i));
        Assert.Equal(plain, Unlock(file.ToArray()));
    }

    [Fact]
    public void The_file_does_not_contain_what_was_locked_and_is_not_the_same_twice()
    {
        var plain = System.Text.Encoding.ASCII.GetBytes(new string('A', 5000) + "SECRET-IDENTITY-KEY");
        var a = Lock(plain);
        var b = Lock(plain);
        Assert.DoesNotContain("SECRET-IDENTITY-KEY", System.Text.Encoding.ASCII.GetString(a));
        Assert.NotEqual(a, b);   // a fresh salt and nonce each time
    }

    [Fact]
    public void A_wrong_passphrase_is_refused()
    {
        var locked = Lock(Random(1000));
        Assert.Throws<CryptographicException>(() => Unlock(locked, "correct horse battery stapl"));
        Assert.Throws<CryptographicException>(() => Unlock(locked, ""));
    }

    [Fact]
    public void A_changed_byte_anywhere_is_noticed()
    {
        var locked = Lock(Random(ProfileCipher.ChunkSize * 2 + 10));
        // In the header's key settings and salt, and in the first, middle and last chunk.
        foreach (var at in new[] { 12, 20, 45, locked.Length / 2, locked.Length - 1 })
        {
            var bad = (byte[])locked.Clone();
            bad[at] ^= 0x01;
            Assert.ThrowsAny<Exception>(() => Unlock(bad));
        }
    }

    [Fact]
    public void A_file_cut_short_is_noticed_even_at_a_chunk_boundary()
    {
        var plain = Random(ProfileCipher.ChunkSize * 3);
        var locked = Lock(plain);
        const int header = 40, block = ProfileCipher.ChunkSize + 16;

        // The last chunk dropped: what is left looks complete, but its chunk was not sealed as the last.
        var dropped = locked[..(locked.Length - block)];
        Assert.ThrowsAny<Exception>(() => Unlock(dropped));
        // Cut in the middle of a chunk, and down to just the header.
        Assert.ThrowsAny<Exception>(() => Unlock(locked[..(locked.Length - 5)]));
        Assert.Throws<InvalidDataException>(() => Unlock(locked[..header]));
    }

    [Fact]
    public void Chunks_cannot_be_swapped_or_borrowed_from_another_backup()
    {
        const int header = 40, block = ProfileCipher.ChunkSize + 16;
        var locked = Lock(Random(ProfileCipher.ChunkSize * 3));
        var swapped = (byte[])locked.Clone();
        Array.Copy(locked, header, swapped, header + block, block);
        Array.Copy(locked, header + block, swapped, header, block);
        Assert.ThrowsAny<Exception>(() => Unlock(swapped));

        var other = Lock(Random(ProfileCipher.ChunkSize * 3));
        var mixed = (byte[])locked.Clone();
        Array.Copy(other, header + block, mixed, header + block, block);
        Assert.ThrowsAny<Exception>(() => Unlock(mixed));
    }

    [Fact]
    public void Something_that_is_not_a_locked_backup_is_said_so()
    {
        Assert.Throws<InvalidDataException>(() => Unlock(new byte[] { 0x50, 0x4B, 3, 4 }));   // the start of a zip
        Assert.Throws<InvalidDataException>(() => Unlock(Array.Empty<byte>()));
    }

    [Fact]
    public void A_header_that_asks_for_a_huge_amount_of_memory_is_refused_before_any_is_used()
    {
        var locked = Lock(Random(10));
        BitConverter.GetBytes(uint.MaxValue).CopyTo(locked, 8);          // memory
        Assert.Throws<InvalidDataException>(() => Unlock(locked));
        locked = Lock(Random(10));
        BitConverter.GetBytes(uint.MaxValue).CopyTo(locked, 12);         // passes
        Assert.Throws<InvalidDataException>(() => Unlock(locked));
    }

    [Fact]
    public void A_short_passphrase_is_not_accepted()
    {
        Assert.NotNull(ProfileCipher.CheckPassphrase("too short"));
        Assert.NotNull(ProfileCipher.CheckPassphrase(null));
        Assert.Null(ProfileCipher.CheckPassphrase("twelve chars"));
        Assert.Throws<ArgumentException>(() => ProfileCipher.Encrypt(new MemoryStream(), "short"));
    }

    [Fact]
    public void The_same_text_typed_in_a_different_form_opens_the_backup()
    {
        // "é" as one character and as e plus an accent: what different keyboards and systems produce for the same word.
        var locked = Lock(Random(100), "café au lait, please");
        Assert.Equal(100, Unlock(locked, "café au lait, please").Length);
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-lockedbackup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void A_locked_profile_backup_restores_with_its_passphrase_and_only_with_it()
    {
        var source = NewDir();
        var target = NewDir();
        var file = Path.Combine(Path.GetTempPath(), "yaparr-locked-" + Guid.NewGuid().ToString("N") + ".yapbak");
        try
        {
            var original = new AppData(source);
            using var identity = PeerIdentity.Generate();
            original.SaveIdentity(identity);
            original.SaveSettings(new AppSettings { DisplayName = "Zed" });
            Directory.CreateDirectory(Path.Combine(source, "history"));
            File.WriteAllText(Path.Combine(source, "history", "room.log"), "hello from the past");

            original.ExportProfile(file, Passphrase);

            Assert.True(AppData.IsBackupLocked(file));
            Assert.DoesNotContain("PK", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file), 0, 2));   // not a zip someone could open

            var restoring = new AppData(target);
            Assert.Throws<InvalidDataException>(() => restoring.StageProfile(file));                         // no passphrase given
            Assert.Throws<CryptographicException>(() => restoring.StageProfile(file, "not the passphrase"));

            var staging = restoring.StageProfile(file, Passphrase);
            try
            {
                using var restored = PeerIdentity.Import(File.ReadAllBytes(Path.Combine(staging, "identity.key")));
                Assert.Equal(identity.AccountId, restored.AccountId);                                         // same account, as a new device
                Assert.NotEqual(identity.Id, restored.Id);
                Assert.Equal("hello from the past", File.ReadAllText(Path.Combine(staging, "history", "room.log")));
            }
            finally { Directory.Delete(staging, true); }
        }
        finally
        {
            foreach (var path in new[] { source, target }) { try { Directory.Delete(path, true); } catch (IOException) { } }
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_plain_backup_still_works_and_is_not_taken_for_a_locked_one()
    {
        var source = NewDir();
        var file = Path.Combine(Path.GetTempPath(), "yaparr-plain-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var data = new AppData(source);
            using (var identity = PeerIdentity.Generate()) data.SaveIdentity(identity);
            data.SaveSettings(new AppSettings { DisplayName = "Zed" });
            data.ExportProfile(file);
            Assert.False(AppData.IsBackupLocked(file));
            Directory.Delete(data.StageProfile(file), true);
            Directory.Delete(data.StageProfile(file, "a passphrase that is not needed"), true);   // ignored for a plain one
        }
        finally
        {
            try { Directory.Delete(source, true); } catch (IOException) { }
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_backup_with_a_too_short_passphrase_is_refused_and_leaves_no_file()
    {
        var source = NewDir();
        var file = Path.Combine(Path.GetTempPath(), "yaparr-short-" + Guid.NewGuid().ToString("N") + ".yapbak");
        try
        {
            var data = new AppData(source);
            using (var identity = PeerIdentity.Generate()) data.SaveIdentity(identity);
            Assert.Throws<ArgumentException>(() => data.ExportProfile(file, "short"));
            Assert.False(File.Exists(file));
        }
        finally { try { Directory.Delete(source, true); } catch (IOException) { } }
    }
}
