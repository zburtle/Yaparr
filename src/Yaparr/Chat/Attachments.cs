using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>What a header check found out about a picture.</summary>
public sealed record ImageInfo(string Mime, int Width, int Height, int Frames);

/// <summary>
/// Reads just enough of a PNG, JPEG, GIF or WebP file to tell what it is and how big it is, without decoding it.
/// Files from other peers are checked with this before anything tries to decode them.
/// </summary>
public static class ImageSniffer
{
    public const int MaxDimension = 8192;
    public const long MaxPixels = 36_000_000;
    public const int MaxGifFrames = 600;
    public const int MaxGifDimension = 2048;

    public static bool TryRead(ReadOnlySpan<byte> d, out ImageInfo info)
    {
        info = null!;
        ImageInfo? found = null;
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G' && d[12] == 'I' && d[13] == 'H' && d[14] == 'D' && d[15] == 'R')
        {
            long w = BinaryPrimitives.ReadUInt32BigEndian(d[16..]), h = BinaryPrimitives.ReadUInt32BigEndian(d[20..]);
            if (w is > 0 and <= MaxDimension && h is > 0 and <= MaxDimension) found = new ImageInfo("image/png", (int)w, (int)h, 1);
        }
        else if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
            found = ReadJpeg(d);
        else if (d.Length >= 13 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8' && (d[4] == '7' || d[4] == '9') && d[5] == 'a')
            found = ReadGif(d);
        else if (d.Length >= 30 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
            found = ReadWebp(d);

        if (found == null || found.Width <= 0 || found.Height <= 0) return false;
        if (found.Width > MaxDimension || found.Height > MaxDimension || (long)found.Width * found.Height > MaxPixels) return false;
        info = found;
        return true;
    }

    private static ImageInfo? ReadJpeg(ReadOnlySpan<byte> d)
    {
        int pos = 2;
        while (pos + 4 <= d.Length)
        {
            if (d[pos] != 0xFF) return null;
            while (pos < d.Length && d[pos] == 0xFF) pos++;
            if (pos >= d.Length) return null;
            int marker = d[pos++];
            if (marker is 0x01 or (>= 0xD0 and <= 0xD8)) continue;
            if (marker is 0xD9 or 0xDA) return null; // end of image or start of data before a frame header
            if (pos + 2 > d.Length) return null;
            int length = BinaryPrimitives.ReadUInt16BigEndian(d[pos..]);
            if (length < 2) return null;
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (pos + 7 > d.Length) return null;
                return new ImageInfo("image/jpeg", BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 3)..]), 1);
            }
            pos += length;
        }
        return null;
    }

    private static ImageInfo? ReadGif(ReadOnlySpan<byte> d)
    {
        int w = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]), h = BinaryPrimitives.ReadUInt16LittleEndian(d[8..]);
        if (w == 0 || h == 0 || w > MaxGifDimension || h > MaxGifDimension) return null;
        int pos = 13;
        if ((d[10] & 0x80) != 0) pos += 3 * (1 << ((d[10] & 7) + 1));
        int frames = 0;
        while (pos < d.Length)
        {
            byte block = d[pos++];
            if (block == 0x3B) break;
            if (block == 0x21)
            {
                pos++; // extension label
                if (!SkipSubBlocks(d, ref pos)) return null;
            }
            else if (block == 0x2C)
            {
                if (pos + 9 > d.Length) return null;
                byte flags = d[pos + 8];
                pos += 9;
                if ((flags & 0x80) != 0) pos += 3 * (1 << ((flags & 7) + 1));
                pos++; // LZW minimum code size
                if (!SkipSubBlocks(d, ref pos)) return null;
                if (++frames > MaxGifFrames) return null;
            }
            else return null;
        }
        return frames == 0 ? null : new ImageInfo("image/gif", w, h, frames);
    }

    private static bool SkipSubBlocks(ReadOnlySpan<byte> d, ref int pos)
    {
        while (pos < d.Length)
        {
            int size = d[pos++];
            if (size == 0) return true;
            pos += size;
        }
        return false;
    }

    private static ImageInfo? ReadWebp(ReadOnlySpan<byte> d)
    {
        if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == ' ' && d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A)
            return new ImageInfo("image/webp", BinaryPrimitives.ReadUInt16LittleEndian(d[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(d[28..]) & 0x3FFF, 1);
        if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == 'L' && d[20] == 0x2F)
            return new ImageInfo("image/webp", 1 + (d[21] | ((d[22] & 0x3F) << 8)), 1 + ((d[22] >> 6) | (d[23] << 2) | ((d[24] & 0x0F) << 10)), 1);
        if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == 'X' && (d[20] & 0x02) == 0) // animated WebP is not supported
            return new ImageInfo("image/webp", 1 + (d[24] | (d[25] << 8) | (d[26] << 16)), 1 + (d[27] | (d[28] << 8) | (d[29] << 16)), 1);
        return null;
    }
}

