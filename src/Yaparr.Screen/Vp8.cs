namespace Yaparr.Screen;

/// <summary>The few facts about a VP8 frame that Yaparr needs without decoding it (RFC 6386, section 9.1).</summary>
public static class Vp8
{
    /// <summary>A key frame can be decoded without any earlier frame: bit 0 of the first byte is 0.</summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> frame) => frame.Length >= 10 && (frame[0] & 1) == 0 && frame[3] == 0x9d && frame[4] == 0x01 && frame[5] == 0x2a;

    /// <summary>The picture size, which only key frames carry.</summary>
    public static bool TryGetSize(ReadOnlySpan<byte> frame, out int width, out int height)
    {
        width = height = 0;
        if (!IsKeyFrame(frame)) return false;
        width = (frame[6] | (frame[7] << 8)) & 0x3FFF;
        height = (frame[8] | (frame[9] << 8)) & 0x3FFF;
        return width > 0 && height > 0;
    }

    /// <summary>Builds the start of a key frame, enough for <see cref="IsKeyFrame"/> and <see cref="TryGetSize"/>. For tests.</summary>
    public static byte[] FakeKeyFrame(int width, int height, int totalBytes = 64)
    {
        var f = new byte[Math.Max(10, totalBytes)];
        f[0] = 0x10; // key frame (bit 0 clear), version 0, shown
        f[3] = 0x9d; f[4] = 0x01; f[5] = 0x2a;
        f[6] = (byte)width; f[7] = (byte)(width >> 8);
        f[8] = (byte)height; f[9] = (byte)(height >> 8);
        return f;
    }
}
