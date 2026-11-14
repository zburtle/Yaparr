using Yaparr.Screen;

namespace Yaparr.Tests;

public class InProcessEncoderTests
{
    [Fact]
    public void No_key_frame_is_forced_until_a_viewer_asks()
    {
        var pacer = new KeyFramePacer(500);
        Assert.False(pacer.ShouldForce(10_000));
        Assert.Equal(0, pacer.Forced);
    }

    [Fact]
    public void A_request_forces_one_key_frame_on_the_next_picture()
    {
        var pacer = new KeyFramePacer(500);
        pacer.Request();
        Assert.True(pacer.ShouldForce(10_000));
        Assert.False(pacer.ShouldForce(10_033));   // answered: not again for the same request
        Assert.Equal(1, pacer.Forced);
    }

    [Fact]
    public void A_request_soon_after_a_key_frame_is_kept_and_answered_when_the_gap_has_passed()
    {
        var pacer = new KeyFramePacer(500);
        pacer.KeySent(10_000);
        pacer.Request();
        Assert.False(pacer.ShouldForce(10_200));
        Assert.False(pacer.ShouldForce(10_499));
        Assert.True(pacer.ShouldForce(10_500));
        Assert.Equal(1, pacer.Forced);
    }

    [Fact]
    public void A_key_frame_that_goes_out_anyway_answers_what_is_waiting()
    {
        var pacer = new KeyFramePacer(500);
        pacer.KeySent(10_000);
        pacer.Request();
        pacer.KeySent(10_300);   // the regular key frame came first
        Assert.False(pacer.ShouldForce(11_000));
        Assert.Equal(0, pacer.Forced);
    }

    [Fact]
    public void Libraries_are_found_in_an_ffmpeg_folder_next_to_the_app_or_the_one_that_is_named()
    {
        string root = Path.Combine(Path.GetTempPath(), "yaparr-libs-" + Guid.NewGuid().ToString("N"));
        try
        {
            string app = Path.Combine(root, "app"), named = Path.Combine(root, "named");
            Directory.CreateDirectory(Path.Combine(app, "ffmpeg"));
            Directory.CreateDirectory(named);
            Assert.Null(FfmpegLibraries.Find(app, explicitFolder: Path.Combine(root, "nothing")));

            string library = OperatingSystem.IsWindows() ? "avcodec-63.dll" : "libavcodec.so.63";
            File.WriteAllText(Path.Combine(app, "ffmpeg", library), "");
            Assert.Equal(Path.Combine(app, "ffmpeg"), FfmpegLibraries.Find(app, explicitFolder: Path.Combine(root, "nothing")));

            File.WriteAllText(Path.Combine(named, library), "");
            Assert.Equal(named, FfmpegLibraries.Find(app, explicitFolder: named));   // the named folder wins
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    // A picture of moving bars with a little noise, NV12.
    private static byte[] Picture(int width, int height, int n)
    {
        var picture = new byte[width * height * 3 / 2];
        var random = new Random(n);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) picture[y * width + x] = (byte)Math.Min(235, 60 + ((x + y + n * 3) & 127) + ((((x + n * 7) >> 5) & 1) * 40) + random.Next(8));
        Array.Fill(picture, (byte)128, width * height, width * height / 2);
        return picture;
    }

    /// <summary>
    /// Runs the real NVIDIA encoder through FFmpeg's libraries. It does nothing where the libraries (set YAPARR_FFMPEG_LIBS) or an NVIDIA
    /// card are missing, which is the case on the build server.
    /// </summary>
    [Fact]
    public void The_real_encoder_makes_key_frames_a_viewer_can_start_from_and_makes_one_on_the_very_next_picture_when_asked()
    {
        if (!FfmpegLibraries.IsAvailable) return;
        const int w = 1280, h = 720;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(w, h, new ScreenOptions { Fps = 30, Kbps = 3000, KeyFrameSeconds = 2 }); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return; }   // no NVIDIA card here

        using (encoder)
        {
            var packets = new List<(int Picture, byte[] Frame, bool Key)>();
            for (int n = 0; n < 120; n++)
            {
                int picture = n;
                bool force = n is 25 or 26 or 70;
                int before = packets.Count;
                encoder.Encode(Picture(w, h, n), force, (frame, key) => packets.Add((picture, frame, key)));
                Assert.Equal(before + 1, packets.Count);   // no delay: one packet per picture, at once
            }

            // Every key frame holds the parameter sets, which is what a viewer that joins needs.
            foreach (var (_, frame, key) in packets.Where(p => p.Key)) Assert.True(ScreenCodecs.IsKeyFrame(ScreenCodec.H264, frame));
            Assert.True(packets[0].Key);
            // A key frame was asked for at 25, 26 and 70; each came out on that very picture.
            foreach (int asked in new[] { 25, 26, 70 }) Assert.True(packets[asked].Key, $"picture {asked} should be a key frame");
            // And the regular one is not at 60 any more: asking restarts the interval (60 pictures from 70 is 130).
            Assert.False(packets[60].Key);
            Assert.False(packets[100].Key);
        }
    }

