using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaparr.Chat.Files;

/// <summary>
/// A file offered in a message: its name, size, the size of its pieces, and the <see cref="Root"/> that fixes the content of every piece (see <c>docs/file-sharing.md</c>). Nothing is
/// sent until someone asks for the file. The offer travels behind a marker in the text of the message, so the message's signature covers it.
/// </summary>
public sealed record FileOffer(string Root, string Name, long Size, int PieceSize)
{
    public const int MinPieceSize = 256 * 1024;
    public const int MaxPieceSize = 16 * 1024 * 1024;
    public const int DefaultPieceSize = 1024 * 1024;

    /// <summary>The most pieces a file can have (4 TiB at 1 MiB): the list of their hashes is 32 bytes each, and a downloader holds it.</summary>
    public const int MaxPieces = 1 << 22;

    /// <summary>The unit a piece is sent in.</summary>
    public const int BlockSize = 16 * 1024;

    private const string Marker = "\u0001yaparr-file1\u0001";

    public int PieceCount => (int)((Size + PieceSize - 1) / PieceSize);

    public int PieceLength(int piece) => (int)Math.Min(PieceSize, Size - (long)piece * PieceSize);

    public int BlockCount(int piece) => (PieceLength(piece) + BlockSize - 1) / BlockSize;

    /// <summary>The piece size to use for a file of <paramref name="size"/> bytes: 1 MiB, larger for files that would otherwise have too many pieces.</summary>
    public static int PieceSizeFor(long size)
    {
        int pieceSize = DefaultPieceSize;
        while (pieceSize < MaxPieceSize && (size + pieceSize - 1) / pieceSize > MaxPieces) pieceSize *= 2;
        return pieceSize;
    }

    /// <summary>True when the numbers can describe a file (and so are safe to allocate for).</summary>
    public static bool IsValid(long size, int pieceSize) =>
        size >= 1 && pieceSize is >= MinPieceSize and <= MaxPieceSize && (pieceSize & (pieceSize - 1)) == 0 && (size + pieceSize - 1) / pieceSize <= MaxPieces;

    public static bool IsRoot(string? text) => text is { Length: 64 } && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>The message text that carries this offer, with an optional caption.</summary>
    public string ToText(string caption = "")
    {
        var dto = new Dto { H = Root, N = Name, S = Size, P = PieceSize, C = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim() is { Length: > 1000 } longCaption ? longCaption[..1000] : caption.Trim() };
        return Marker + JsonSerializer.Serialize(dto, Json);
    }

