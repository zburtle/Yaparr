using System.Security.Cryptography;
using SkiaSharp;
using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>One picture on the shelf.</summary>
/// <param name="Id">What the file is called (its content, so the same picture is only ever on the shelf once).</param>
/// <param name="Mime">image/gif, image/png, image/jpeg or image/webp.</param>
/// <param name="Bytes">The size of the file.</param>
/// <param name="LastUsed">When it was added or last sent; the shelf is ordered by this, newest first.</param>
internal sealed record ShelfItem(string Id, string Mime, long Bytes, DateTime LastUsed)
{
    public bool IsGif => Mime == "image/gif";
}

/// <summary>
/// The GIFs and pictures a person keeps close at hand to send again: a folder on this device, nothing more. Nothing about it is shared or
/// looked up anywhere, and there is no limit on how many or how big (sending one is still bound by the usual limit for a picture). It is part of
/// the backup like the rest of the profile.
/// </summary>
internal sealed class GifShelf
{
    private const int PreviewSide = 200;
    private readonly string _directory;
    private readonly string _previews;

    public GifShelf(string directory)
    {
        _directory = directory;
        _previews = Path.Combine(directory, "previews");
        Directory.CreateDirectory(_previews);
    }

    private static string Extension(string mime) => mime switch { "image/gif" => ".gif", "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };

    private static string? MimeOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".gif" => "image/gif", ".png" => "image/png", ".webp" => "image/webp", ".jpg" => "image/jpeg", _ => null,
    };

    /// <summary>Everything on the shelf, the most recently used first.</summary>
    public List<ShelfItem> List()
    {
        var items = new List<ShelfItem>();
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            if (MimeOf(Path.GetExtension(path)) is not { } mime) continue;
            var info = new FileInfo(path);
            items.Add(new ShelfItem(info.Name, mime, info.Length, info.LastWriteTimeUtc));
        }
        return items.OrderByDescending(i => i.LastUsed).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Puts a picture on the shelf (or brings it to the front if it is there already). Throws <see cref="ArgumentException"/> with a message for the person.</summary>
    public ShelfItem Add(byte[] data)
    {
        if (!ImageSniffer.TryRead(data, out var info))
            throw new ArgumentException("That file is not a PNG, JPEG, GIF or WebP picture, or it is too large.");
        var id = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()[..32] + Extension(info.Mime);
        var path = Path.Combine(_directory, id);
        if (File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        else File.WriteAllBytes(path, data);
        return new ShelfItem(id, info.Mime, data.Length, File.GetLastWriteTimeUtc(path));
    }

    public byte[]? Read(string id) => IsValid(id) && File.Exists(Path.Combine(_directory, id)) ? File.ReadAllBytes(Path.Combine(_directory, id)) : null;

    /// <summary>Moves a picture to the front, because it was just sent.</summary>
    public void Used(string id)
    {
        if (IsValid(id) && File.Exists(Path.Combine(_directory, id))) File.SetLastWriteTimeUtc(Path.Combine(_directory, id), DateTime.UtcNow);
    }

    public void Remove(string id)
    {
        if (!IsValid(id)) return;
        File.Delete(Path.Combine(_directory, id));
        File.Delete(Path.Combine(_previews, id + ".jpg"));
    }

    /// <summary>A small still picture (a JPEG) to show in the grid, made once and kept.</summary>
    public byte[]? Preview(string id)
    {
        if (!IsValid(id)) return null;
        var cached = Path.Combine(_previews, id + ".jpg");
        if (File.Exists(cached)) return File.ReadAllBytes(cached);
        var data = Read(id);
        if (data == null) return null;
        var bytes = PreviewOf(data);
        if (bytes == null) return null;
        File.WriteAllBytes(cached, bytes);
        return bytes;
    }

    /// <summary>A small still picture (a JPEG) of any picture file, for a grid.</summary>
    public static byte[]? PreviewOf(byte[] data)
    {
        using var source = SKBitmap.Decode(data);
        if (source == null) return null;
        double scale = Math.Min(1.0, (double)PreviewSide / Math.Max(source.Width, source.Height));
        int w = Math.Max(1, (int)(source.Width * scale)), h = Math.Max(1, (int)(source.Height * scale));
        using var flat = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(new SKColor(0x80, 0x80, 0x80));
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        using var encoded = SKImage.FromBitmap(flat).Encode(SKEncodedImageFormat.Jpeg, 80);
        return encoded?.ToArray();
    }

    // An id is a name this class made: hex, a dot and an extension. Anything else could be a path.
    private static bool IsValid(string id) =>
        id.Length > 33 && id.Length < 40 && id[32] == '.' && id.AsSpan(0, 32).IndexOfAnyExcept("0123456789abcdef") < 0 && MimeOf(Path.GetExtension(id)) != null;
}
