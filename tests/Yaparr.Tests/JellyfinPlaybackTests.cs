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

    [Fact]
    public void A_status_survives_the_trip_and_a_stray_packet_is_not_taken_for_one()
    {
        var status = new FilmStatus("Severance: Hello, Ms. Cobel — “first”", TimeSpan.FromSeconds(754.5), TimeSpan.FromMinutes(52), Paused: true);
        Assert.True(FilmStatus.TryDecode(status.Encode(), out var back));
        Assert.Equal(status, back);
        Assert.False(FilmStatus.TryDecode(new byte[] { 0 }, out _));            // the one-byte packet a stream sends after its last frame
        Assert.False(FilmStatus.TryDecode(new byte[40], out _));                // another version
        // A title that is too long is cut between characters.
        var tooLong = new FilmStatus(new string('é', 300), TimeSpan.Zero, TimeSpan.Zero, false);
        Assert.True(FilmStatus.TryDecode(tooLong.Encode(), out var cut));
        Assert.All(cut.Title, c => Assert.Equal('é', c));
        Assert.True(cut.Title.Length is > 50 and <= 80);
    }

    [Fact]
    public void The_next_episode_gets_the_sound_and_subtitles_most_like_the_last_ones()
    {
        var english = new JellyfinTrack(1, "English / DD+ 5.1", "eng", "eac3", true, false);
        var commentary = new JellyfinTrack(2, "Commentary", "eng", "ac3", false, false);
        var nextEnglish = new JellyfinTrack(7, "English 5.1", "eng", "eac3", true, false);
        var nextCommentary = new JellyfinTrack(8, "Director commentary", "eng", "ac3", false, false);
        var nextFrench = new JellyfinTrack(9, "French", "fra", "eac3", false, false);
        var next = new[] { nextFrench, nextCommentary, nextEnglish };
        Assert.Equal(7, JellyfinClient.TrackLike(next, english)?.Index);
        Assert.Equal(8, JellyfinClient.TrackLike(next, commentary)?.Index);          // a commentary stays one
        Assert.Null(JellyfinClient.TrackLike(new[] { nextFrench }, english));       // no such language: the server's own choice
        Assert.Null(JellyfinClient.TrackLike(next, null));
    }

    // Needs a transport stream made by a Jellyfin server (tools/Yaparr.JellyfinSpike --dump), named in YAPARR_FILM_TS, and FFmpeg's libraries with the AAC decoder.
    // Plays it into a room of two and checks that the pictures and the sound go out at the speed of the film and reach the viewer; that pausing and a jump work; that the next
    // episode starts on the same stream; and that the viewer is told what is playing and how far it is.
    [Fact]
    public async Task A_recorded_film_goes_out_at_its_own_speed_and_can_be_paused_jumped_in_and_followed_by_the_next()
    {
        var file = Environment.GetEnvironmentVariable("YAPARR_FILM_TS");
        if (file == null || !File.Exists(file) || !AacDecoder.IsAvailable) return;

        var opened = new List<(string Id, TimeSpan Start)>();
        var first = new FilmTarget("e1", "Show: One", TimeSpan.FromMinutes(40));
        var second = new FilmTarget("e2", "Show: Two", TimeSpan.FromMinutes(41));
        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var sharer = JellyfinSharer.Start(c.Root, c.Rooms[0], "Show: One", 3000, first, TimeSpan.Zero,
            (target, start, _) => { lock (opened) opened.Add((target.Id, start)); return Task.FromResult<(Stream, IAsyncDisposable)>((File.OpenRead(file), new Nothing())); },
            (current, _) => Task.FromResult<FilmTarget?>(current.Id == "e1" ? second : null));
        var views = await c.WatchAllAsync(sharer.Source.Info.Id);
        var statusGroup = c.Rooms[0].MediaGroups.First(g => g.Kind == FilmStatus.Kind).Id;
        var statusViews = await c.WatchAllAsync(statusGroup);
        try
        {
            int pictures = 0;
            views[0].FrameReceived += _ => Interlocked.Increment(ref pictures);
            var statuses = new List<FilmStatus>();
            statusViews[0].Received += p => { if (FilmStatus.TryDecode(p.Payload.Span, out var s)) lock (statuses) statuses.Add(s); };
            var started = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(8));
            double seconds = started.Elapsed.TotalSeconds;

            // The file holds about 25 s and goes out at its own speed, 24 pictures and 46.9 sound frames a second (the first half second is the lead).
            double expectedPictures = (seconds + 0.5 + 1.0) * 24, expectedSound = (seconds + 0.5 + 1.0) * 46.875;   // the viewer joined a second after the start
            Assert.InRange(sharer.Pictures, expectedPictures * 0.8 - 40, expectedPictures * 1.1 + 40);
            Assert.InRange(sharer.SoundFrames, expectedSound * 0.8 - 40, expectedSound * 1.1 + 40);
            Assert.True(pictures > 100, $"the viewer got {pictures} pictures");
            Assert.True(sharer.Position.TotalSeconds is > 6 and < 11, $"the film is at {sharer.Position}");
            await TestUtil.WaitFor(() => { lock (statuses) return statuses.Any(s => s.Title == "Show: One" && s.Position > TimeSpan.FromSeconds(3) && s.Duration == first.Duration); }, 5000);

            // Paused, it stops and the viewer is told; resumed, it goes on from there.
            sharer.Pause();
            await Task.Delay(500);
            long during = sharer.Pictures;
            await Task.Delay(1500);
            Assert.True(sharer.Pictures - during <= 30, "pictures kept going while paused");
            Assert.True(sharer.Paused);
            await TestUtil.WaitFor(() => { lock (statuses) return statuses.Count > 0 && statuses[^1].Paused; }, 5000);
            sharer.Resume();
            await Task.Delay(1000);
            Assert.True(sharer.Pictures - during > 10, "it did not go on after being resumed");

            // A jump starts a new transcode at that time, and the film goes on from there.
            long beforeJump = sharer.Pictures;
            sharer.Seek(TimeSpan.FromMinutes(20));
            await TestUtil.WaitFor(() => { lock (opened) return opened.Any(o => o.Start == TimeSpan.FromMinutes(20)); }, 10_000);
            await Task.Delay(3000);
            Assert.InRange(sharer.Position.TotalSeconds, 20 * 60 + 1, 20 * 60 + 8);
            Assert.True(sharer.Pictures - beforeJump > 30, "no pictures after the jump");
            Assert.Equal("Show: One", sharer.Target.Title);

            // The next episode starts on the same stream, at its beginning, and is announced.
            FilmTarget? changed = null;
            sharer.TargetChanged += t => changed = t;
            sharer.Next();
            await TestUtil.WaitFor(() => changed != null, 10_000);
            Assert.Equal("e2", changed!.Id);
            await TestUtil.WaitFor(() => { lock (opened) return opened.Any(o => o.Id == "e2" && o.Start == TimeSpan.Zero); }, 10_000);
            await Task.Delay(2500);
            Assert.True(sharer.Position.TotalSeconds < 6, $"the next episode is at {sharer.Position}");
            await TestUtil.WaitFor(() => { lock (statuses) return statuses.Any(s => s.Title == "Show: Two"); }, 5000);
            Assert.Equal(sharer.Source.Info.Id, c.Rooms[0].MediaGroups.First(g => g.Kind == "screen").Id);   // still the one share
        }
        finally
        {
            foreach (var v in views) v.Dispose();
            foreach (var v in statusViews) v.Dispose();
        }
    }
}
