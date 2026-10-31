using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Identity;

/// <summary>
/// Encrypts the lines of the chat history files, so a copy of the folder (a backup, a synced drive, a stolen disk)
/// does not hold anyone's messages. Each line is sealed with AES-GCM under a key kept in a file of its own, which
/// <see cref="SecretStore"/> seals to the Windows user (elsewhere it is private to its owner). A line also records
/// which file it belongs to, so lines cannot be moved between conversations, and any change to it is detected.
/// Plain lines written by earlier versions are still read; the files are rewritten sealed.
/// </summary>
public sealed class HistoryCipher
{
    private const string Prefix = "enc1:";
    private const int NonceSize = 12, TagSize = 16;
    private readonly byte[] _key;

    public HistoryCipher(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("The key must be 32 bytes.", nameof(key));
        _key = key;
    }

    /// <summary>Opens the key file, or makes a new key if there is none. A key that cannot be opened is kept aside (old history cannot be read without it).</summary>
    public static HistoryCipher LoadOrCreate(string keyPath)
    {
        try
        {
            if (File.Exists(keyPath) && SecretStore.ReadAllBytes(keyPath) is { Length: 32 } key) return new HistoryCipher(key);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException) { }

        try { if (File.Exists(keyPath)) File.Move(keyPath, keyPath + ".unreadable", overwrite: true); }
        catch (IOException) { }
        var fresh = RandomNumberGenerator.GetBytes(32);
        SecretStore.WriteAllBytes(keyPath, fresh);
        return new HistoryCipher(fresh);
    }

    // ---- whole files (pictures, sounds) ----
    // Sealed in pieces, each with its own nonce, so a range from the middle of a large file can be read without opening
    // all of it. Every piece is tied to the file's name, its place and the file's length, so pieces cannot be swapped,
    // moved between files, dropped from the end or reordered without being noticed.

    private static readonly byte[] FileMagic = "YPBLOB1"u8.ToArray();
    private const int ChunkSize = 64 * 1024, ChunkOverhead = NonceSize + TagSize, FileHeader = 7 + 8;

    public static int SealedFileMagicLength => FileMagic.Length;

    public static bool IsSealedFile(ReadOnlySpan<byte> start) => start.StartsWith(FileMagic);

    private static byte[] ChunkContext(string name, int index, long length)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var context = new byte[nameBytes.Length + 4 + 8];
        nameBytes.CopyTo(context, 0);
        BitConverter.TryWriteBytes(context.AsSpan(nameBytes.Length), index);
        BitConverter.TryWriteBytes(context.AsSpan(nameBytes.Length + 4), length);
        return context;
    }

    /// <summary>The bytes to write to a file called <paramref name="name"/> for <paramref name="plain"/>.</summary>
    public byte[] SealFile(string name, byte[] plain)
    {
        int chunks = (plain.Length + ChunkSize - 1) / ChunkSize;
        var output = new byte[FileHeader + plain.Length + chunks * ChunkOverhead];
        FileMagic.CopyTo(output, 0);
        BitConverter.TryWriteBytes(output.AsSpan(FileMagic.Length), (long)plain.Length);
        using var aes = new AesGcm(_key, TagSize);
        int at = FileHeader;
        for (int i = 0; i < chunks; i++)
        {
            int n = Math.Min(ChunkSize, plain.Length - i * ChunkSize);
            RandomNumberGenerator.Fill(output.AsSpan(at, NonceSize));
            aes.Encrypt(output.AsSpan(at, NonceSize), plain.AsSpan(i * ChunkSize, n), output.AsSpan(at + NonceSize, n),
                output.AsSpan(at + NonceSize + n, TagSize), ChunkContext(name, i, plain.Length));
            at += n + ChunkOverhead;
        }
        return output;
    }

    /// <summary>
    /// Up to <paramref name="length"/> bytes of the original file from <paramref name="offset"/>, read from the sealed
    /// file <paramref name="file"/> (called <paramref name="name"/>). Null if the offset is past the end or any piece
    /// that is needed was changed, cut short or sealed for something else.
    /// </summary>
    public byte[]? OpenFileRange(string name, Stream file, long offset, int length)
    {
        try
        {
            var header = new byte[FileHeader];
            file.Position = 0;
            file.ReadExactly(header);
            if (!IsSealedFile(header)) return null;
            long total = BitConverter.ToInt64(header, FileMagic.Length);
            if (total < 0 || offset < 0 || offset > total || file.Length != FileHeader + total + (total + ChunkSize - 1) / ChunkSize * ChunkOverhead) return null;

            long end = Math.Min(total, offset + (long)length);
            if (end <= offset) return Array.Empty<byte>();
            var result = new byte[end - offset];
            using var aes = new AesGcm(_key, TagSize);
            for (int i = (int)(offset / ChunkSize); i <= (int)((end - 1) / ChunkSize); i++)
            {
                long start = (long)i * ChunkSize;
                int n = (int)Math.Min(ChunkSize, total - start);
                var sealedChunk = new byte[n + ChunkOverhead];
                file.Position = FileHeader + (long)i * (ChunkSize + ChunkOverhead);
                file.ReadExactly(sealedChunk);
                var plain = new byte[n];
                aes.Decrypt(sealedChunk.AsSpan(0, NonceSize), sealedChunk.AsSpan(NonceSize, n), sealedChunk.AsSpan(NonceSize + n, TagSize), plain, ChunkContext(name, i, total));

                long from = Math.Max(offset, start), to = Math.Min(end, start + n);
                plain.AsSpan((int)(from - start), (int)(to - from)).CopyTo(result.AsSpan((int)(from - offset)));
            }
            return result;
        }
        catch (Exception ex) when (ex is CryptographicException or EndOfStreamException or IOException or ArgumentException) { return null; }
    }

    /// <summary>A line to write for <paramref name="text"/>. <paramref name="context"/> names the file it goes in.</summary>
    public string Seal(string text, string context)
    {
        var plain = Encoding.UTF8.GetBytes(text);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(context));
        return Prefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    /// <summary>
    /// The text of a line read from the file named <paramref name="context"/>, or null if it cannot be opened (it was
    /// changed, belongs elsewhere, or was sealed with another key). <paramref name="wasPlain"/> is set for a line an
    /// earlier version wrote without sealing it.
    /// </summary>
    public string? Open(string line, string context, out bool wasPlain)
    {
        wasPlain = false;
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            wasPlain = line.StartsWith('{');
            return wasPlain ? line : null;
        }
        try
        {
            var data = Convert.FromBase64String(line[Prefix.Length..]);
            if (data.Length < NonceSize + TagSize) return null;
            var plain = new byte[data.Length - NonceSize - TagSize];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize, plain.Length), data.AsSpan(data.Length - TagSize), plain, Encoding.UTF8.GetBytes(context));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return null; }
    }
}
