using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Yaparr.AndroidApp;

/// <summary>The profile pictures people chose, as brushes for the round avatars. Without one, an avatar is its coloured initial.</summary>
public static class AvatarBrushes
{
    /// <summary>Gives the picture file of a person (their account id), or null; set by the session.</summary>
    public static Func<string, byte[]?>? Source { get; set; }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], IBrush> Cache = new();

    /// <summary>The picture of a person as a brush, or null when they have none (or it cannot be read).</summary>
    public static IBrush? For(string? account)
    {
        if (account == null || Source?.Invoke(account) is not { } bytes) return null;
        if (Cache.TryGetValue(bytes, out var known)) return known;
        try
        {
            var brush = new ImageBrush(new Bitmap(new MemoryStream(bytes))) { Stretch = Stretch.UniformToFill };
            Cache.Add(bytes, brush);
            return brush;
        }
        catch (Exception) { return null; }
    }
}
