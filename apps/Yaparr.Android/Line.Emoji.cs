using Avalonia.Media;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

// The emoji in a message: the custom ones it uses (with their pictures, once they have arrived) and whether it is nothing but a few emoji.
public sealed partial class Line
{
    /// <summary>The custom emoji the words use, as the message described them (their pictures are fetched as the message arrives).</summary>
    public IReadOnlyList<CustomEmoji>? Emoji { get; set; }

    /// <summary>The pictures of the custom emoji that are on this phone now, by name; the rest show as their :name: until they arrive.</summary>
    public IReadOnlyDictionary<string, IImage>? EmojiImages
    {
        get
        {
            if (Emoji == null) return null;
            var images = new Dictionary<string, IImage>();
            foreach (var e in Emoji)
                if (EmojiPictures.For(e) is { } image) images[e.Name] = image;
            return images;
        }
    }

    /// <summary>A picture of an emoji arrived: the words are drawn again with it.</summary>
    public void RaiseEmoji() => Raise(nameof(EmojiImages));

    /// <summary>A message of nothing but one to three emoji (standard or custom), shown large.</summary>
    public bool IsBigEmoji => Attachment == null
        && (EmojiTyping.IsOnlyEmoji(Text) || Emoji is { Count: > 0 } && CustomEmojiRules.IsOnlyEmoji(Text, Emoji.Select(e => e.Name).ToList()));
}
