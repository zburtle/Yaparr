namespace Yaparr.Screen;

/// <summary>Parts of screen sharing that a release can switch off.</summary>
public static class ScreenFeatures
{
    /// <summary>
    /// <b>The switch for H.265 (HEVC).</b> The codec is covered by patent pools (Access Advance, Via LA and Velos Media, among others), which Yaparr has no licence
    /// from. If that ever has to stop, change this one line to <c>false</c> and make a release: nothing else needs to change.
    /// <para>
    /// With it <c>false</c>: no H.265 encoder is started (the share goes on in H.264, as when no graphics card can do it), no H.265 stream is decoded or recognised
    /// (a viewer is told that this version does not play it), the video format list does not offer it, a saved choice of it is ignored, and the Android app does not ask the
    /// phone for an H.265 decoder. It does <b>not</b> take the H.265 code out of FFmpeg's libraries, which are a separate download (Yaparr.FFmpeg): to stop shipping
    /// those too, build them without it (<c>WITH_HEVC=0</c> in that repository's build scripts) and point <c>FfmpegInstaller</c> at the new release.
    /// </para>
    /// </summary>
    private static readonly bool H265Allowed = true;

    /// <summary>
    /// Whether H.265 is on in this build. <c>YAPARR_H265=0</c> in the environment turns it off on one computer (for trying the off state, or to keep it off); nothing
    /// in the environment can turn it on when <see cref="H265Allowed"/> is off.
    /// </summary>
    public static bool H265 { get; } = H265Allowed && Environment.GetEnvironmentVariable("YAPARR_H265") != "0";

    /// <summary>Whether this build may use <paramref name="codec"/> at all (every codec but H.265 always may).</summary>
    public static bool Allows(ScreenCodec codec) => codec != ScreenCodec.H265 || H265;

    /// <summary>What to tell a person when H.265 is asked for or arrives and this build has it switched off.</summary>
    public const string H265Off = "H.265 is switched off in this version of Yaparr.";
}
