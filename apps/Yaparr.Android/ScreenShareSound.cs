using Android.Media;
using Android.Media.Projection;
using Yaparr.Voice;

namespace Yaparr.AndroidApp;

/// <summary>
/// The sound that goes with a shared screen: what the shared app (or, when the whole screen is shared, the apps) play, caught with Android's audio
/// playback capture and sent as the stream of its own that viewers play next to the picture (<see cref="StreamAudioSender"/>, the same as the
/// computer's). Yaparr's own sound, which includes the voice chat, is left out so that it does not go round. Needs Android 10, and the
/// microphone permission (which is what Android calls the permission for capturing audio).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
internal sealed class ScreenShareSound : IDisposable
{
    private const int SampleRate = 48_000;

    private readonly AudioRecord _record;
    private readonly StreamAudioSender _sender;
    private readonly Thread _thread;
    private volatile bool _stopped;
    private long _samples;

    /// <summary>The sound stopped on its own (Android took the audio away, say). The picture goes on. Raised on another thread.</summary>
    public event Action<string>? Failed;

    public ScreenShareSound(MediaProjection projection, StreamAudioSender sender)
    {
        _sender = sender;
        var configBuilder = new AudioPlaybackCaptureConfiguration.Builder(projection);
        configBuilder.AddMatchingUsage(AudioUsageKind.Media);
        configBuilder.AddMatchingUsage(AudioUsageKind.Game);
        configBuilder.AddMatchingUsage(AudioUsageKind.Unknown);
        configBuilder.ExcludeUid(Android.OS.Process.MyUid());
        var config = configBuilder.Build()!;
        var formatBuilder = new AudioFormat.Builder();
        formatBuilder.SetEncoding(Encoding.Pcm16bit);
        formatBuilder.SetSampleRate(SampleRate);
        formatBuilder.SetChannelMask(ChannelOut.Stereo);
        var format = formatBuilder.Build()!;
        int minimum = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Stereo, Encoding.Pcm16bit);
        var recordBuilder = new AudioRecord.Builder();
        recordBuilder.SetAudioPlaybackCaptureConfig(config);
        recordBuilder.SetAudioFormat(format);
        recordBuilder.SetBufferSizeInBytes(Math.Max(minimum, SampleRate / 5 * 4));   // at least a fifth of a second
        _record = recordBuilder.Build()!;
        if (_record.State != State.Initialized)
        {
            _record.Release();
            throw new InvalidOperationException("Android would not let the sound be captured.");
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "screen share sound" };
    }

    public void Start()
    {
        _record.StartRecording();
        _thread.Start();
    }

    /// <summary>How many samples of each channel have been sent, for the diagnostics.</summary>
    public long Samples => Interlocked.Read(ref _samples);

    private void Run()
    {
        // 20 ms of stereo, as the encoder takes it.
        var pcm = new short[StreamAudioSender.FrameSamples * StreamAudioSender.Channels];
        var floats = new float[pcm.Length];
        try
        {
            while (!_stopped)
            {
                int read = _record.Read(pcm, 0, pcm.Length);
                if (read < 0) { if (!_stopped) Failed?.Invoke("Android stopped the sound: error " + read); return; }
                if (read == 0) continue;
                for (int i = 0; i < read; i++) floats[i] = pcm[i] / 32768f;
                _sender.Push(floats.AsSpan(0, read));
                Interlocked.Add(ref _samples, read / 2);
            }
        }
        catch (Exception ex) when (!_stopped)
        {
            Failed?.Invoke("The sound stopped: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _stopped = true;
        try { _record.Stop(); } catch (Exception ex) when (ex is Java.Lang.IllegalStateException) { }
        try { if (_thread.IsAlive) _thread.Join(500); } catch (ThreadStateException) { }
        _record.Release();
        _sender.Dispose();
    }
}