    /// <summary>Recognises a message that offers a file. Anything malformed or implausible is treated as plain text.</summary>
    public static bool TryParse(string text, out FileOffer offer, out string caption)
    {
        offer = null!;
        caption = text;
        if (!text.StartsWith(Marker, StringComparison.Ordinal)) return false;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(text.AsSpan(Marker.Length), Json);
            if (dto?.H is not { } root || !IsRoot(root.ToLowerInvariant()) || !IsValid(dto.S, dto.P)) return false;
            offer = new FileOffer(root.ToLowerInvariant(), CleanName(dto.N), dto.S, dto.P);
            caption = dto.C ?? "";
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>A name that is safe to show and to use as a file name: no folders, no control characters, not hidden, at most 120 characters.</summary>
    public static string CleanName(string? name)
    {
        var written = (name ?? "").Replace('\\', '/');
        var clean = new string(written[(written.LastIndexOf('/') + 1)..].Where(c => !char.IsControl(c) && c is not ('/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')).ToArray()).Trim().TrimStart('.').TrimEnd('.', ' ');
        if (clean.Length > 120) clean = clean[..120];
        return clean.Length == 0 ? "file" : clean;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed class Dto
    {
        public string? H { get; set; }
        public string? N { get; set; }
        public long S { get; set; }
        public int P { get; set; }
        public string? C { get; set; }
    }
}

/// <summary>The hashes that make a file's root, and checks against them.</summary>
public static class PieceHashes
{
    public const int HashSize = 32;

    /// <summary>The root: a hash of the size, the piece size and every piece's hash, in order.</summary>
    public static string RootOf(long size, int pieceSize, ReadOnlySpan<byte> hashes)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.ASCII.GetBytes("yaparr-file-v1"));
        Span<byte> header = stackalloc byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(header, size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[8..], pieceSize);
        sha.AppendData(header);
        sha.AppendData(hashes);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Whether the list of hashes is the one the offer's root names (the right length, and the right content).</summary>
    public static bool Matches(FileOffer offer, ReadOnlySpan<byte> hashes) =>
        hashes.Length == (long)offer.PieceCount * HashSize && RootOf(offer.Size, offer.PieceSize, hashes) == offer.Root;

    public static bool PieceMatches(ReadOnlySpan<byte> hashes, int piece, ReadOnlySpan<byte> data) =>
        (long)(piece + 1) * HashSize <= hashes.Length && SHA256.HashData(data).AsSpan().SequenceEqual(hashes.Slice(piece * HashSize, HashSize));

    /// <summary>
    /// Reads the file once and returns its offer and the hash of every piece. <paramref name="progress"/> gets the bytes read so far. The file is only read, never changed.
    /// </summary>
    public static async Task<(FileOffer Offer, byte[] Hashes)> HashFileAsync(string path, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await HashStreamAsync(fs, FileOffer.CleanName(Path.GetFileName(path)), progress, ct).ConfigureAwait(false);
    }

    /// <summary>The same for a file that is reached as a stream (the phone's file chooser hands over a content address, not a path). The stream must be seekable only in that it reports its length.</summary>
    public static async Task<(FileOffer Offer, byte[] Hashes)> HashStreamAsync(Stream fs, string name, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        long size = fs.Length;
        if (size < 1) throw new ArgumentException("That file is empty.");
        int pieceSize = FileOffer.PieceSizeFor(size);
        int count = (int)((size + pieceSize - 1) / pieceSize);
        var hashes = new byte[count * HashSize];
        var buffer = new byte[pieceSize];
        long done = 0;
        for (int i = 0; i < count; i++)
        {
            int length = (int)Math.Min(pieceSize, size - (long)i * pieceSize);
            await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
            SHA256.HashData(buffer.AsSpan(0, length), hashes.AsSpan(i * HashSize, HashSize));
            done += length;
            progress?.Report(done);
        }
        if (fs.Length != size) throw new IOException("The file changed while it was being read.");
        return (new FileOffer(RootOf(size, pieceSize, hashes), FileOffer.CleanName(name), size, pieceSize), hashes);
    }
}

/// <summary>Which pieces of a file someone has: a bit for each.</summary>
public sealed class PieceMap
{
    private readonly byte[] _bits;
    private int _count;

    public PieceMap(int length)
    {
        Length = length;
        _bits = new byte[(length + 7) / 8];
    }

    public int Length { get; }
    public int Count => _count;
    public bool IsComplete => _count == Length;

    public bool this[int piece] => (uint)piece < (uint)Length && (_bits[piece >> 3] & (1 << (piece & 7))) != 0;

    public bool Set(int piece)
    {
        if ((uint)piece >= (uint)Length || this[piece]) return false;
        _bits[piece >> 3] |= (byte)(1 << (piece & 7));
        _count++;
        return true;
    }

    public bool Clear(int piece)
    {
        if (!this[piece]) return false;
        _bits[piece >> 3] &= (byte)~(1 << (piece & 7));
        _count--;
        return true;
    }

    public void SetAll()
    {
        for (int i = 0; i < Length; i++) Set(i);
    }

    public byte[] ToBytes() => (byte[])_bits.Clone();

    /// <summary>Reads a map of <paramref name="length"/> pieces from its bytes; false when the bytes are not that long.</summary>
    public static bool TryFromBytes(int length, ReadOnlySpan<byte> bytes, out PieceMap map)
    {
        map = new PieceMap(length);
        if (bytes.Length != map._bits.Length) return false;
        for (int i = 0; i < length; i++)
            if ((bytes[i >> 3] & (1 << (i & 7))) != 0) map.Set(i);
        return true;
    }
}
