using SkiaSharp;
using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>Gets a picture ready to send: still pictures are shrunk and re-encoded, which also drops camera and location metadata.</summary>
internal static class ImageTools
{
    public const int MaxSide = 1600;

    public sealed record Prepared(byte[] Bytes, string FileName, byte[]? Thumbnail);

    public static Prepared Prepare(byte[] input, string fileName)
    {
        if (input.Length > 40 * 1024 * 1024) throw new ArgumentException("That file is too big to send as a picture.");
        // A BMP is read and sent as a PNG or JPEG like any other still picture.
        bool bmp = input.Length > 2 && input[0] == (byte)'B' && input[1] == (byte)'M';
        if (!ImageSniffer.TryRead(input, out var info) && !bmp)
            throw new ArgumentException("That file is not a PNG, JPEG, GIF, WebP or BMP picture, or it is too large.");

        using var source = SKBitmap.Decode(input) ?? throw new ArgumentException("That picture could not be read.");
        var thumbnail = Thumbnail(source);

        // Animated pictures (a GIF, or an animated WebP) are sent as they are, since re-encoding would lose the animation, so they have to fit already.
        if (!bmp && info.Frames > 1)
        {
            if (input.Length > Attachment.MaxBytes)
                throw new ArgumentException($"That {(info.Mime == "image/gif" ? "GIF" : "animated picture")} is {input.Length / (1024 * 1024)} MB; the limit is {Attachment.MaxBytes / (1024 * 1024)} MB.");
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

    /// <summary>
    /// The small square picture of a link's card: the middle of the page's picture, as large as fits in <see cref="LinkPreview.MaxThumbnailBytes"/>.
    /// Null when the bytes are not a picture that can be read.
    /// </summary>
    public static byte[]? CardThumbnail(byte[] input)
    {
        if (!ImageSniffer.TryRead(input, out _)) return null;
        using var source = SKBitmap.Decode(input);
        if (source == null || source.Width < 1 || source.Height < 1) return null;
        int edge = Math.Min(source.Width, source.Height);
        var crop = new SKRect((source.Width - edge) / 2f, (source.Height - edge) / 2f, (source.Width + edge) / 2f, (source.Height + edge) / 2f);
        using var image = SKImage.FromBitmap(source);
        foreach (var (side, quality) in new[] { (LinkPreview.MaxThumbnailDimension, 60), (112, 55), (96, 50), (80, 45), (64, 40), (48, 35) })
        {
            using var flat = new SKBitmap(side, side, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(flat))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawImage(image, crop, new SKRect(0, 0, side, side), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            using var encoded = SKImage.FromBitmap(flat).Encode(SKEncodedImageFormat.Jpeg, quality);
            if (encoded != null && encoded.Size <= LinkPreview.MaxThumbnailBytes) return encoded.ToArray();
        }
        return null;
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

    /// <summary>
    /// A profile picture from any picture file: the middle square, shrunk to fit <see cref="ChatNode.MaxAvatarBytes"/> (128, then 96, then 64 pixels), as a JPEG.
    /// Throws <see cref="ArgumentException"/> when the file is not a picture.
    /// </summary>
    public static byte[] Avatar(byte[] input)
    {
        if (input.Length > 40 * 1024 * 1024 || !ImageSniffer.TryRead(input, out _)) throw new ArgumentException("That file is not a PNG, JPEG, GIF or WebP picture.");
        using var source = SKBitmap.Decode(input) ?? throw new ArgumentException("That picture could not be read.");
        int side = Math.Min(source.Width, source.Height);
        var crop = new SKRectI((source.Width - side) / 2, (source.Height - side) / 2, (source.Width - side) / 2 + side, (source.Height - side) / 2 + side);
        foreach (int size in new[] { 128, 96, 64 })
        {
            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(source);
            surface.Canvas.DrawImage(image, crop, new SKRect(0, 0, size, size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var small = surface.Snapshot();
            foreach (int quality in new[] { 85, 70, 50 })
            {
                using var data = small.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (data.Size <= ChatNode.MaxAvatarBytes) return data.ToArray();
            }
        }
        throw new ArgumentException("That picture could not be made small enough.");
    }
}
