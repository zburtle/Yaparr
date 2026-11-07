using SkiaSharp;
using Yaparr.AppCore;

namespace Yaparr.Tests;

public sealed class GifShelfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-shelf-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static byte[] Png(int w, int h, SKColor color)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public void A_picture_is_kept_once_and_listed()
    {
        var shelf = new GifShelf(_dir);
        var red = Png(40, 30, SKColors.Red);
        var first = shelf.Add(red);
        var again = shelf.Add(red);
        Assert.Equal(first.Id, again.Id);
        Assert.Single(shelf.List());
        Assert.Equal("image/png", first.Mime);
        Assert.Equal(red, shelf.Read(first.Id));
    }

    [Fact]
    public void The_one_used_last_comes_first_and_there_is_no_limit()
    {
        var shelf = new GifShelf(_dir);
        var ids = new List<string>();
        for (int i = 0; i < 60; i++) ids.Add(shelf.Add(Png(8 + i, 8, new SKColor((byte)i, 20, 30))).Id);
        Assert.Equal(60, shelf.List().Count);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, ids[0]), DateTime.UtcNow.AddDays(-5));
        shelf.Used(ids[30]);
        Assert.Equal(ids[30], shelf.List()[0].Id);
        Assert.Equal(ids[0], shelf.List()[^1].Id);
    }

    [Fact]
    public void Something_that_is_not_a_picture_is_refused()
    {
        var shelf = new GifShelf(_dir);
        Assert.Throws<ArgumentException>(() => shelf.Add("not a picture"u8.ToArray()));
        Assert.Empty(shelf.List());
    }

    [Fact]
    public void A_picture_can_be_removed_with_its_preview()
    {
        var shelf = new GifShelf(_dir);
        var item = shelf.Add(Png(300, 100, SKColors.Blue));
        var preview = shelf.Preview(item.Id);
        Assert.NotNull(preview);
        using (var small = SKBitmap.Decode(preview!)) Assert.Equal(200, small.Width);
        shelf.Remove(item.Id);
        Assert.Empty(shelf.List());
        Assert.Null(shelf.Read(item.Id));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "previews")));
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData(@"..\secret.png")]
    [InlineData("abc.png")]
    [InlineData("0123456789abcdef0123456789abcdef.exe")]
    public void A_name_that_the_shelf_did_not_make_is_never_touched(string id)
    {
        var shelf = new GifShelf(_dir);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(_dir)!, "secret.png"), Png(4, 4, SKColors.Green));
        try
        {
            Assert.Null(shelf.Read(id));
            Assert.Null(shelf.Preview(id));
            shelf.Remove(id);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "secret.png")));
        }
        finally { File.Delete(Path.Combine(Path.GetDirectoryName(_dir)!, "secret.png")); }
    }
}
