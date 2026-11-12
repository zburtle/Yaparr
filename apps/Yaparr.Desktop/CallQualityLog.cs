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
    private readonly Func<IEnumerable<ScreenViewControl>> _watches;
    private readonly Dictionary<ScreenViewControl, (WatchFigures Figures, long At)> _watchBefore = new();
    private readonly FileLog _log;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly Dictionary<PeerId, VoiceQuality> _before = new();
    private VoiceSession? _watched;
    private long _frames, _bytes, _resent, _dropped, _at;
    private ScreenSharer? _sharerWatched;
    private int _quiet;

    public CallQualityLog(Func<VoiceSession?> voice, Func<ScreenSharer?> sharer, Func<ChatNode?> node, Func<IEnumerable<ScreenViewControl>> watches, FileLog log)
    {
        _watches = watches;
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
        // The first time a share is seen, only the starting figures are taken: it began some time before this tick, so dividing everything it has
        // done so far by the one second since "now" would read as a burst of frames when nothing was wrong.
        bool firstLook = false;
        if (sharer != _sharerWatched)
        {
            _sharerWatched = sharer;
            firstLook = sharer != null;
            (_frames, _bytes, _resent, _dropped, _at) = sharer == null ? (0, 0, 0, 0, Environment.TickCount64)
                : (sharer.Frames, sharer.Bytes, sharer.Source.Status.Retransmitted, sharer.Source.Status.Dropped, Environment.TickCount64);
            sharer?.TakeLongestGapMs();
        }
        if (sharer != null && firstLook) anything = true;
        else if (sharer != null)
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

        foreach (var watch in _watches().ToList())
        {
            if (watch.Figures() is not { } now) continue;
            anything = true;
            long tick = Environment.TickCount64;
            // The first look counts from when the watching began, so nothing is missed from the minute it started in.
            var (was, at) = _watchBefore.TryGetValue(watch, out var before) ? before : (now with { Shown = 0, FramesIn = 0, BytesIn = 0, Lost = 0, Missed = 0, Recovered = 0, Late = 0, Dropped = 0, PacketsSeen = 0, GapsNoticed = 0, BigGaps = 0, ArrivedLate = 0, RepairedAfterAsking = 0, NacksSent = 0, PacketsAsked = 0, GaveUp = 0 }, now.StartedAt);
            _watchBefore[watch] = (now, tick);
            double seconds = Math.Max(1, (tick - at) / 1000.0);
            double arriving = (now.FramesIn - was.FramesIn) / seconds, drawn = (now.Shown - was.Shown) / seconds, kbps = (now.BytesIn - was.BytesIn) * 8 / 1000.0 / seconds;
            long skipped = now.Skipped - was.Skipped, lost = now.Lost - was.Lost, late = now.Late - was.Late, dropped = now.Dropped - was.Dropped;
            // Frames lost, or too late, or dropped by the decoder; pictures drawn far fewer than arrive (or hardly any at all); a decoder that is behind; no path to the sharer.
            bool bad = !now.Attached || lost > 0 || dropped > 0 || late > 10 || now.DecodeQueue > 5 || drawn < 10 || (arriving > 0 && drawn < arriving * 0.6);
            // Written every minute while watching, with or without trouble, so that a good minute can be set beside a bad one.
            if (bad) problem = true;
            var packets = now with
            {
                PacketsSeen = now.PacketsSeen - was.PacketsSeen, GapsNoticed = now.GapsNoticed - was.GapsNoticed, BigGaps = now.BigGaps - was.BigGaps, ArrivedLate = now.ArrivedLate - was.ArrivedLate,
                RepairedAfterAsking = now.RepairedAfterAsking - was.RepairedAfterAsking, NacksSent = now.NacksSent - was.NacksSent, PacketsAsked = now.PacketsAsked - was.PacketsAsked, GaveUp = now.GaveUp - was.GaveUp,
            };
            string? path = now.Parent is { } parent ? _node()?.GetLink(parent).ToString() : null;
            _log.Write($"call quality: watching{(bad ? " (a problem)" : "")} {now.Name}{(now.PictureKind != null ? " (" + now.PictureKind + ")" : "")}: {arriving:0.0} frames/s arriving at {kbps:0} kbit/s, {drawn:0.0} pictures/s drawn, {lost} frames lost, {skipped} skipped waiting for a key frame, {late} too late, "
                       + $"{dropped} dropped by the decoder, decoder queue {now.DecodeQueue}, {now.Recovered} packets recovered in all; in the last minute {packets.DescribePackets()}; "
                       + (now.Attached ? $"attached {(now.Parent is { } p ? "under " + p.Short : "to the sharer")}{(path != null ? " (" + path + ")" : "")}" : "not attached to the sharer"));
        }
        foreach (var gone in _watchBefore.Keys.Where(k => !_watches().Contains(k)).ToList()) _watchBefore.Remove(gone);

        if (problem) _quiet = 0;
        else if (anything && ++_quiet % 10 == 0) _log.Write("call quality: no problems in the last ten minutes");
    }
}
