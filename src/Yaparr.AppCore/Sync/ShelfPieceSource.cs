using System.Security.Cryptography;
using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>
/// The person's own shelves as <see cref="IPieceSource"/>: each custom emoji is a piece <c>e-&lt;name&gt;</c> (tag: a hash of the picture, so a replaced picture is a change) and each picture
/// on the GIF shelf a piece <c>g-&lt;id&gt;</c> (its id is a hash of the picture, so there is nothing to change). Unlike history, taking one off a shelf is a removal that goes to the
/// account, and a removal on another device removes it here. The order of the GIF shelf (what was sent last) stays on the device.
/// </summary>
internal sealed class ShelfPieceSource(EmojiShelf emoji, GifShelf gifs) : IPieceSource
{
    private const string EmojiPrefix = "e-", GifPrefix = "g-";

    private static string Tag(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data))[..16];

    public IReadOnlyDictionary<string, string> Local()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in emoji.List())
            if (emoji.Read(item.Name) is { } data) all[EmojiPrefix + item.Name] = Tag(data);
        foreach (var item in gifs.List()) all[GifPrefix + IdOf(item.Id)] = "1";
        return all;
    }

    // A shelf id is "<32 hex>.<extension>"; the piece is named by the hash alone, the type is found again from the picture itself.
    private static string IdOf(string shelfId) => shelfId[..32];

    private string? ShelfIdOf(string hash) => gifs.List().FirstOrDefault(i => i.Id.StartsWith(hash, StringComparison.Ordinal))?.Id;

    public byte[]? Build(string name)
    {
        if (name.StartsWith(EmojiPrefix, StringComparison.Ordinal)) return emoji.Read(name[EmojiPrefix.Length..]);
        if (name.StartsWith(GifPrefix, StringComparison.Ordinal) && ShelfIdOf(name[GifPrefix.Length..]) is { } id) return gifs.Read(id);
        return null;
    }

    public bool Take(string name, byte[] content)
    {
        try
        {
            if (name.StartsWith(EmojiPrefix, StringComparison.Ordinal))
            {
                var shortName = name[EmojiPrefix.Length..];
                if (emoji.Read(shortName) is { } have && have.AsSpan().SequenceEqual(content)) return true;
                emoji.Add(shortName, content);
                return true;
            }
            if (name.StartsWith(GifPrefix, StringComparison.Ordinal))
            {
                // The name is the hash of the picture: a piece that is not what its name says is not taken.
                if (Convert.ToHexStringLower(SHA256.HashData(content))[..32] != name[GifPrefix.Length..]) return false;
                gifs.Add(content);
                return true;
            }
        }
        catch (ArgumentException) { return false; }
        return false;
    }

    public bool Removable(string name) => name.StartsWith(EmojiPrefix, StringComparison.Ordinal) || name.StartsWith(GifPrefix, StringComparison.Ordinal);

    public void Drop(string name)
    {
        if (name.StartsWith(EmojiPrefix, StringComparison.Ordinal)) emoji.Remove(name[EmojiPrefix.Length..]);
        else if (name.StartsWith(GifPrefix, StringComparison.Ordinal) && ShelfIdOf(name[GifPrefix.Length..]) is { } id) gifs.Remove(id);
    }
}

/// <summary>Several sources as one: a piece belongs to the source that owns its name (by prefix).</summary>
internal sealed class PieceSources(params (string Prefix, IPieceSource Source)[] sources) : IPieceSource
{
    private IPieceSource? Of(string name) => sources.FirstOrDefault(s => name.StartsWith(s.Prefix, StringComparison.Ordinal)).Source;

    public IReadOnlyDictionary<string, string> Local()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (prefix, source) in sources)
            foreach (var (name, tag) in source.Local())
                if (name.StartsWith(prefix, StringComparison.Ordinal)) all[name] = tag;
        return all;
    }

    public byte[]? Build(string name) => Of(name)?.Build(name);

    public bool Take(string name, byte[] content) => Of(name)?.Take(name, content) ?? false;

    public bool Removable(string name) => Of(name)?.Removable(name) ?? false;

    public void Drop(string name) => Of(name)?.Drop(name);
}
