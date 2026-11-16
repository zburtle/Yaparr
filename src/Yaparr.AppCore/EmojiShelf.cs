using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>One custom emoji of a person's own: the name in <c>:name:</c>, the picture's type and size.</summary>
internal sealed record OwnEmoji(string Name, string Mime, long Bytes);

/// <summary>
/// A person's own custom emoji: a folder on this device, one small picture for each name, nothing more. Nothing about it is shared or looked up anywhere;
/// a message that uses one carries the picture with it (see <see cref="CustomEmoji"/>), so the people who read it see the same picture without having it.
/// It is part of the backup like the rest of the profile. A room has its own emoji as well (<see cref="ChatRoom.Emoji"/>).
/// </summary>
internal sealed class EmojiShelf
{
    private readonly string _directory;

    public EmojiShelf(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private static string Extension(string mime) => mime switch { "image/gif" => ".gif", "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };

    private static string? MimeOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".gif" => "image/gif", ".png" => "image/png", ".webp" => "image/webp", ".jpg" => "image/jpeg", _ => null,
    };

    // The file for a name, whatever its type is. Only names this class made are looked for, so a name is never a path.
    private string? PathOf(string name)
    {
        if (!CustomEmojiRules.IsValidName(name)) return null;
        foreach (var extension in new[] { ".png", ".gif", ".webp", ".jpg" })
        {
            var path = Path.Combine(_directory, name + extension);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Everything on the shelf, by name.</summary>
    public List<OwnEmoji> List()
    {
        var items = new List<OwnEmoji>();
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!CustomEmojiRules.IsValidName(name) || MimeOf(Path.GetExtension(path)) is not { } mime) continue;
            items.Add(new OwnEmoji(name, mime, new FileInfo(path).Length));
        }
        return items.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Puts a picture on the shelf under a name; a picture already under that name is replaced. Throws <see cref="ArgumentException"/> with a message for the
    /// person: a name that cannot be used (or is a standard emoji's), or a picture that is not one or is too big for an emoji.
    /// </summary>
    public OwnEmoji Add(string name, byte[] data)
    {
        var clean = CustomEmojiRules.CleanName(name)
                    ?? throw new ArgumentException($"A name is {CustomEmojiRules.MinNameLength} to {CustomEmojiRules.MaxNameLength} letters, digits or underscores.");
        if (EmojiTyping.FromShortcode(clean) != null) throw new ArgumentException($":{clean}: is the name of a standard emoji. Choose another name.");
        if (!CustomEmojiRules.TryRead(data, out var info, out var problem)) throw new ArgumentException(problem);
        Remove(clean);
        File.WriteAllBytes(Path.Combine(_directory, clean + Extension(info.Mime)), data);
        return new OwnEmoji(clean, info.Mime, data.Length);
    }

    public bool Has(string name) => PathOf(name) != null;

    public byte[]? Read(string name) => PathOf(name) is { } path ? File.ReadAllBytes(path) : null;

    public void Remove(string name)
    {
        if (PathOf(name) is { } path) File.Delete(path);
    }
}
