using FFmpeg.AutoGen;

namespace Yaparr.Screen;

/// <summary>
/// Decodes AAC (frames with their ADTS headers, as a transport stream carries them) to 32-bit float stereo, with FFmpeg's own AAC decoder from the libraries Yaparr loads
/// into itself (see <see cref="FfmpegLibraries"/>). One mono stream is given to both sides; more than two channels are not read (the server is asked for stereo). Not thread safe.
/// </summary>
public sealed unsafe class AacDecoder : IDisposable
{
    private AVCodecContext* _context;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private bool _disposed;

    public static bool IsAvailable => FfmpegLibraries.TryLoad(out _) && ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AAC) != null;

    public AacDecoder()
    {
        if (!FfmpegLibraries.TryLoad(out var why)) throw new FileNotFoundException("FFmpeg's libraries are needed to play sound. " + why);
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null) throw new PlatformNotSupportedException("These FFmpeg libraries cannot decode AAC. Update FFmpeg (Yaparr.FFmpeg ffmpeg-11 or newer).");
        try
        {
            _context = ffmpeg.avcodec_alloc_context3(codec);
            int opened = ffmpeg.avcodec_open2(_context, codec, null);
            if (opened < 0) throw new InvalidOperationException("The AAC decoder could not be started (" + opened + ").");
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decodes one frame. The samples (left, right, left, right…) are appended to <paramref name="output"/>, which is cleared first. Returns how many sample pairs came out
    /// (1024 for a frame), 0 when the decoder gave nothing (a damaged frame).
    /// </summary>
    public int Decode(ReadOnlySpan<byte> frame, List<float> output)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AacDecoder));
        output.Clear();
        if (frame.Length == 0) return 0;
        if (ffmpeg.av_new_packet(_packet, frame.Length) < 0) return 0;
        try
        {
            frame.CopyTo(new Span<byte>(_packet->data, frame.Length));
            int sent = ffmpeg.avcodec_send_packet(_context, _packet);
            if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN)) return 0;
        }
        finally { ffmpeg.av_packet_unref(_packet); }

        int pairs = 0;
        while (true)
        {
            int result = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (result < 0) break;
            try
            {
                int samples = _frame->nb_samples, channels = _frame->ch_layout.nb_channels;
                var format = (AVSampleFormat)_frame->format;
                if (samples <= 0 || channels <= 0) continue;
                for (int i = 0; i < samples; i++)
                {
                    float left, right;
                    if (format == AVSampleFormat.AV_SAMPLE_FMT_FLTP)
                    {
                        left = ((float*)_frame->data[0])[i];
                        right = channels > 1 ? ((float*)_frame->data[1])[i] : left;
                    }
                    else if (format == AVSampleFormat.AV_SAMPLE_FMT_FLT)
                    {
                        var packed = (float*)_frame->data[0];
                        left = packed[i * channels];
                        right = channels > 1 ? packed[i * channels + 1] : left;
                    }
                    else return pairs;   // a format this does not read
                    output.Add(left);
                    output.Add(right);
                }
                pairs += samples;
            }
            finally { ffmpeg.av_frame_unref(_frame); }
        }
        return pairs;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        fixed (AVCodecContext** context = &_context) ffmpeg.avcodec_free_context(context);
        fixed (AVPacket** packet = &_packet) ffmpeg.av_packet_free(packet);
        fixed (AVFrame** frame = &_frame) ffmpeg.av_frame_free(frame);
    }
}