/// <summary>
/// A picture or GIF attached to a message. The message itself only carries this small description (including a
/// content hash, which the message signature covers); the file is fetched separately from whoever has it.
/// </summary>
public sealed record Attachment(string Hash, string FileName, long Size, string Mime, int Width, int Height, byte[]? Thumbnail = null)
{
    public const long MaxBytes = 8 * 1024 * 1024;
    public const int MaxThumbnailBytes = 3000;
    public const int MaxThumbnailDimension = 128;
    private const string Marker = "\u0001yaparr-att1\u0001";

    public bool IsGif => Mime == "image/gif";

    public static string HashOf(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>The message text that carries this attachment, with an optional caption.</summary>
    public string ToText(string caption)
    {
        var dto = new Dto
        {
            H = Hash, N = FileName, S = Size, M = Mime, W = Width, X = Height,
            T = Thumbnail is { Length: > 0 } ? Convert.ToBase64String(Thumbnail) : null,
            C = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim() is { Length: > 1000 } longCaption ? longCaption[..1000] : caption.Trim(),
        };
        return Marker + JsonSerializer.Serialize(dto, Json);
    }

    /// <summary>Recognises a message that carries an attachment. Anything malformed or suspicious is treated as plain text.</summary>
    public static bool TryParse(string text, out Attachment attachment, out string caption)
    {
        attachment = null!;
        caption = text;
        if (!text.StartsWith(Marker, StringComparison.Ordinal)) return false;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(text.AsSpan(Marker.Length), Json);
            if (dto?.H is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)) return false;
            if (dto.S is < 1 or > MaxBytes || dto.M is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")) return false;
            if (dto.W is < 1 or > ImageSniffer.MaxDimension || dto.X is < 1 or > ImageSniffer.MaxDimension) return false;
            if ((long)dto.W * dto.X > ImageSniffer.MaxPixels) return false;

            byte[]? thumb = null;
            if (dto.T is { Length: > 0 and <= MaxThumbnailBytes * 2 })
            {
                try { thumb = Convert.FromBase64String(dto.T); } catch (FormatException) { }
                if (thumb != null && !(thumb.Length <= MaxThumbnailBytes && ImageSniffer.TryRead(thumb, out var t)
                        && t.Frames == 1 && t.Width <= MaxThumbnailDimension && t.Height <= MaxThumbnailDimension)) thumb = null;
            }
            attachment = new Attachment(hash.ToLowerInvariant(), CleanName(dto.N), dto.S, dto.M, dto.W, dto.X, thumb);
            caption = dto.C ?? "";
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c) && c is not ('/' or '\\' or ':')).ToArray()).Trim().TrimStart('.');
        return clean.Length == 0 ? "picture" : clean.Length <= 80 ? clean : clean[..80];
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed class Dto
    {
        public string? H { get; set; }
        public string? N { get; set; }
        public long S { get; set; }
        public string M { get; set; } = "";
        public int W { get; set; }
        public int X { get; set; }
        public string? T { get; set; }
        public string? C { get; set; }
    }
}

