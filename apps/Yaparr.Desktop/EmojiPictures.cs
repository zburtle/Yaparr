using Avalonia.Media;
using Avalonia.Media.Imaging;
using Yaparr.Chat;

namespace Yaparr.Desktop;

/// <summary>The pictures of custom emoji, as images to draw: decoded once and kept, since the same emoji shows over and over in a chat.</summary>
public static class EmojiPictures
{
    private const int MaxKept = 300;
    private static readonly Dictionary<string, Bitmap> Kept = new();

    /// <summary>Reads the file of a picture by its hash (the chat library holds them); set when the app starts.</summary>
    public static Func<string, byte[]?>? Reader { get; set; }

    /// <summary>The picture of an emoji a message uses, or null while its file has not arrived.</summary>
    public static IImage? For(CustomEmoji emoji)
    {
        lock (Kept)
        {
            if (Kept.TryGetValue(emoji.Hash, out var found)) return found;
        }
        var bytes = Reader?.Invoke(emoji.Hash);
        if (bytes == null || FromBytes(bytes) is not Bitmap bitmap) return null;
        lock (Kept)
        {
            if (Kept.Count >= MaxKept) Kept.Clear();
            Kept[emoji.Hash] = bitmap;
        }
        return bitmap;
    }

    /// <summary>An image from a picture file, or null if it cannot be read as one.</summary>
    public static IImage? FromBytes(byte[] bytes)
    {
        try { return new Bitmap(new MemoryStream(bytes)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException) { return null; }
    }
}
