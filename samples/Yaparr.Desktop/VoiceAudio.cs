using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Enums;
using SoundFlow.Structs;
using Yaparr.Voice;

namespace Yaparr.Desktop;

/// <summary>Connects a voice session to the microphone and speakers.</summary>
internal sealed class VoiceAudio : IDisposable
{
    private static MiniAudioEngine? s_engine;
    private static readonly object s_gate = new();

    private static MiniAudioEngine Engine
    {
        get
        {
            lock (s_gate)
            {
                if (s_engine == null) s_engine = new MiniAudioEngine();
                s_engine.UpdateAudioDevicesInfo();
                return s_engine;
            }
        }
    }

    public static IReadOnlyList<string> InputNames() => Engine.CaptureDevices.Select(d => d.Name).ToList();
    public static IReadOnlyList<string> OutputNames() => Engine.PlaybackDevices.Select(d => d.Name).ToList();

    private static AudioFormat Format => new()
    {
        Format = SampleFormat.F32, Channels = 1, SampleRate = VoiceSession.SampleRate, Layout = AudioFormat.GetLayoutFromChannels(1),
    };

    private sealed class Speaker : SoundComponent
    {
        private readonly VoiceSession _session;
        public Speaker(AudioEngine engine, AudioFormat format, VoiceSession session) : base(engine, format) => _session = session;
        protected override void GenerateAudio(Span<float> buffer, int channels) => _session.PullPlayback(buffer);
    }

    private readonly AudioCaptureDevice _capture;
    private readonly AudioPlaybackDevice _playback;

    public VoiceAudio(VoiceSession session, string? input, string? output)
    {
        var engine = Engine;
        DeviceInfo? inInfo = engine.CaptureDevices.Where(d => d.Name == input).Select(d => (DeviceInfo?)d).FirstOrDefault();
        DeviceInfo? outInfo = engine.PlaybackDevices.Where(d => d.Name == output).Select(d => (DeviceInfo?)d).FirstOrDefault();

        _playback = engine.InitializePlaybackDevice(outInfo, Format, new MiniAudioDeviceConfig());
        _playback.MasterMixer.AddComponent(new Speaker(engine, Format, session));
        try
        {
            _capture = engine.InitializeCaptureDevice(inInfo, Format, new MiniAudioDeviceConfig());
        }
        catch
        {
            _playback.Dispose();
            throw;
        }
        _capture.OnAudioProcessed += (samples, _) => session.PushCapture(samples);
        _playback.Start();
        _capture.Start();
    }

    public void Dispose()
    {
        try { _capture.Stop(); } catch (Exception) { }
        try { _playback.Stop(); } catch (Exception) { }
        _capture.Dispose();
        _playback.Dispose();
    }
}
