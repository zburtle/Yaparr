using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Tests;

public class JellyfinPlaybackTests
{
    // ---- the request ----

    [Fact]
    public void The_transcode_is_asked_for_without_b_frames_and_with_a_play_session_of_its_own()
    {
        var path = JellyfinClient.StreamPath(new JellyfinStreamRequest("item1", "src1", "play-1", VideoKbps: 3000, MaxHeight: 720, AudioIndex: 2), "dev");
        Assert.StartsWith("Videos/item1/stream.ts?", path);
        foreach (var part in new[] { "mediaSourceId=src1", "deviceId=dev", "playSessionId=play-1", "videoCodec=h264", "audioCodec=aac", "maxRefFrames=1", "videoBitRate=3000000",
                     "maxHeight=720", "audioSampleRate=48000", "allowVideoStreamCopy=false", "audioStreamIndex=2" })
            Assert.Contains(part, path);
        Assert.DoesNotContain("subtitle", path);
        Assert.DoesNotContain("startTimeTicks", path);
    }

    [Fact]
    public void A_start_time_and_subtitles_burned_in_do_not_go_together_but_either_does()
    {
        Assert.Contains("startTimeTicks=6000000000", JellyfinClient.StreamPath(new JellyfinStreamRequest("i", "s", "p", StartTicks: 6_000_000_000), "d"));
        var burned = JellyfinClient.StreamPath(new JellyfinStreamRequest("i", "s", "p", SubtitleIndex: 4), "d");
        Assert.Contains("subtitleStreamIndex=4", burned);
        Assert.Contains("subtitleMethod=Encode", burned);
        Assert.Throws<ArgumentException>(() => JellyfinClient.StreamPath(new JellyfinStreamRequest("i", "s", "p", SubtitleIndex: 4, StartTicks: 6_000_000_000), "d"));
    }

    // ---- taking a transport stream apart ----

    // A transport stream made by hand: one program with a video stream (pid 256) and a sound stream (pid 257).
    private sealed class TsWriter
    {
        public readonly MemoryStream Out = new();
        private readonly Dictionary<int, int> _counters = new();

        private void Packet(int pid, bool start, ReadOnlySpan<byte> payload)
        {
            var p = new byte[188];
            Array.Fill(p, (byte)0xFF);
            p[0] = 0x47;
            p[1] = (byte)((start ? 0x40 : 0) | (pid >> 8));
            p[2] = (byte)pid;
            int counter = _counters.GetValueOrDefault(pid);
            _counters[pid] = counter + 1;
            p[3] = (byte)(0x10 | (counter & 0xF));
            payload.CopyTo(p.AsSpan(4));
            Out.Write(p);
        }

        public void Tables()
        {
            // PAT: program 1 -> pid 4096
            var pat = new byte[] { 0, 0x00, 0xB0, 13, 0, 1, 0xC1, 0, 0, 0, 1, 0xF0, 0x00, 0, 0, 0, 0 };
            Packet(0, true, pat);
            // PMT: video (0x1B) on 256, AAC (0x0F) on 257
            var pmt = new byte[] { 0, 0x02, 0xB0, 23, 0, 1, 0xC1, 0, 0, 0xE1, 0x00, 0xF0, 0x00, 0x1B, 0xE1, 0x00, 0xF0, 0x00, 0x0F, 0xE1, 0x01, 0xF0, 0x00, 0, 0, 0, 0 };
            Packet(4096, true, pmt);
        }

        public void Pes(int pid, long pts, byte[] data)
        {
            var header = new List<byte> { 0, 0, 1, (byte)(pid == 256 ? 0xE0 : 0xC0), 0, 0, 0x80, 0x80, 5 };
            header.Add((byte)(0x21 | ((pts >> 30) & 7) << 1));
            header.Add((byte)(pts >> 22));
            header.Add((byte)(((pts >> 15) & 0x7F) << 1 | 1));
            header.Add((byte)(pts >> 7));
            header.Add((byte)((pts & 0x7F) << 1 | 1));
            var all = header.Concat(data).ToArray();
            for (int i = 0; i < all.Length; i += 184)
            {
                var chunk = all.AsSpan(i, Math.Min(184, all.Length - i));
                if (chunk.Length == 184) Packet(pid, i == 0, chunk);
                else
                {
                    // The last piece is padded with an adaptation field of stuffing.
                    var p = new byte[188];
                    p[0] = 0x47;
                    p[1] = (byte)((i == 0 ? 0x40 : 0) | (pid >> 8));
                    p[2] = (byte)pid;
                    int counter = _counters.GetValueOrDefault(pid);
                    _counters[pid] = counter + 1;
                    p[3] = (byte)(0x30 | (counter & 0xF));
                    int stuffing = 184 - chunk.Length;
                    p[4] = (byte)(stuffing - 1);
                    if (stuffing > 1) { p[5] = 0; for (int s = 6; s < 4 + stuffing; s++) p[s] = 0xFF; }
                    chunk.CopyTo(p.AsSpan(4 + stuffing));
                    Out.Write(p);
                }
            }
        }
    }

    private static byte[] AdtsFrame(int bodyLength)
    {
        int length = 7 + bodyLength;
        var f = new byte[length];
        f[0] = 0xFF; f[1] = 0xF1;
        f[2] = (byte)((1 << 6) | (3 << 2));            // AAC LC, 48 kHz
        f[3] = (byte)((2 << 6) | ((length >> 11) & 3)); // two channels
        f[4] = (byte)((length >> 3) & 0xFF);
        f[5] = (byte)(((length & 7) << 5) | 0x1F);
        f[6] = 0xFC;
        return f;
    }

