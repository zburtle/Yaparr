using SkiaSharp;
using Yaparr.Chat;

namespace Yaparr.Desktop;

/// <summary>Gets a picture ready to send: still pictures are shrunk and re-encoded, which also drops camera and location metadata.</summary>
internal static class ImageTools
{
    public const int MaxSide = 1600;

    public sealed record Prepared(byte[] Bytes, string FileName, byte[]? Thumbnail);

    public static Prepared Prepare(byte[] input, string fileName)
    {
        if (input.Length > 40 * 1024 * 1024) throw new ArgumentException("That file is too big to send as a picture.");
        if (!ImageSniffer.TryRead(input, out var info))
            throw new ArgumentException("That file is not a PNG, JPEG, GIF or WebP picture, or it is too large.");

        using var source = SKBitmap.Decode(input) ?? throw new ArgumentException("That picture could not be read.");
        var thumbnail = Thumbnail(source);

        // GIFs are sent as they are (re-encoding would lose the animation), so they have to fit already.
        if (info.Mime == "image/gif")
        {
            if (input.Length > Attachment.MaxBytes)
                throw new ArgumentException($"That GIF is {input.Length / (1024 * 1024)} MB; the limit is {Attachment.MaxBytes / (1024 * 1024)} MB.");
            return new Prepared(input, fileName, thumbnail);
        }

        SKBitmap? scaled = null;
        try
        {
            var bitmap = source;
            if (Math.Max(source.Width, source.Height) > MaxSide)
            {
                double scale = (double)MaxSide / Math.Max(source.Width, source.Height);
                scaled = source.Resize(new SKImageInfo(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale))),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (scaled != null) bitmap = scaled;
            }

            bool transparent = bitmap.Pixels.Any(p => p.Alpha < 255);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = transparent ? image.Encode(SKEncodedImageFormat.Png, 100) : image.Encode(SKEncodedImageFormat.Jpeg, 85);
            if (data == null) throw new ArgumentException("That picture could not be converted.");
            var bytes = data.ToArray();
            if (bytes.Length > Attachment.MaxBytes) throw new ArgumentException("That picture is too big to send even after shrinking it.");
            return new Prepared(bytes, Path.ChangeExtension(Path.GetFileName(fileName), transparent ? ".png" : ".jpg"), thumbnail);
        }
        finally { scaled?.Dispose(); }
    }

    /// <summary>A tiny preview that travels inside the message, so there is something to show before the file arrives.</summary>
    private static byte[]? Thumbnail(SKBitmap source)
    {
        foreach (var (side, quality) in new[] { (72, 45), (64, 35), (48, 30), (32, 30) })
        {
            double scale = Math.Min(1.0, (double)side / Math.Max(source.Width, source.Height));
            int w = Math.Max(1, (int)(source.Width * scale)), h = Math.Max(1, (int)(source.Height * scale));
            using var flat = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(flat))
            {
                canvas.Clear(new SKColor(0x80, 0x80, 0x80)); // transparent areas become neutral grey
                using var image = SKImage.FromBitmap(source);
                canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            using var encoded = SKImage.FromBitmap(flat).Encode(SKEncodedImageFormat.Jpeg, quality);
            if (encoded != null && encoded.Size <= Attachment.MaxThumbnailBytes) return encoded.ToArray();
        }
        return null;
    }
}
