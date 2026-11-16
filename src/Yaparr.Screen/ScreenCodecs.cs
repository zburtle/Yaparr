namespace Yaparr.Screen;

/// <summary>
/// The frames of a share do not say which codec they are in; a viewer reads it off the key frame. VP8 key frames carry the
/// code 9D 01 2A at byte 3, an H.264 frame starts with a 00 00 01 start code, and an AV1 unit starts with a temporal delimiter (12 00) and holds a
/// sequence header, so they cannot be mistaken for each other. H.265 also starts with start codes, but its NAL units have two byte headers and are told by their types
/// (video, sequence and picture parameter sets and an IRAP picture), which an H.264 frame cannot be read as; it is only recognised where
/// <see cref="ScreenFeatures.H265"/> is on.
/// </summary>
public static class ScreenCodecs
{
    /// <summary>The codec and picture size of a key frame; false for anything else.</summary>
    public static bool TryGetKeyFrame(ReadOnlySpan<byte> frame, out ScreenCodec codec, out int width, out int height) =>
        TryGetKeyFrame(frame, ScreenFeatures.H265, out codec, out width, out height);

    /// <summary>As above, with whether H.265 is on given (for tests of the switch).</summary>
    internal static bool TryGetKeyFrame(ReadOnlySpan<byte> frame, bool h265, out ScreenCodec codec, out int width, out int height)
    {
        if (Av1.LooksLikeTemporalUnit(frame) && Av1.TryGetSize(frame, out width, out height)) { codec = ScreenCodec.Av1; return true; }
        if (Vp8.TryGetSize(frame, out width, out height)) { codec = ScreenCodec.Vp8; return true; }
        if (h265 && H265.IsKeyFrame(frame) && H265.TryGetSize(frame, out width, out height)) { codec = ScreenCodec.H265; return true; }
        if (H264.IsKeyFrame(frame) && H264.TryGetSize(frame, out width, out height)) { codec = ScreenCodec.H264; return true; }
        codec = ScreenCodec.Auto;
        width = height = 0;
        return false;
    }

    public static bool IsKeyFrame(ScreenCodec codec, ReadOnlySpan<byte> frame) =>
        codec switch { ScreenCodec.H264 => H264.IsKeyFrame(frame), ScreenCodec.H265 => ScreenFeatures.H265 && H265.IsKeyFrame(frame), ScreenCodec.Av1 => Av1.IsKeyFrame(frame), _ => Vp8.IsKeyFrame(frame) };

    /// <summary>For messages: the codec and, for the hardware ones, whose graphics card makes it.</summary>
    public static string Describe(ScreenCodec codec, HardwareEncoder? encoder) =>
        codec is ScreenCodec.H264 or ScreenCodec.Av1 or ScreenCodec.H265 && encoder is { } card ? $"{Name(codec)} on the {HardwareEncoders.Maker(card)} graphics card" : Describe(codec);

    /// <summary>The codec's name as people write it.</summary>
    public static string Name(ScreenCodec codec) => codec switch { ScreenCodec.Av1 => "AV1", ScreenCodec.H265 => "H.265", ScreenCodec.Vp8 => "VP8", _ => "H.264" };

    public static string Describe(ScreenCodec codec) => codec switch
    {
        ScreenCodec.H264 => "H.264 on the graphics card",
        ScreenCodec.Av1 => "AV1 on the graphics card",
        ScreenCodec.H265 => "H.265 on the graphics card",
        ScreenCodec.Vp8 => "VP8 in software",
        _ => "automatic",
    };
}
