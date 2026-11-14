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
    public void The_size_is_read_from_the_output_stream_and_not_from_the_input()
    {
        var parser = new RawSizeParser();
        Assert.False(parser.Feed("Input #0, x11grab, from ':0':", out _, out _));
        Assert.False(parser.Feed("  Stream #0:0: Video: rawvideo (BGR[0] / 0x30524742), bgr0, 2560x1440, 3000 kb/s, 30 fps, 1000k tbr", out _, out _));
        Assert.False(parser.Feed("Output #0, rawvideo, to 'pipe:1':", out _, out _));
        Assert.True(parser.Feed("  Stream #0:0: Video: rawvideo (NV12 / 0x3231564E), nv12(pc, gbr/unknown/unknown, progressive), 1920x1080 [SAR 1:1 DAR 16:9], 3110400 kb/s, 30 fps, 30 tbn", out int w, out int h));
        Assert.Equal((1920, 1080), (w, h));
    }

    [Fact]
    public void Nothing_is_read_as_a_size_before_the_output_is_described()
    {
        var parser = new RawSizeParser();
        Assert.False(parser.Feed("  Stream #0:0: Video: rawvideo (NV12 / 0x3231564E), nv12, 1280x720, 30 fps", out _, out _));
        Assert.False(parser.Feed("Stream mapping:", out _, out _));
    }

    [Fact]
    public void The_raw_capture_is_the_same_capture_with_raw_pictures_instead_of_an_encoder()
    {
        var display = new Display(0, "x", 0, 0, 2560, 1440, true, Handle: 0x1A2B);
        var options = new ScreenOptions { X11Display = null, Display = display, Fps = 60, MaxWidth = 1920, MaxHeight = 1080 };
        var raw = FfmpegArgs.CaptureRaw(options);
        var encoded = FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Nvenc);

        Assert.Equal("info", raw[raw.IndexOf("-loglevel") + 1]);   // FFmpeg only says the picture size at this level
        Assert.Contains("-nostats", raw);
        Assert.DoesNotContain("-c:v", raw);
        Assert.Equal(new[] { "-f", "rawvideo", "-pix_fmt", "nv12", "pipe:1" }, raw.TakeLast(5));
        string rawFilter = raw[raw.IndexOf("-filter_complex") + 1], encodedFilter = encoded[encoded.IndexOf("-filter_complex") + 1];
        Assert.Equal(encodedFilter[..encodedFilter.LastIndexOf("format=")], rawFilter[..rawFilter.LastIndexOf("format=")]);   // the capture and scaling are the same
        Assert.EndsWith("format=nv12", rawFilter);
    }

    [Fact]
    public void The_raw_capture_on_x11_scales_and_converts_like_the_encoded_one()
    {
        var options = new ScreenOptions { X11Display = ":0", Display = new Display(0, "x", 0, 0, 1920, 1080, true), Fps = 30, MaxWidth = 1280, MaxHeight = 720 };
        var raw = FfmpegArgs.CaptureRaw(options);
        Assert.Contains("x11grab", raw);
        Assert.Contains("scale='min(1280,iw)':'min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format=nv12", raw);
        Assert.Equal(new[] { "-f", "rawvideo", "-pix_fmt", "nv12", "pipe:1" }, raw.TakeLast(5));
    }

    [Fact]
    public void The_in_process_encoder_is_given_the_options_of_the_command_line()
    {
        var args = FfmpegArgs.Capture(new ScreenOptions { X11Display = null, Display = new Display(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B) }, ScreenCodec.H264, HardwareEncoder.Nvenc);
        foreach (var (name, value) in FfmpegArgs.NvencH264Options)
        {
            string flag = "-" + (name == "profile" ? "profile:v" : name);
            int at = args.IndexOf(flag);
            Assert.True(at >= 0, $"the command line has no {flag}");
            Assert.Equal(value, args[at + 1]);
        }
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

    /// <summary>
    /// The whole chain on this machine: a real FFmpeg captures a monitor and writes raw pictures, the size is read from what it prints, the
    /// in-process encoder makes H.264 of them, and a real FFmpeg decodes that again to the same number of pictures. It does nothing without
    /// Windows, a monitor, FFmpeg and its libraries, or an NVIDIA card.
    /// </summary>
    [Fact]
    public async Task A_real_capture_goes_through_the_in_process_encoder_and_the_result_decodes()
    {
        if (!OperatingSystem.IsWindows() || !FfmpegLibraries.IsAvailable) return;
        if (FfmpegLocator.Find() is not { } ffmpeg) return;
        if (Displays.List().FirstOrDefault() is not { } display) return;
        var options = new ScreenOptions { Display = display, Fps = 15, Kbps = 1000, MaxWidth = 640, MaxHeight = 360, X11Display = null };
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in FfmpegArgs.CaptureRaw(options)) psi.ArgumentList.Add(a);
        using var capture = System.Diagnostics.Process.Start(psi)!;
        var size = new TaskCompletionSource<(int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parser = new RawSizeParser();
        capture.ErrorDataReceived += (_, e) => { if (e.Data != null && parser.Feed(e.Data, out int w, out int h)) size.TrySetResult((w, h)); };
        capture.BeginErrorReadLine();
        var frames = new List<byte[]>();
        try
        {
            var (width, height) = await size.Task.WaitAsync(limit.Token);
            Assert.True(width <= 640 && height <= 360 && width % 2 == 0 && height % 2 == 0, $"unexpected size {width}x{height}");
            InProcessEncoder encoder;
            try { encoder = new InProcessEncoder(width, height, options); }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return; }
            using (encoder)
            {
                var picture = new byte[encoder.PictureBytes];
                var stdout = capture.StandardOutput.BaseStream;
                for (int n = 0; n < 30; n++)
                {
                    await stdout.ReadExactlyAsync(picture, limit.Token);
                    encoder.Encode(picture, forceKey: n == 20, (frame, _) => frames.Add(frame));
                }
            }
            Assert.Equal(30, frames.Count);
            Assert.True(ScreenCodecs.IsKeyFrame(ScreenCodec.H264, frames[0]));
            Assert.True(H264.TryGetSize(frames[0], out int sw, out int sh));
            Assert.Equal((width, height), (sw, sh));
            Assert.True(ScreenCodecs.IsKeyFrame(ScreenCodec.H264, frames[20]));

            // A real FFmpeg decodes them: 30 pictures of NV12 come out.
            var dpsi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in FfmpegArgs.DecodeForViewer(ScreenCodec.H264, gpu: false)) dpsi.ArgumentList.Add(a);
            using var decoder = System.Diagnostics.Process.Start(dpsi)!;
            decoder.BeginErrorReadLine();
            var output = decoder.StandardOutput.BaseStream.CopyToAsync(Stream.Null, limit.Token);
            foreach (var frame in frames) await decoder.StandardInput.BaseStream.WriteAsync(frame, limit.Token);
            decoder.StandardInput.Close();
            await output;
            await decoder.WaitForExitAsync(limit.Token);
            Assert.Equal(0, decoder.ExitCode);
        }
        finally { try { if (!capture.HasExited) capture.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
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