public enum AttachmentState
{
    /// <summary>The file is on this computer.</summary>
    Ready,
    /// <summary>Being downloaded.</summary>
    Fetching,
    /// <summary>Too big to fetch automatically over this connection; fetch it when the user asks.</summary>
    NeedsConfirmation,
    /// <summary>Nobody who is connected has it (yet).</summary>
    Unavailable,
}

public sealed record AttachmentUpdate(string Hash, AttachmentState State, long Received, long Total);

/// <summary>Files that belong to messages, stored under their content hash and, when there is a cipher, encrypted.</summary>
internal sealed class BlobStore
{
    private const long MemoryLimit = 64L * 1024 * 1024;
    private readonly string? _directory;
    private readonly HistoryCipher? _cipher;
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _memory = new();
    private readonly Queue<string> _memoryOrder = new();
    private long _memoryBytes;

    public BlobStore(string? directory, HistoryCipher? cipher = null)
    {
        _directory = directory;
        _cipher = cipher;
        if (directory != null) Directory.CreateDirectory(directory);
    }

    private static bool Valid(string hash) => hash.Length == 64 && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public bool Has(string hash)
    {
        if (!Valid(hash)) return false;
        if (_directory != null) return File.Exists(Path.Combine(_directory, hash));
        lock (_gate) return _memory.ContainsKey(hash);
    }

    public byte[]? Read(string hash) => Valid(hash) ? ReadRange(hash, 0, int.MaxValue) : null;

    public byte[]? ReadRange(string hash, long offset, int length)
    {
        if (!Valid(hash)) return null;
        if (_directory == null)
        {
            lock (_gate)
            {
                if (!_memory.TryGetValue(hash, out var all) || offset > all.Length) return null;
                return all.AsSpan((int)offset, (int)Math.Min(length, all.Length - offset)).ToArray();
            }
        }
        try
        {
            using var fs = new FileStream(Path.Combine(_directory, hash), FileMode.Open, FileAccess.Read, FileShare.Read);
            var start = new byte[HistoryCipher.SealedFileMagicLength];
            int got = fs.Read(start, 0, start.Length);
            if (HistoryCipher.IsSealedFile(start.AsSpan(0, got)))
                return _cipher?.OpenFileRange(hash, fs, offset, length);
            if (offset > fs.Length) return null;
            fs.Position = offset;
            var buffer = new byte[(int)Math.Min(length, fs.Length - offset)];
            fs.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Save(string hash, byte[] data)
    {
        if (!Valid(hash)) return;
        if (_directory != null)
        {
            var path = Path.Combine(_directory, hash);
            if (File.Exists(path)) return;
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, _cipher == null ? data : _cipher.SealFile(hash, data));
            File.Move(temp, path, overwrite: true);
            return;
        }
        lock (_gate)
        {
            if (!_memory.TryAdd(hash, data)) return;
            _memoryOrder.Enqueue(hash);
            _memoryBytes += data.Length;
            while (_memoryBytes > MemoryLimit && _memoryOrder.TryDequeue(out var old))
                if (_memory.Remove(old, out var gone)) _memoryBytes -= gone.Length;
        }
    }

    /// <summary>Encrypts files that an earlier version stored as they were, keeping their age so they are still swept when due.</summary>
    public void SealExisting()
    {
        if (_directory == null || _cipher == null) return;
        try
        {
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles().Where(f => Valid(f.Name)).ToList())
            {
                try
                {
                    var data = File.ReadAllBytes(file.FullName);
                    if (HistoryCipher.IsSealedFile(data.AsSpan(0, Math.Min(data.Length, HistoryCipher.SealedFileMagicLength)))) continue;
                    var age = file.LastWriteTimeUtc;
                    var temp = file.FullName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(temp, _cipher.SealFile(file.Name, data));
                    File.SetLastWriteTimeUtc(temp, age);
                    File.Move(temp, file.FullName, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // in use: next start
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes files older than <paramref name="maxAge"/>; they belong to messages that have aged out of the history.</summary>
    public void Sweep(TimeSpan maxAge)
    {
        if (_directory == null) return;
        try
        {
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles())
                if (DateTime.UtcNow - file.LastWriteTimeUtc > maxAge) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
