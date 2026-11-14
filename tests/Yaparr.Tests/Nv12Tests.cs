using Yaparr.Screen;

namespace Yaparr.Tests;

public class Nv12Tests
{
    private static byte[] Picture(int width, int height, Random random)
    {
        var data = new byte[Nv12.Size(width, height)];
        random.NextBytes(data);
        return data;
    }

    // The same sum written out one pixel at a time.
    private static byte[] Reference(byte[] nv12, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
            for (int x = 0; x < width; x++)
            {
                int c = width * height + row / 2 * width + x / 2 * 2;
                uint p = Nv12.Pixel(nv12[row * width + x], nv12[c] - 128, nv12[c + 1] - 128);
                BitConverter.TryWriteBytes(bgra.AsSpan((row * width + x) * 4), p);
            }
        return bgra;
    }

    [Theory]
    [InlineData(16, 8)]
    [InlineData(18, 6)]   // not a multiple of eight: the last pixels take the plain path
    [InlineData(1920, 1080)]
    public void MatchesTheSumDoneOnePixelAtATime(int width, int height)
    {
        var nv12 = Picture(width, height, new Random(7));
        var bgra = new byte[width * height * 4];
        Nv12.ToBgra(nv12, width, height, bgra);
        Assert.Equal(Reference(nv12, width, height), bgra);
    }

    [Fact]
    public void BlackWhiteAndGrey()
    {
        int w = 16, h = 2;
        var nv12 = new byte[Nv12.Size(w, h)];
        for (int i = 0; i < w; i++) { nv12[i] = 16; nv12[w + i] = 235; }   // top row black, bottom row white
        for (int i = 0; i < w; i++) nv12[w * h + i] = 128;                  // no colour
        var bgra = new byte[w * h * 4];
        Nv12.ToBgra(nv12, w, h, bgra);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, bgra[..4]);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, bgra[(w * 4)..(w * 4 + 4)]);
    }

    [Fact]
    public void RefusesAnOddSizeOrAShortPicture()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Nv12.ToBgra(new byte[100], 5, 4, new byte[80]));
        Assert.Throws<ArgumentException>(() => Nv12.ToBgra(new byte[10], 4, 4, new byte[64]));
    }
}
