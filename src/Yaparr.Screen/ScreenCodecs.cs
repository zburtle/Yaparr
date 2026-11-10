namespace Yaparr.Screen;

/// <summary>
/// The frames of a share do not say which codec they are in; a viewer reads it off the key frame. VP8 key frames carry the
/// code 9D 01 2A at byte 3, an H.264 frame starts with a 00 00 01 start code, and an AV1 unit starts with a temporal delimiter (12 00) and holds a
/// sequence header, so they cannot be mistaken for each other.
/// </summary>
public static class ScreenCodecs
{
    /// <summary>The codec and picture size of a key frame; false for anything else.</summary>
    public static bool TryGetKeyFrame(ReadOnlySpan<byte> frame, out ScreenCodec codec, out int width, out int height)
    {
        if (Av1.LooksLikeTemporalUnit(frame) && Av1.TryGetSize(frame, out width, out height)) { codec = ScreenCodec.Av1; return true; }
        if (Vp8.TryGetSize(frame, out width, out height)) { codec = ScreenCodec.Vp8; return true; }
        if (H264.IsKeyFrame(frame) && H264.TryGetSize(frame, out width, out height)) { codec = ScreenCodec.H264; return true; }
        codec = ScreenCodec.Auto;
        width = height = 0;
        return false;
    }

    public static bool IsKeyFrame(ScreenCodec codec, ReadOnlySpan<byte> frame) =>
        codec switch { ScreenCodec.H264 => H264.IsKeyFrame(frame), ScreenCodec.Av1 => Av1.IsKeyFrame(frame), _ => Vp8.IsKeyFrame(frame) };

    /// <summary>For messages: the codec and, for the hardware ones, whose graphics card makes it.</summary>
    public static string Describe(ScreenCodec codec, HardwareEncoder? encoder) =>
        codec is ScreenCodec.H264 or ScreenCodec.Av1 && encoder is { } card ? $"{(codec == ScreenCodec.Av1 ? "AV1" : "H.264")} on the {HardwareEncoders.Maker(card)} graphics card" : Describe(codec);

    public static string Describe(ScreenCodec codec) => codec switch
    {
        ScreenCodec.H264 => "H.264 on the graphics card",
        ScreenCodec.Av1 => "AV1 on the graphics card",
        ScreenCodec.Vp8 => "VP8 in software",
        _ => "automatic",
    };
}
