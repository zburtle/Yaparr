namespace Yaparr.Voice;

/// <summary>
/// Cleans up your microphone before it is sent: echo cancellation, noise suppression and the like. The library has no
/// signal processing of its own; an app hands one to <see cref="VoiceSession.Processor"/>. Everything is mono, 48 kHz,
/// samples from -1 to 1. The two methods are called from different threads (the microphone's and the speakers').
/// </summary>
public interface IVoiceProcessor : IDisposable
{
    /// <summary>Cleans one microphone frame (<see cref="VoiceSession.FrameSamples"/> samples, 20 ms) in place.</summary>
    void ProcessCapture(Span<float> frame);

    /// <summary>
    /// Says what has just been given to the speakers, in blocks of any size. Echo cancellation needs this to
    /// recognise your own speakers in the microphone.
    /// </summary>
    void ProcessPlayback(ReadOnlySpan<float> played);
}