    [Fact]
    public void A_transport_stream_gives_its_pictures_and_frames_of_sound_with_their_time_stamps()
    {
        var w = new TsWriter();
        w.Tables();
        var picture0 = new byte[] { 0, 0, 0, 1, 9, 0xF0, 0, 0, 0, 1, 0x65, 1, 2, 3 };
        var picture1 = new byte[] { 0, 0, 0, 1, 9, 0x30, 0, 0, 0, 1, 0x41, 4, 5, 6 };
        var bigPicture = new byte[] { 0, 0, 0, 1, 9, 0x30, 0, 0, 0, 1, 0x41 }.Concat(Enumerable.Repeat((byte)7, 700)).ToArray();   // spans several packets
        // Two frames of sound in one packet.
        var twoFrames = AdtsFrame(20).Concat(AdtsFrame(30)).ToArray();
        w.Pes(256, 90000, picture0);
        w.Pes(257, 90000 - 3870, twoFrames);
        w.Pes(256, 90000 + 3750, picture1);
        w.Pes(256, 90000 + 7500, bigPicture);
        w.Pes(257, 90000 + 7500, AdtsFrame(10));

        var units = new List<TsUnit>();
        var demuxer = new TsDemuxer(units.Add);
        // Fed in odd pieces, as a network gives it.
        var bytes = w.Out.ToArray();
        for (int i = 0; i < bytes.Length; i += 100) demuxer.Feed(bytes.AsSpan(i, Math.Min(100, bytes.Length - i)));
        demuxer.Finish();

        var video = units.Where(u => u.IsVideo).ToList();
        var sound = units.Where(u => !u.IsVideo).ToList();
        Assert.Equal(new long[] { 90000, 93750, 97500 }, video.Select(u => u.Pts));
        Assert.Equal(picture0, video[0].Data);
        Assert.Equal(bigPicture, video[2].Data);
        // The sound packet holds two frames of 1024 samples each, a frame later by 1024 / 48000 s = 1920 ticks.
        Assert.Equal(new long[] { 90000 - 3870, 90000 - 3870 + 1920, 97500 }, sound.Select(u => u.Pts));
        Assert.Equal(new[] { 27, 37, 17 }, sound.Select(u => u.Data.Length));
        Assert.Equal(0, demuxer.SyncLosses);
    }

    [Fact]
    public void A_stream_that_starts_in_the_middle_of_a_packet_finds_its_way()
    {
        var w = new TsWriter();
        w.Tables();
        w.Pes(256, 90000, new byte[] { 0, 0, 0, 1, 9, 0xF0, 0, 0, 0, 1, 0x65, 1, 2, 3 });
        w.Pes(256, 93750, new byte[] { 0, 0, 0, 1, 9, 0x30, 0, 0, 0, 1, 0x41, 4 });
        var bytes = new byte[] { 1, 2, 3, 4, 5 }.Concat(w.Out.ToArray()).ToArray();
        var units = new List<TsUnit>();
        var demuxer = new TsDemuxer(units.Add);
        demuxer.Feed(bytes);
        demuxer.Finish();
        Assert.Equal(2, units.Count);
        Assert.True(demuxer.SyncLosses >= 1);
    }

    // ---- playing a real recording, when there is one ----

    private sealed class Nothing : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Needs a transport stream made by a Jellyfin server (tools/Yaparr.JellyfinSpike --dump), named in YAPARR_FILM_TS, and FFmpeg's libraries with the AAC decoder.
    // Plays 10 seconds of it into a room of two and checks that the pictures and the sound go out at the speed of the film, and reach the viewer.
    [Fact]
    public async Task A_recorded_film_goes_out_at_its_own_speed_and_reaches_a_viewer()
    {
        var file = Environment.GetEnvironmentVariable("YAPARR_FILM_TS");
        if (file == null || !File.Exists(file) || !AacDecoder.IsAvailable) return;

        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var sharer = JellyfinSharer.Start(c.Root, c.Rooms[0], "film", 3000, _ => Task.FromResult<(Stream, IAsyncDisposable)>((File.OpenRead(file), new Nothing())));
        var views = await c.WatchAllAsync(sharer.Source.Info.Id);
        try
        {
            int pictures = 0;
            views[0].FrameReceived += _ => Interlocked.Increment(ref pictures);
            var started = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(10));
            double seconds = started.Elapsed.TotalSeconds;

            // The file holds about 25 s and goes out at its own speed, 24 pictures and 46.9 sound frames a second (the first half second is the lead).
            double expectedPictures = (seconds + 0.5 + 1.0) * 24, expectedSound = (seconds + 0.5 + 1.0) * 46.875;   // the viewer joined a second after the start
            Assert.InRange(sharer.Pictures, expectedPictures * 0.8 - 40, expectedPictures * 1.1 + 40);
            Assert.InRange(sharer.SoundFrames, expectedSound * 0.8 - 40, expectedSound * 1.1 + 40);
            Assert.True(pictures > 120, $"the viewer got {pictures} pictures");
            Assert.True(sharer.Position.TotalSeconds is > 8 and < 13, $"the film is at {sharer.Position}");

            // Paused, it stops; resumed, it goes on from there.
            sharer.Pause();
            await Task.Delay(500);
            long during = sharer.Pictures;
            await Task.Delay(1500);
            Assert.True(sharer.Pictures - during <= 30, "pictures kept going while paused");
            Assert.True(sharer.Paused);
            sharer.Resume();
            await Task.Delay(1000);
            Assert.True(sharer.Pictures - during > 10, "it did not go on after being resumed");
        }
        finally { foreach (var v in views) v.Dispose(); }
    }
}
