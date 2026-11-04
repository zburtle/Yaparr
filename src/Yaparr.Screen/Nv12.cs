using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Yaparr.Screen;

/// <summary>
/// NV12 to BGRA. FFmpeg hands the viewer NV12 (a plane of brightness, then one plane of interleaved blue and red differences at
/// half the size), which is 1.5 bytes a pixel instead of 4 and costs far less to produce than BGRA, since the decoder's own
/// colour conversion was the larger part of the viewer's processor time. The conversion is done here with vector instructions.
/// Colours are BT.601 with the limited range (16 to 235), which is what the stream is marked with and what FFmpeg would use.
/// </summary>
public static class Nv12
{
    /// <summary>The size of one NV12 picture: the brightness plane and half as much again for the colour. Width and height must be even.</summary>
    public static int Size(int width, int height) => width * height * 3 / 2;

    /// <summary>Converts a picture. <paramref name="bgra"/> must hold width x height x 4 bytes.</summary>
    public static void ToBgra(ReadOnlySpan<byte> nv12, int width, int height, Span<byte> bgra)
    {
        if (width < 2 || height < 2 || (width | height) % 2 != 0) throw new ArgumentOutOfRangeException(nameof(width), "The size must be even.");
        if (nv12.Length < Size(width, height)) throw new ArgumentException("The picture is too short.", nameof(nv12));
        if (bgra.Length < width * height * 4) throw new ArgumentException("The output is too short.", nameof(bgra));
        var y = nv12[..(width * height)];
        var uv = nv12[(width * height)..];
        var output = MemoryMarshal.Cast<byte, uint>(bgra);
        for (int row = 0; row < height; row++)
            Row(y.Slice(row * width, width), uv.Slice(row / 2 * width, width), output.Slice(row * width, width));
    }

    private static void Row(ReadOnlySpan<byte> y, ReadOnlySpan<byte> uv, Span<uint> output)
    {
        int x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            // Eight pixels at a time: eight brightness values and four pairs of colour, each pair serving two pixels.
            var sixteen = Vector256.Create(16); var c128 = Vector256.Create(128); var round = Vector256.Create(128);
            var zero = Vector256<int>.Zero; var max = Vector256.Create(255);
            var shape = Vector128.Create((byte)0, 0, 2, 2, 4, 4, 6, 6, 1, 1, 3, 3, 5, 5, 7, 7);   // u u u u u u u u v v v v v v v v
            for (; x + 8 <= y.Length; x += 8)
            {
                var yi = Ints(Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.AsRef(in y[x]))).AsByte()));
                // The eight bytes u0 v0 u1 v1 u2 v2 u3 v3 turn into u0 u0 u1 u1 u2 u2 u3 u3 and v0 v0 v1 v1 v2 v2 v3 v3.
                var spread = Vector128.Shuffle(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.AsRef(in uv[x]))).AsByte(), shape);
                var ui = Ints(Vector128.WidenLower(spread)) - c128;
                var vi = Ints(Vector128.WidenUpper(spread)) - c128;
                var luma = (yi - sixteen) * Vector256.Create(298) + round;
                var r = Vector256.Min(Vector256.Max((luma + vi * Vector256.Create(409)) >> 8, zero), max);
                var g = Vector256.Min(Vector256.Max((luma - ui * Vector256.Create(100) - vi * Vector256.Create(208)) >> 8, zero), max);
                var b = Vector256.Min(Vector256.Max((luma + ui * Vector256.Create(516)) >> 8, zero), max);
                (b | (g << 8) | (r << 16) | Vector256.Create(unchecked((int)0xFF000000))).AsUInt32().CopyTo(output.Slice(x, 8));
            }
        }
        for (; x < y.Length; x += 2)
        {
            int u = uv[x] - 128, v = uv[x + 1] - 128;
            output[x] = Pixel(y[x], u, v);
            output[x + 1] = Pixel(y[x + 1], u, v);
        }
    }

    private static Vector256<int> Ints(Vector128<ushort> eight) => Vector256.Create(Vector128.WidenLower(eight).AsInt32(), Vector128.WidenUpper(eight).AsInt32());

    /// <summary>One pixel from its brightness and its two colour differences (already minus 128).</summary>
    public static uint Pixel(int y, int u, int v)
    {
        int luma = (y - 16) * 298 + 128;
        int r = Math.Clamp((luma + 409 * v) >> 8, 0, 255);
        int g = Math.Clamp((luma - 100 * u - 208 * v) >> 8, 0, 255);
        int b = Math.Clamp((luma + 516 * u) >> 8, 0, 255);
        return 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
    }
}
