using Concentus;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Voice;

// One soundboard sound being played: decoded a frame at a time as the speakers ask for it.
internal sealed class SoundPlayback
{
    private readonly SoundClip _clip;
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(VoiceSession.SampleRate, 1);
    private int _next;

    public SoundPlayback(PeerId by, SoundClip clip)
    {
        By = by;
        _clip = clip;
    }

    public PeerId By { get; }

    public bool Finished => _next >= _clip.Frames;

    /// <summary>Fills one 20 ms frame. False when the sound has ended.</summary>
    public bool ReadFrame(Span<float> frame)
    {
        if (Finished) return false;
        _decoder.Decode(_clip.Frame(_next++), frame, VoiceSession.FrameSamples, false);
        return true;
    }
}