    [Fact]
    public void An_encoder_refuses_a_picture_of_the_wrong_size()
    {
        if (!FfmpegLibraries.IsAvailable) return;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(640, 360, new ScreenOptions { Fps = 30, Kbps = 1000 }); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return; }
        using (encoder) Assert.Throws<ArgumentException>(() => encoder.Encode(new byte[100], false, (_, _) => { }));
    }

    [Theory]
    [InlineData(15, 360)]
    [InlineData(641, 360)]
    [InlineData(640, 361)]
    public void Pictures_that_cannot_be_encoded_are_refused_before_anything_starts(int width, int height)
    {
        if (!FfmpegLibraries.IsAvailable) return;
        Assert.Throws<ArgumentException>(() => new InProcessEncoder(width, height, new ScreenOptions()));
    }

    /// <summary>
    /// The bitrate follows a new value while encoding, in both directions, without restarting the encoder or losing a frame: the way to lower the
    /// quality of a share for viewers who cannot keep up (measured here: 7000, 1650, 1390 and 6900 kbit/s for 6000, 1500, 1500 and 6000 asked for, on
    /// pictures that are expensive to code, so that the encoder is held to its bitrate). Does nothing without the libraries and an NVIDIA card.
    /// </summary>
    [Fact]
    public void The_bitrate_follows_a_new_value_while_encoding_in_both_directions()
    {
        if (!FfmpegLibraries.IsAvailable) return;
        const int w = 1280, h = 720, fps = 30;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(w, h, new ScreenOptions { Fps = fps, Kbps = 6000, KeyFrameSeconds = 2 }); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return; }
        using (encoder)
        {
            var picture = new byte[encoder.PictureBytes];
            var rng = new Random(1);
            int frame = 0;
            double Phase(int seconds)
            {
                long bytes = 0; int frames = 0;
                for (int n = 0; n < seconds * fps; n++, frame++)
                {
                    for (int i = 0; i < w * h; i += 7) picture[i] = (byte)rng.Next(256);
                    for (int i = 0; i < w * h; i++) picture[i] = (byte)(picture[i] + frame);
                    encoder.Encode(picture, false, (f, _) => { bytes += f.Length; frames++; });
                }
                Assert.Equal(seconds * fps, frames);   // a picture in, a frame out, whatever the bitrate
                return bytes * 8 / 1000.0 / seconds;
            }
            double high = Phase(3);
            Assert.InRange(high, 4000, 9000);
            encoder.SetBitrate(1500);
            Assert.Equal(1500, encoder.Kbps);
            Phase(1);   // a moment to settle
            double low = Phase(3);
            Assert.True(low < high * 0.4, $"{low:0} kbit/s after asking for 1500, {high:0} before");
            encoder.SetBitrate(6000);
            Phase(1);
            double back = Phase(3);
            Assert.True(back > low * 2.5, $"{back:0} kbit/s after asking for 6000 again, {low:0} before");
            Assert.Throws<ArgumentOutOfRangeException>(() => encoder.SetBitrate(10));
        }
    }

    /// <summary>
    /// The VA-API encoder (Linux, Intel and AMD cards): where there is a card, it makes key frames on request and says it cannot be given a new bitrate while it runs;
    /// where there is none, it says so plainly and does not crash. Does nothing without the libraries or off Linux.
    /// </summary>
    [Fact]
    public void The_VA_API_encoder_works_where_there_is_a_card_and_says_so_plainly_where_there_is_not()
    {
        if (!OperatingSystem.IsLinux() || !FfmpegLibraries.IsAvailable) return;
        const int w = 640, h = 360;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(w, h, new ScreenOptions { Fps = 30, Kbps = 1500, KeyFrameSeconds = 2 }, HardwareEncoder.Vaapi); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            Assert.True(ex.Message.Contains("VA-API") || ex.Message.Contains("graphics card"), ex.Message);
            return;   // no Intel or AMD card here
        }
        using (encoder)
        {
            Assert.Equal(HardwareEncoder.Vaapi, encoder.Kind);
            Assert.False(encoder.LiveBitrate);
            Assert.Throws<NotSupportedException>(() => encoder.SetBitrate(1000));
            var packets = new List<(byte[] Frame, bool Key)>();
            for (int n = 0; n < 60; n++)
                encoder.Encode(Picture(w, h, n), forceKey: n == 30, (frame, key) => packets.Add((frame, key)));
            Assert.True(packets.Count >= 55, $"{packets.Count} packets for 60 pictures");
            Assert.True(packets[0].Key, "the first packet is a key frame");
            Assert.True(packets.Count(p => p.Key) >= 2, "a key frame was asked for");
        }
    }

    [Theory]
    [InlineData("0.5", 1.5, 0.5)]
    [InlineData(" 2 ", 1.5, 2)]
    [InlineData("1.25", 1.0, 1.25)]
    [InlineData("", 1.5, 1.5)]          // nothing: the default
    [InlineData("many", 1.5, 1.5)]      // not a number: the default
    [InlineData("0.01", 1.5, 1.5)]      // outside what makes sense: the default
    [InlineData("99", 1.5, 1.5)]
    public void The_rate_settings_come_from_the_environment_only_when_they_make_sense(string text, double fallback, double expected)
        => Assert.Equal(expected, EncoderTuning.Parse(text, fallback, 0.1, 5));

    /// <summary>The buffer and the ceiling of the options are what the encoder is set up with (and what its description says), for a key frame no bigger than the buffer lets it be.</summary>
    [Fact]
    public void The_encoder_is_set_up_with_the_buffer_and_the_ceiling_of_the_options()
    {
        if (!FfmpegLibraries.IsAvailable) return;
        InProcessEncoder encoder;
        try { encoder = new InProcessEncoder(640, 360, new ScreenOptions { Fps = 30, Kbps = 2000, BufferSeconds = 0.5, MaxRateMultiple = 1.25, X11Display = null }); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return; }   // no NVIDIA card here
        using (encoder) Assert.Contains("(at most 2500, buffer 1000)", encoder.Describe());
        var defaults = new ScreenOptions { Fps = 30, Kbps = 2000, X11Display = null };
        Assert.Equal((EncoderTuning.BufferSeconds, EncoderTuning.MaxRateMultiple, EncoderTuning.KeySeconds), (defaults.BufferSeconds, defaults.MaxRateMultiple, defaults.KeyFrameSeconds));
        // a quality level keeps them
        var level = defaults.WithQuality(1400, 30, 1280, 720);
        Assert.Equal((defaults.BufferSeconds, defaults.MaxRateMultiple), (level.BufferSeconds, level.MaxRateMultiple));
    }
}
