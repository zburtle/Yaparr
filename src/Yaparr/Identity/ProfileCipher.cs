using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace Yaparr.Identity;

/// <summary>
/// Locks a profile backup with a passphrase, so the file can be kept anywhere (a cloud drive, a USB stick, an email to yourself) without
/// anyone who finds it being able to act as you. The key comes from the passphrase with Argon2id (memory-hard, so guessing is slow and
/// cannot be sped up much with graphics cards), and the data is sealed in chunks with AES-GCM so a backup of any size streams through without
/// being held in memory. Every chunk is tied to its place and to the header, and the last one is marked, so chunks cannot be reordered, dropped
/// from the end or mixed with another backup without it being noticed.
/// <code>
/// header  "YAPBAK1\n" | memory KiB (u32) | passes (u32) | lanes (u8) | salt (16) | nonce prefix (7)
/// chunk   AES-GCM(plaintext up to 64 KiB) + tag (16); nonce = prefix | chunk number (u32, big endian) | 1 if it is the last chunk else 0;
///         the header is the associated data
/// </code>
/// </summary>
public static class ProfileCipher
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("YAPBAK1\n");
    private const int SaltLength = 16, PrefixLength = 7, NonceLength = 12, TagLength = 16, KeyLength = 32;
    private const int HeaderLength = 8 + 4 + 4 + 1 + SaltLength + PrefixLength;
    internal const int ChunkSize = 64 * 1024;

    /// <summary>The shortest passphrase a backup may be locked with.</summary>
    public const int MinPassphraseLength = 12;

    // What new backups use: 64 MiB and three passes, about a quarter of a second on a phone. The values are in the file, so they can be raised later
    // and old backups still open. What a file may ask for when it is opened is capped, so a made-up header cannot make the app use all its memory.
    private const uint DefaultMemoryKiB = 64 * 1024, DefaultPasses = 3, MaxMemoryKiB = 512 * 1024, MaxPasses = 12;
    private const byte DefaultLanes = 2, MaxLanes = 8;

    /// <summary>True if the file starts like a locked backup (a plain backup is a zip file and does not).</summary>
    public static bool IsLocked(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> start = stackalloc byte[8];
            return file.Read(start) == 8 && start.SequenceEqual(Magic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Why a passphrase is not good enough, or null if it is.</summary>
    public static string? CheckPassphrase(string? passphrase) =>
        (passphrase ?? "").Length < MinPassphraseLength
            ? $"The passphrase needs at least {MinPassphraseLength} characters. A few words in a row make a good one, and are easy to remember."
            : null;

    /// <summary>A stream to write the plain backup into; what reaches <paramref name="output"/> is the locked file. Dispose it to finish the file.</summary>
    public static Stream Encrypt(Stream output, string passphrase) =>
        Encrypt(output, passphrase, DefaultMemoryKiB, DefaultPasses, DefaultLanes);

    internal static Stream Encrypt(Stream output, string passphrase, uint memoryKiB, uint passes, byte lanes)
    {
        if (CheckPassphrase(passphrase) is { } why) throw new ArgumentException(why, nameof(passphrase));
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), memoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), passes);
        header[16] = lanes;
        RandomNumberGenerator.Fill(header.AsSpan(17, SaltLength + PrefixLength));
        var key = DeriveKey(passphrase, header.AsSpan(17, SaltLength), memoryKiB, passes, lanes);
        output.Write(header);
        return new LockingStream(output, key, header);
    }

    /// <summary>
    /// Unlocks <paramref name="input"/> into <paramref name="output"/>. A wrong passphrase and a damaged file look the same from outside
    /// (<see cref="CryptographicException"/>); a file that is not a locked backup, or is cut short, is an <see cref="InvalidDataException"/>.
    /// </summary>
    public static void Decrypt(Stream input, Stream output, string passphrase)
    {
        var header = new byte[HeaderLength];
        if (input.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false) < HeaderLength || !header.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("This is not a locked Yaparr backup.");
        var memoryKiB = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var passes = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var lanes = header[16];
        if (memoryKiB < 8 * 1024 || memoryKiB > MaxMemoryKiB || passes < 1 || passes > MaxPasses || lanes < 1 || lanes > MaxLanes)
            throw new InvalidDataException("This backup was made by a version of Yaparr that this one does not understand.");

        var key = DeriveKey(passphrase ?? "", header.AsSpan(17, SaltLength), memoryKiB, passes, lanes);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            var nonce = new byte[NonceLength];
            header.AsSpan(17 + SaltLength, PrefixLength).CopyTo(nonce);
            var block = new byte[ChunkSize + TagLength];
            var plain = new byte[ChunkSize];
            var next = new byte[ChunkSize + TagLength];
            int have = input.ReadAtLeast(block, block.Length, throwOnEndOfStream: false);
            uint number = 0;
            while (true)
            {
                // The block is the last one if nothing follows it; reading the next one first is how that is known.
                int following = have == block.Length ? input.ReadAtLeast(next, next.Length, throwOnEndOfStream: false) : 0;
                bool last = following == 0;
                if (have < TagLength) throw new InvalidDataException("This backup is cut short.");
                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(PrefixLength), number);
                nonce[NonceLength - 1] = last ? (byte)1 : (byte)0;
                var length = have - TagLength;
                try { aes.Decrypt(nonce, block.AsSpan(0, length), block.AsSpan(length, TagLength), plain.AsSpan(0, length), header); }
                catch (AuthenticationTagMismatchException)
                {
                    // On the first chunk this is almost always a wrong passphrase; later on it is a damaged file. Either way the person is told both.
                    throw new CryptographicException("The passphrase is wrong, or the backup file was damaged.");
                }
                output.Write(plain, 0, length);
                if (last) return;
                (block, next) = (next, block);
                have = following;
                number++;
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, uint memoryKiB, uint passes, byte lanes)
    {
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithMemoryAsKB((int)memoryKiB)
            .WithIterations((int)passes)
            .WithParallelism(lanes)
            .WithSalt(salt.ToArray())
            .Build();
        var argon = new Argon2BytesGenerator();
        argon.Init(parameters);
        var key = new byte[KeyLength];
        // Normalised, so the same words typed on another keyboard or system give the same key.
        argon.GenerateBytes(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormKC)), key);
        return key;
    }

    private sealed class LockingStream : Stream
    {
        private readonly Stream _output;
        private readonly AesGcm _aes;
        private readonly byte[] _nonce = new byte[NonceLength];
        private readonly byte[] _header;
        private readonly byte[] _pending = new byte[ChunkSize];
        private readonly byte[] _sealed = new byte[ChunkSize + TagLength];
        private int _count;
        private uint _number;
        private bool _finished;

        public LockingStream(Stream output, byte[] key, byte[] header)
        {
            _output = output;
            _header = header;
            _aes = new AesGcm(key, TagLength);
            CryptographicOperations.ZeroMemory(key);
            header.AsSpan(17 + SaltLength, PrefixLength).CopyTo(_nonce);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            while (!data.IsEmpty)
            {
                // A full chunk is held back until more arrives, because the last chunk is sealed differently and may turn out to be this one.
                if (_count == ChunkSize) Seal(last: false);
                var take = Math.Min(ChunkSize - _count, data.Length);
                data[..take].CopyTo(_pending.AsSpan(_count));
                _count += take;
                data = data[take..];
            }
        }

        private void Seal(bool last)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_nonce.AsSpan(PrefixLength), _number++);
            _nonce[NonceLength - 1] = last ? (byte)1 : (byte)0;
            _aes.Encrypt(_nonce, _pending.AsSpan(0, _count), _sealed.AsSpan(0, _count), _sealed.AsSpan(_count, TagLength), _header);
            _output.Write(_sealed, 0, _count + TagLength);
            _count = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_finished)
            {
                _finished = true;
                Seal(last: true);
                _aes.Dispose();
                _output.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
