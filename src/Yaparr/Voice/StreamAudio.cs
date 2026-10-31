using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
using Yaparr.Chat;

namespace Yaparr.Voice;

/// <summary>
/// The sound that goes with a shared screen, sent as a stream of its own (stereo, 48 kHz, Opus, 20 ms to a packet). It is a
/// separate stream so that a lost picture never holds the sound back: the viewer hears everything that arrives.
/// </summary>
public sealed class StreamAudioSender : IDisposable
{
    public const int Channels = 2;
    public const int FrameSamples = RemoteVoice.FrameSamples;

    private readonly MediaSource _source;
    private readonly IOpusEncoder _encoder;
    private readonly float[] _frame = new float[FrameSamples * Channels];
    private readonly byte[] _packet = new byte[2 + 1275];
    private int _fill;
    private ushort _seq;

    public StreamAudioSender(MediaSource source, int kbps = 96)
    {
        _source = source;
        _encoder = OpusCodecFactory.CreateEncoder(VoiceSession.SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = kbps * 1000;
        _encoder.Complexity = 5;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 5;
    }

    public long Packets { get; private set; }

    /// <summary>Gives the sender sound to send: interleaved left and right samples (-1 to 1), in chunks of any size.</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        while (!samples.IsEmpty)
        {
            int n = Math.Min(samples.Length, _frame.Length - _fill);
            samples[..n].CopyTo(_frame.AsSpan(_fill));
            _fill += n;
            samples = samples[n..];
            if (_fill < _frame.Length) continue;
            _fill = 0;
            int length = _encoder.Encode(_frame, FrameSamples, _packet.AsSpan(2), _packet.Length - 2);
            BinaryPrimitives.WriteUInt16BigEndian(_packet, _seq++);
            _source.Send(_packet.AsSpan(0, 2 + length));
            Packets++;
        }
    }

    public void Dispose() { }
}

/// <summary>Plays the sound of a screen someone shares: puts the packets in order, covers for lost ones and holds a little back to ride out jitter.</summary>
public sealed class StreamAudioReceiver : IDisposable
{
    private const int FrameValues = StreamAudioSender.FrameSamples * StreamAudioSender.Channels;

    private readonly MediaView _view;
    private readonly RemoteVoice _remote;
    private readonly float[] _tmp = new float[FrameValues];
    private readonly float[] _ring = new float[FrameValues * 2];
    private int _ringStart, _ringCount;
    private readonly object _lock = new();
    private volatile float _gain = 1f;
    private volatile bool _disposed;

    /// <param name="delayMs">How far behind the picture the sound is held back, so the two stay together.</param>
    public StreamAudioReceiver(MediaView view, int delayMs = 160)
    {
        _view = view;
        _remote = new RemoteVoice(view.Info.Root, StreamAudioSender.Channels, Math.Clamp(delayMs / 20, 2, 25));
        view.Received += OnPacket;
    }

    /// <summary>How loud it is, 0 to 2 (1 is as sent).</summary>
    public float Gain { get => _gain; set => _gain = Math.Clamp(value, 0f, 2f); }

    public int Received => _remote.Received;
    public int Lost => _remote.Lost;
    public int Underruns => _remote.Underruns;

    private void OnPacket(MediaPacket packet)
    {
        var span = packet.Payload.Span;
        if (_disposed || span.Length <= 2) return; // the tiny packets that follow a pause carry no sound
        _remote.Enqueue(BinaryPrimitives.ReadUInt16BigEndian(span), span[2..]);
    }

    /// <summary>Fills <paramref name="output"/> with interleaved stereo samples (48 kHz). Silence when nothing is arriving.</summary>
    public void Pull(Span<float> output)
    {
        float gain = _gain;
        lock (_lock)
        {
            while (!output.IsEmpty)
            {
                if (_ringCount == 0)
                {
                    if (!_remote.ReadFrame(_tmp, Environment.TickCount64)) Array.Clear(_tmp);
                    for (int i = 0; i < FrameValues; i++)
                    {
                        float s = _tmp[i] * gain;
                        _ring[i] = s > 1f ? 1f : s < -1f ? -1f : s;
                    }
                    _ringStart = 0;
                    _ringCount = FrameValues;
                }
                int n = Math.Min(output.Length, _ringCount);
                _ring.AsSpan(_ringStart, n).CopyTo(output);
                _ringStart += n;
                _ringCount -= n;
                output = output[n..];
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Received -= OnPacket;
        _remote.Dispose();
    }
}
