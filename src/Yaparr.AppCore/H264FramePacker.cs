namespace Yaparr.AppCore;

/// <summary>
/// Turns what a phone's H.264 encoder hands out into frames a viewer can use (the desktop sends the same, see <c>H264</c> in Yaparr.Screen):
/// each frame is one access unit of an Annex B byte stream, and a key frame carries the parameter sets (SPS and PPS) in front of its IDR picture,
/// so a viewer who has just joined can start from it and finds the picture size in it. Android's MediaCodec gives the parameter sets once, in a
/// buffer of their own flagged as codec configuration, and the key frames after it without them; this puts them back in front of each key frame.
/// </summary>
internal sealed class H264FramePacker
{
    private const int NalIdr = 5, NalSps = 7, NalPps = 8;

    private byte[]? _config;

    /// <summary>Whether the parameter sets are known (the encoder has said what it makes).</summary>
    public bool HasConfig => _config != null;

    /// <summary>The encoder's codec configuration buffer: the SPS and PPS, each behind a start code.</summary>
    public void SetConfig(ReadOnlySpan<byte> config)
    {
        var kinds = Kinds(config);
        if (kinds.Sps && kinds.Pps) _config = config.ToArray();
    }

    /// <summary>
    /// The frame to send for an encoded buffer, or null when it cannot be used yet (a key frame before the encoder has said what it
    /// makes, or nothing in the buffer).
    /// </summary>
    /// <param name="encoded">One access unit as the encoder wrote it, with start codes.</param>
    /// <param name="isKey">The encoder flagged it as a sync (key) frame.</param>
    public byte[]? Pack(ReadOnlySpan<byte> encoded, bool isKey)
    {
        if (encoded.Length == 0) return null;
        if (!isKey) return encoded.ToArray();

        var kinds = Kinds(encoded);
        if (kinds.Sps && kinds.Pps) return encoded.ToArray();   // some encoders repeat them with every key frame
        if (_config == null) return null;
        var frame = new byte[_config.Length + encoded.Length];
        _config.CopyTo(frame, 0);
        encoded.CopyTo(frame.AsSpan(_config.Length));
        return frame;
    }

    private static (bool Sps, bool Pps, bool Idr) Kinds(ReadOnlySpan<byte> data)
    {
        bool sps = false, pps = false, idr = false;
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1) continue;
            switch (data[i + 3] & 0x1F)
            {
                case NalSps: sps = true; break;
                case NalPps: pps = true; break;
                case NalIdr: idr = true; break;
            }
            i += 2;
        }
        return (sps, pps, idr);
    }

    /// <summary>
    /// The size to encode a captured area at: the same shape, with the longer side at most <paramref name="maxLongSide"/>, and both sides even
    /// (an H.264 encoder cannot make an odd size). The shared area can be any size: a whole phone screen, or one app in a window or a
    /// folded screen.
    /// </summary>
    public static (int Width, int Height) FitSize(int width, int height, int maxLongSide)
    {
        if (width < 2 || height < 2) return (2, 2);
        double scale = Math.Min(1.0, (double)maxLongSide / Math.Max(width, height));
        int w = Math.Max(2, (int)Math.Round(width * scale / 2) * 2);
        int h = Math.Max(2, (int)Math.Round(height * scale / 2) * 2);
        return (w, h);
    }

    /// <summary>
    /// A bitrate (kbit/s) that suits a size and a frame rate: enough for text to stay readable, and no more than <paramref name="ceiling"/>.
    /// Where a person's upload can only feed a few viewers, that is the number to lower.
    /// </summary>
    public static int SuggestKbps(int width, int height, int fps, int ceiling = 3000)
    {
        double bits = (double)width * height * Math.Max(1, fps) * 0.07;   // about 0.07 bits per pixel: screens are mostly still
        return (int)Math.Clamp(bits / 1000, 400, Math.Max(400, ceiling));
    }
}
