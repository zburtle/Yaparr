using System.Buffers.Binary;
using Yaparr.AppCore;

namespace Yaparr.Tests;

// A person's own custom emoji: a folder on this computer.
public sealed class EmojiShelfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-emoji-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static byte[] Png(int width, int height = 20, int padding = 0)
    {
        var b = new byte[40 + padding];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)height);
        return b;
    }

    [Fact]
    public void An_emoji_is_kept_under_its_name_and_listed_by_name()
    {
        var shelf = new EmojiShelf(_dir);
        var party = Png(32);
        var added = shelf.Add(" Party ", party);
        Assert.Equal("party", added.Name);
        Assert.Equal("image/png", added.Mime);
        shelf.Add("kitty", Png(33));
        Assert.Equal(new[] { "kitty", "party" }, shelf.List().Select(e => e.Name));
        Assert.Equal(party, shelf.Read("party"));
        Assert.True(shelf.Has("kitty"));
        Assert.Null(shelf.Read("dog"));
        Assert.Null(shelf.Read("../party"));
    }

    [Fact]
    public void A_new_picture_under_a_name_replaces_the_old_one()
    {
        var shelf = new EmojiShelf(_dir);
        shelf.Add("party", Png(32));
        var newer = Png(40);
        shelf.Add("party", newer);
        Assert.Single(shelf.List());
        Assert.Equal(newer, shelf.Read("party"));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void An_emoji_is_removed_by_name()
    {
        var shelf = new EmojiShelf(_dir);
        shelf.Add("party", Png(32));
        shelf.Remove("party");
        shelf.Remove("party");           // nothing there: no harm
        shelf.Remove("../party");
        Assert.Empty(shelf.List());
        Assert.False(shelf.Has("party"));
    }

    [Fact]
    public void What_cannot_be_an_emoji_is_refused_with_a_reason()
    {
        var shelf = new EmojiShelf(_dir);
        Assert.Contains("letters", Assert.Throws<ArgumentException>(() => shelf.Add("a", Png(32))).Message);
        Assert.Contains("letters", Assert.Throws<ArgumentException>(() => shelf.Add("two words", Png(32))).Message);
        Assert.Contains("standard emoji", Assert.Throws<ArgumentException>(() => shelf.Add("fire", Png(32))).Message);
        Assert.Contains("not a PNG", Assert.Throws<ArgumentException>(() => shelf.Add("text", "words"u8.ToArray())).Message);
        Assert.Contains("300x20", Assert.Throws<ArgumentException>(() => shelf.Add("wide", Png(300))).Message);
        Assert.Contains("KB", Assert.Throws<ArgumentException>(() => shelf.Add("heavy", Png(32, 20, 300_000))).Message);
        Assert.Empty(shelf.List());
    }

    [Fact]
    public void Files_that_are_not_emoji_are_left_alone()
    {
        var shelf = new EmojiShelf(_dir);
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");
        File.WriteAllBytes(Path.Combine(_dir, "Bad Name.png"), Png(32));
        shelf.Add("good", Png(32));
        Assert.Equal(new[] { "good" }, shelf.List().Select(e => e.Name));
    }

    private static byte[] RealPng(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.Orange);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public void A_big_still_picture_is_shrunk_to_fit_as_an_emoji_and_one_that_fits_is_left_alone()
    {
        var big = RealPng(900, 450);
        var made = ImageTools.ForEmoji(big);
        Assert.NotEqual(big, made);
        Assert.True(Yaparr.Chat.CustomEmojiRules.TryRead(made, out var info, out _));
        Assert.Equal(128, info.Width);
        Assert.Equal(64, info.Height);

        var small = RealPng(40, 40);
        Assert.Equal(small, ImageTools.ForEmoji(small));
        var notAPicture = "words"u8.ToArray();
        Assert.Equal(notAPicture, ImageTools.ForEmoji(notAPicture));
    }
}
