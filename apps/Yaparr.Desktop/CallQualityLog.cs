using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Diagnostics;
using Yaparr.Identity;
using Yaparr.Screen;
using Yaparr.Voice;

namespace Yaparr.Desktop;

/// <summary>
/// Writes a line to the log every minute about how a call and a screen share are going: for each person heard, how many packets came, how many
/// had to be made up and how often the audio ran dry; for a share, the frame rate, the longest stall and the packets sent again. Only minutes
/// with something wrong are written (and one line every ten minutes to show it was watching), so that after a call with robotic voices or
/// dropped frames the log says when, and for whom, instead of nothing.
/// </summary>
public sealed class CallQualityLog
{
    private readonly Func<VoiceSession?> _voice;
    private readonly Func<ScreenSharer?> _sharer;
    private readonly Func<ChatNode?> _node;
    private readonly FileLog _log;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly Dictionary<PeerId, VoiceQuality> _before = new();
    private VoiceSession? _watched;
    private long _frames, _bytes, _resent, _dropped, _at;
    private ScreenSharer? _sharerWatched;
    private int _quiet;

    public CallQualityLog(Func<VoiceSession?> voice, Func<ScreenSharer?> sharer, Func<ChatNode?> node, FileLog log)
    {
        _voice = voice;
        _sharer = sharer;
        _node = node;
        _log = log;
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    private void Sample()
    {
        bool anything = false, problem = false;
        var voice = _voice();
        if (voice != _watched) { _before.Clear(); _watched = voice; }
        if (voice != null)
        {
            foreach (var peer in voice.HeardFrom)
            {
                var now = voice.Quality(peer);
                _before.TryGetValue(peer, out var was);
                _before[peer] = now;
                int received = now.Received - was.Received, lost = now.Lost - was.Lost, gaps = now.Underruns - was.Underruns;
                if (received + lost == 0) continue;
                anything = true;
                if (lost == 0 && gaps == 0) continue;
                problem = true;
                _log.Write($"call quality: voice from {peer.Short}: {received} packets in the last minute, {lost} made up ({100.0 * lost / Math.Max(1, received + lost):0.0}%), "
                           + $"{gaps} gaps, {now.BufferMs} ms held back, {(_node()?.GetLink(peer))?.ToString() ?? "path unknown"}");
            }
        }

        var sharer = _sharer();
        if (sharer != _sharerWatched) { _sharerWatched = sharer; _frames = _bytes = _resent = _dropped = 0; _at = Environment.TickCount64; }
        if (sharer != null)
        {
            anything = true;
            long now = Environment.TickCount64;
            double seconds = Math.Max(1, (now - _at) / 1000.0);
            long frames = sharer.Frames, bytes = sharer.Bytes;
            var status = sharer.Source.Status;
            double fps = (frames - _frames) / seconds, kbps = (bytes - _bytes) * 8 / 1000.0 / seconds;
            long resent = status.Retransmitted - _resent, dropped = status.Dropped - _dropped;
            long gap = sharer.TakeLongestGapMs();
            double wanted = Math.Max(1, sharer.Options.Fps);
            // More pictures than were asked for means the capture is handing over a burst (the encoder then sends many times the bitrate).
            bool bad = fps < wanted * 0.8 || fps > wanted * 1.5 || gap > 3000.0 / wanted || resent > 50 || dropped > 0;
            (_frames, _bytes, _resent, _dropped, _at) = (frames, bytes, status.Retransmitted, status.Dropped, now);
            if (bad)
            {
                problem = true;
                _log.Write($"call quality: sharing: {fps:0.0} of {wanted:0} fps, {kbps:0} kbit/s, longest wait for a frame {gap} ms, {resent} packets sent again, {dropped} dropped, "
                           + $"{status.Children.Count} watching directly, {ScreenCodecs.Describe(sharer.Codec, sharer.Encoder)}");
            }
        }

        if (problem) _quiet = 0;
        else if (anything && ++_quiet % 10 == 0) _log.Write("call quality: no problems in the last ten minutes");
    }
}
