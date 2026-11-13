using System.Diagnostics;
using Yaparr.Screen;

namespace Yaparr.Tests;

// Sharing a screen on Linux under X11: the monitor list, the command line for x11grab and where sharing is refused. None of it
// needs Linux or FFmpeg to be checked.
public class LinuxScreenTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string TwoMonitors = "Monitors: 2\n 0: +*DP-1 1920/527x1080/296+1920+0  DP-1\n 1: +HDMI-1 1280/340x1024/270+0+0  HDMI-1\n";

    [Fact]
    public void XrandrListsThePrimaryFirstThenLeftToRight()
    {
        var monitors = Displays.ParseXrandr(TwoMonitors);
        Assert.Equal(2, monitors.Count);
        Assert.Equal(new Display(0, "DP-1", 1920, 0, 1920, 1080, true), monitors[0]);
        Assert.Equal(new Display(1, "HDMI-1", 0, 0, 1280, 1024, false), monitors[1]);
    }

    [Fact]
    public void XrandrWithoutAStarMakesTheFirstMonitorThePrimary()
    {
        var monitors = Displays.ParseXrandr("Monitors: 1\n 0: +eDP-1 1366/310x768/170+0+0  eDP-1\n");
        Assert.Single(monitors);
        Assert.True(monitors[0].Primary);
    }

    [Fact]
    public void AMonitorLeftOfTheMainOneHasANegativePosition()
    {
        var monitors = Displays.ParseXrandr("Monitors: 2\n 0: +*DP-1 1920/527x1080/296+0+0  DP-1\n 1: +DP-2 1920/527x1080/296-1920+0  DP-2\n");
        Assert.Equal(-1920, monitors[1].X);
    }

    [Fact]
    public void NothingUsefulInTheOutputGivesNoMonitors()
    {
        Assert.Empty(Displays.ParseXrandr(""));
        Assert.Empty(Displays.ParseXrandr("Can't open display"));
    }

    [Fact]
    public void X11CaptureGrabsTheMonitorsRectangleAtTheFrameRate()
    {
        var options = new ScreenOptions { X11Display = ":0", Display = new Display(0, "DP-1", 1920, 0, 1920, 1080, true), Fps = 30, ShowCursor = false };
        string line = string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.Vp8));
        Assert.Contains("-f x11grab -framerate 30 -draw_mouse 0 -video_size 1920x1080 -i :0+1920,0", line);
        Assert.Contains("-vf scale='min(1280,iw)':'min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p", line);
        Assert.Contains("-c:v libvpx", line);
        Assert.DoesNotContain("gfxcapture", line);
        Assert.DoesNotContain("-filter_complex", line);
    }

    [Fact]
    public void X11CaptureWithoutAKnownSizeGrabsTheWholeScreen()
    {
        var options = new ScreenOptions { X11Display = ":0.0", Display = new Display(0, "Screen", 0, 0, 0, 0, true) };
        string line = string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.Vp8));
        Assert.DoesNotContain("-video_size", line);
        Assert.Contains("-i :0.0+0,0", line);
    }

    [Fact]
    public void X11CaptureEncodesWithTheGraphicsCardLikeTheWindowsPath()
    {
        var options = new ScreenOptions { X11Display = ":0", Display = new Display(0, "DP-1", 0, 0, 1920, 1080, true) };
        string nvidia = string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Nvenc));
        Assert.Contains("-c:v h264_nvenc", nvidia);
        Assert.Contains("-f h264 pipe:1", nvidia);
        Assert.Contains("format=yuv420p", nvidia);
        Assert.Contains("format=nv12", string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Qsv)));
    }

    [Fact]
    public void X11CaptureGrabsAWindowByItsId()
    {
        string line = string.Join(' ', FfmpegArgs.Capture(new ScreenOptions { X11Display = ":0", WindowHandle = 0x3C00003 }, ScreenCodec.Vp8));
        Assert.Contains("-f x11grab -framerate 10 -draw_mouse 1 -window_id 0x3C00003 -i :0 ", line);
        Assert.DoesNotContain("-video_size", line);
    }

    [Fact]
    public void X11CaptureNeedsSomethingToShare()
    {
        Assert.Throws<ArgumentException>(() => FfmpegArgs.Capture(new ScreenOptions { X11Display = ":0" }, ScreenCodec.Vp8));
    }

    [Fact]
    public void VaapiUploadsTheFramesToTheRenderDevice()
    {
        var options = new ScreenOptions { X11Display = ":0", Display = new Display(0, "DP-1", 0, 0, 1920, 1080, true) };
        var args = FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Vaapi);
        string line = string.Join(' ', args);
        Assert.Contains("-vaapi_device /dev/dri/", line);
        Assert.True(args.IndexOf("-vaapi_device") < args.IndexOf("-i"), "the device is named before the input");
        Assert.Contains("format=nv12,hwupload", line);
        Assert.Contains("-c:v h264_vaapi -profile:v high -bf 0 -aud 1", line);
        Assert.Contains("-f h264 pipe:1", line);
        // Only H.264 on VA-API needs the device; VP8 in software on the same options does not.
        Assert.DoesNotContain("vaapi", string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.Vp8, HardwareEncoder.Vaapi)));
    }

    [Fact]
    public void VaapiIsNamedLikeTheOtherEncoders()
    {
        Assert.Equal("h264_vaapi", HardwareEncoders.Name(HardwareEncoder.Vaapi));
        Assert.Equal("Intel or AMD", HardwareEncoders.Maker(HardwareEncoder.Vaapi));
        Assert.Equal("H.264 on the Intel or AMD graphics card", ScreenCodecs.Describe(ScreenCodec.H264, HardwareEncoder.Vaapi));
        Assert.True(HardwareEncoders.TryParse("VA-API", out var card) && card == HardwareEncoder.Vaapi);
        Assert.True(HardwareEncoders.TryParse("vaapi", out card) && card == HardwareEncoder.Vaapi);
        // Linux tries NVIDIA and then VA-API; the Windows encoders are not tried there, and VA-API is not tried on Windows.
        if (OperatingSystem.IsLinux()) Assert.Equal(new[] { HardwareEncoder.Nvenc, HardwareEncoder.Vaapi }, HardwareEncoders.InOrder);
        else Assert.DoesNotContain(HardwareEncoder.Vaapi, HardwareEncoders.InOrder);
    }

    [Fact]
    public void ReadsTheListOfWindowsFromXprop()
    {
        Assert.Equal(new long[] { 0x1400003, 0x3C00006 }, X11Windows.ParseClientList("_NET_CLIENT_LIST(WINDOW): window id # 0x1400003, 0x3c00006\n"));
        Assert.Empty(X11Windows.ParseClientList("_NET_CLIENT_LIST:  not found.\n"));
        Assert.Empty(X11Windows.ParseClientList(""));
    }

    [Fact]
    public void ReadsAWindowsTitleAndProcessFromXprop()
    {
        var p = X11Windows.ParseProperties(
            "_NET_WM_NAME(UTF8_STRING) = \"notes - \\\"draft\\\" - Editor\"\nWM_NAME(STRING) = \"old\"\n_NET_WM_PID(CARDINAL) = 4242\n_NET_WM_WINDOW_TYPE(ATOM) = _NET_WM_WINDOW_TYPE_NORMAL\n_NET_WM_STATE(ATOM) = _NET_WM_STATE_MAXIMIZED_VERT\n");
        Assert.Equal("notes - \"draft\" - Editor", p.Title);
        Assert.Equal(4242, p.Pid);
        Assert.True(p.Shareable);
    }

    [Fact]
    public void FallsBackToTheOlderWindowNameAndAcceptsAWindowWithoutAType()
    {
        var p = X11Windows.ParseProperties("_NET_WM_NAME:  not found.\nWM_NAME(STRING) = \"xterm\"\n");
        Assert.Equal("xterm", p.Title);
        Assert.True(p.Shareable);
    }

    [Theory]
    [InlineData("_NET_WM_WINDOW_TYPE(ATOM) = _NET_WM_WINDOW_TYPE_DOCK")]
    [InlineData("_NET_WM_WINDOW_TYPE(ATOM) = _NET_WM_WINDOW_TYPE_DESKTOP")]
    [InlineData("_NET_WM_WINDOW_TYPE(ATOM) = _NET_WM_WINDOW_TYPE_MENU")]
    [InlineData("_NET_WM_STATE(ATOM) = _NET_WM_STATE_HIDDEN")]
    public void PanelsMenusAndMinimisedWindowsAreNotOffered(string line)
    {
        Assert.False(X11Windows.ParseProperties("_NET_WM_NAME(UTF8_STRING) = \"x\"\n" + line + "\n").Shareable);
    }

    [Fact]
    public void AWindowWithoutATitleIsNotOffered()
    {
        Assert.False(X11Windows.ParseProperties("_NET_WM_NAME:  not found.\nWM_NAME:  not found.\n").Shareable);
    }

    [Fact]
    public void ReadsAWindowsGeometryFromXwininfo()
    {
        var g = X11Windows.ParseGeometry("xwininfo: Window id: 0x3c00003 \"Editor\"\n\n  Absolute upper-left X:  100\n  Absolute upper-left Y:  -20\n  Relative upper-left X:  0\n  Width: 800\n  Height: 600\n  Map State: IsViewable\n");
        Assert.Equal(new X11Windows.Geometry(100, -20, 800, 600, true), g);
        Assert.False(X11Windows.ParseGeometry("  Width: 800\n  Height: 600\n  Map State: IsUnMapped\n")!.Viewable);
        Assert.Null(X11Windows.ParseGeometry("xwininfo: error: no such window"));
    }

    [Fact]
    public void WithoutAnX11DisplayTheWindowsPathIsUsed()
    {
        var options = new ScreenOptions { X11Display = null, Display = new Display(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B) };
        string line = string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.Vp8));
        Assert.Contains("gfxcapture=hmonitor=0x1A2B", line);
        Assert.DoesNotContain("x11grab", line);
    }

    [Fact]
    public void ChangingTheTargetKeepsTheX11Display()
    {
        var next = new ScreenOptions { X11Display = ":1" }.Retarget(new Display(0, "x", 0, 0, 800, 600, true), null);
        Assert.Equal(":1", next.X11Display);
    }

    // The real thing on a virtual X server: FFmpeg's x11grab with the command line Yaparr builds, and the IVF frames read back.
    // Does nothing where this is not Linux or FFmpeg or Xvfb is missing; the Linux job of the Test workflow installs them.
    [Fact]
    public async Task X11CaptureDeliversFramesFromAVirtualScreen()
    {
        if (!OperatingSystem.IsLinux() || FfmpegLocator.Find() is not { } ffmpeg || FindOnPath("Xvfb") is not { } xvfb) return;
        const string display = ":97";
        using var server = Process.Start(new ProcessStartInfo(xvfb, $"{display} -screen 0 640x480x24") { UseShellExecute = false, RedirectStandardError = true })!;
        try
        {
            await Task.Delay(1500);
            var options = new ScreenOptions { X11Display = display, Display = new Display(0, "screen", 0, 0, 640, 480, true), Fps = 10, MaxWidth = 320, MaxHeight = 240 };
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in FfmpegArgs.Capture(options, ScreenCodec.Vp8)) psi.ArgumentList.Add(a);
            using var capture = Process.Start(psi)!;
            var errors = capture.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                var header = await Ivf.ReadHeaderAsync(capture.StandardOutput.BaseStream, timeout.Token);
                Assert.Equal((320, 240), (header.Width, header.Height));
                for (int i = 0; i < 5; i++)
                    Assert.NotNull(await Ivf.ReadFrameAsync(capture.StandardOutput.BaseStream, timeout.Token));
            }
            finally
            {
                try { capture.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }
        finally
        {
            try { server.Kill(); } catch (InvalidOperationException) { }
        }
    }

    // A real share of a virtual X screen, encoded in this program (NVIDIA's encoder through FFmpeg's libraries, a separate FFmpeg capturing with x11grab), taken
    // through the levels of adaptive quality and back: each level with another frame rate or size starts the capture and the encoder again.
    // Does nothing where this is not Linux, or the libraries, FFmpeg, Xvfb or an NVIDIA card are missing.
    [Fact]
    public async Task A_real_share_of_a_virtual_X_screen_is_encoded_in_this_program_and_goes_through_the_levels_of_adaptive_quality()
    {
        if (!OperatingSystem.IsLinux() || !FfmpegLibraries.IsAvailable || FfmpegLocator.Find() is not { } ffmpeg || FindOnPath("Xvfb") is not { } xvfb) return;
        const string display = ":96";
        using var server = Process.Start(new ProcessStartInfo(xvfb, $"{display} -screen 0 1280x720x24") { UseShellExecute = false, RedirectStandardError = true })!;
        try
        {
            await Task.Delay(1500);
            // Asked for at the top preset: 1080p60 at 6 Mbit/s (the screen is smaller, and a picture is never scaled up).
            var asked = new ScreenOptions
            {
                X11Display = display, Display = new Display(0, "screen", 0, 0, 1280, 720, true), Fps = 60, Kbps = 6000, KeyFrameSeconds = 1,
                MaxWidth = 1920, MaxHeight = 1080, Codec = ScreenCodec.H264, Encoder = HardwareEncoder.Nvenc, InProcessEncoder = true,
            };
            using var cluster = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
            using var sharer = ScreenSharer.Start(cluster.Root, cluster.Rooms[0], "Test screen", asked, ffmpeg);
            try { await TestUtil.WaitFor(() => sharer.Frames >= 60 || !sharer.IsRunning, 30_000); }
            catch (TimeoutException) { output.WriteLine(sharer.Describe()); throw; }
            output.WriteLine(sharer.Describe());
            if (!sharer.InProcess) return;   // no NVIDIA encoder here, or the libraries are not usable
            Assert.Contains("h264_nvenc", sharer.EncoderDetails);
            Assert.Equal(60, sharer.RunFps);

            async Task Wait(Func<bool> done, string what)
            {
                long frames = sharer.Frames;
                try { await TestUtil.WaitFor(() => done() && sharer.Frames >= frames + 40, 30_000); }
                catch (TimeoutException) { output.WriteLine(what + ": " + sharer.Describe()); throw; }
            }

            sharer.ApplyStepForTest(1, "test");   // 1080p30 at 4 Mbit/s: another frame rate, so the capture and the encoder start again
            await Wait(() => sharer.RunFps == 30, "step 1");
            Assert.Contains("at 30 fps, 4000 kbit/s", sharer.EncoderDetails);

            sharer.ApplyStepForTest(QualityLadder.Steps - 1, "test");   // the lowest level: 720p30 at 2 Mbit/s, another size, so the capture and the encoder start again
            await Wait(() => sharer.EncoderDetails.Contains("at 30 fps, 2000 kbit/s"), "the lowest level");
            Assert.Contains("1280x720", sharer.EncoderDetails);

            sharer.ApplyStepForTest(0, "test");   // back to what was asked for
            await Wait(() => sharer.RunFps == 60 && sharer.EncoderDetails.Contains("at 60 fps, 6000 kbit/s"), "step 0");
            Assert.True(sharer.IsRunning);
        }
        finally
        {
            try { server.Kill(); } catch (InvalidOperationException) { }
        }
    }

    // A Wayland share with no FFmpeg in it: a stand-in gst-launch-1.0 (a script that writes black NV12 pictures of the size it is asked for) takes the place of GStreamer,
    // and a portal with no desktop behind it the place of the desktop's stream. The pictures are paced into the encoder in this program, which goes through the levels of
    // adaptive quality and back. Does nothing where this is not Linux, or the libraries, FFmpeg's program (only to be found) or an NVIDIA card are missing.
    [Fact]
    public async Task A_Wayland_share_is_encoded_in_this_program_with_no_FFmpeg_capturing_and_goes_through_the_levels_of_adaptive_quality()
    {
        if (!OperatingSystem.IsLinux() || !FfmpegLibraries.IsAvailable || FfmpegLocator.Find() is not { } ffmpeg) return;
        var dir = Directory.CreateTempSubdirectory("yaparr-gst-").FullName;
        string oldPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        try
        {
            string Script(string name, string text)
            {
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, text.Replace("\r", ""));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return path;
            }
            Script("gst-inspect-1.0", "#!/bin/sh\nexit 0\n");
            Script("gst-launch-1.0", """
                #!/bin/sh
                for a in "$@"; do
                  case "$a" in
                    video/x-raw*) w=$(echo "$a" | sed 's/.*width=\([0-9]*\).*/\1/'); h=$(echo "$a" | sed 's/.*height=\([0-9]*\).*/\1/') ;;
                  esac
                done
                bytes=$((w * h * 3 / 2))
                while true; do head -c "$bytes" /dev/zero || exit 0; sleep 0.02; done
                """);
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + oldPath);

            using var portal = PortalScreenCast.ForTest(File.OpenHandle("/dev/null"), node: 57, width: 1280, height: 720);
            var asked = new ScreenOptions
            {
                Portal = portal, X11Display = null, Fps = 60, Kbps = 6000, KeyFrameSeconds = 1, MaxWidth = 1920, MaxHeight = 1080,
                Codec = ScreenCodec.H264, Encoder = HardwareEncoder.Nvenc, InProcessEncoder = true,
            };
            using var cluster = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
            using var sharer = ScreenSharer.Start(cluster.Root, cluster.Rooms[0], "Test screen", asked, ffmpeg);
            try { await TestUtil.WaitFor(() => sharer.Frames >= 60 || !sharer.IsRunning, 30_000); }
            catch (TimeoutException) { output.WriteLine(sharer.Describe()); throw; }
            output.WriteLine(sharer.Describe());
            if (!sharer.InProcess) return;   // no NVIDIA encoder here
            Assert.Contains("h264_nvenc 1280x720 at 60 fps", sharer.EncoderDetails);
            Assert.Contains("no FFmpeg", sharer.EncoderDetails);
            Assert.DoesNotContain("-f rawvideo", sharer.EncoderDetails.Replace("no FFmpeg", ""));   // no FFmpeg command line is in it

            async Task Wait(Func<bool> done, string what)
            {
                long frames = sharer.Frames;
                try { await TestUtil.WaitFor(() => done() && sharer.Frames >= frames + 40, 30_000); }
                catch (TimeoutException) { output.WriteLine(what + ": " + sharer.Describe()); throw; }
            }

            sharer.ApplyStepForTest(1, "test");   // 1080p30: another frame rate, so GStreamer and the encoder start again
            await Wait(() => sharer.RunFps == 30, "step 1");
            Assert.Contains("at 30 fps, 4000 kbit/s", sharer.EncoderDetails);

            sharer.ApplyStepForTest(QualityLadder.Steps - 1, "test");   // the lowest level: 720p30 at 2 Mbit/s
            await Wait(() => sharer.EncoderDetails.Contains("at 30 fps, 2000 kbit/s"), "the lowest level");

            sharer.ApplyStepForTest(0, "test");   // back to what was asked for
            await Wait(() => sharer.RunFps == 60 && sharer.EncoderDetails.Contains("at 60 fps, 6000 kbit/s"), "step 0");
            Assert.True(sharer.IsRunning);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static string? FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, program)).FirstOrDefault(File.Exists);

    [Theory]
    [InlineData(true, false, null, null, null, null)]
    [InlineData(false, true, "x11", null, ":0", null)]
    [InlineData(false, true, null, null, ":0", null)]
    [InlineData(false, true, "wayland", "wayland-0", ":0", null)]
    [InlineData(false, true, "WAYLAND", null, null, null)]
    [InlineData(false, true, null, "wayland-0", null, null)]
    [InlineData(false, true, "x11", null, null, "DISPLAY")]
    [InlineData(false, true, "tty", null, "", "DISPLAY")]
    [InlineData(false, false, null, null, null, "Windows and Linux")]
    public void SharingIsOfferedOnlyWhereItCanWork(bool windows, bool linux, string? session, string? wayland, string? display, string? reasonContains)
    {
        string? why = ScreenCapture.Why(windows, linux, session, wayland, display);
        if (reasonContains == null) Assert.Null(why);
        else Assert.Contains(reasonContains, why);
    }

    [Theory]
    [InlineData("wayland", null, true)]
    [InlineData("Wayland", "wayland-0", true)]
    [InlineData(null, "wayland-0", true)]
    [InlineData("", "wayland-0", true)]
    [InlineData("x11", "wayland-0", false)]   // an X11 session that happens to have a Wayland socket around
    [InlineData("x11", null, false)]
    [InlineData(null, null, false)]
    [InlineData("tty", "wayland-0", false)]
    public void ASessionIsWaylandWhenItSaysSo(string? session, string? socket, bool wayland)
    {
        Assert.Equal(wayland, ScreenCapture.IsWayland(session, socket));
    }

    [Fact]
    public async Task The_sharing_diagnostics_say_how_this_computer_shares_and_do_not_fail_without_ffmpeg()
    {
        var text = await ScreenDiagnostics.BuildAsync(ffmpegPath: Path.Combine(Path.GetTempPath(), "no-such-ffmpeg-" + Guid.NewGuid().ToString("N")));
        Assert.Contains("Screen sharing", text);
        Assert.Contains("Way of capture here:", text);
        Assert.Contains("FFmpeg version:", text);   // asked of a program that is not there: said so, not thrown
        Assert.Contains("could not run", text);
        if (OperatingSystem.IsLinux()) Assert.Contains("Session: XDG_SESSION_TYPE=", text);
    }

    // A Flatpak is not given XDG_SESSION_TYPE, and WAYLAND_DISPLAY only exists in the sandbox with the Wayland socket. Without that socket
    // a Wayland desktop looks like X11 to the app, which then copies the picture from XWayland: a viewer sees the first picture and no more.
    [Fact]
    public void The_flatpak_can_see_that_it_is_on_a_wayland_desktop()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "installer", "flatpak", "com.yaparr.Yaparr.yml"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var arguments = File.ReadAllLines(Path.Combine(dir!, "installer", "flatpak", "com.yaparr.Yaparr.yml")).Select(l => l.Trim()).ToList();
        Assert.Contains("- --socket=wayland", arguments);
        Assert.Contains("- --socket=x11", arguments); // the window itself is still drawn through X11
    }

    private static readonly PortalStream Monitor1080 = new(57, 1920, 1080, false);

    [Fact]
    public void ThePortalPictureIsScaledDownIntoTheBoxInEvenNumbers()
    {
        Assert.Equal((1280, 720), PortalPipeline.FrameSize(Monitor1080, new ScreenOptions()));
        Assert.Equal((1920, 1080), PortalPipeline.FrameSize(Monitor1080, new ScreenOptions { MaxWidth = 3840, MaxHeight = 2160 }));   // never up
        Assert.Equal((1000, 500), PortalPipeline.FrameSize(new PortalStream(1, 2000, 1000, true), new ScreenOptions { MaxWidth = 1000, MaxHeight = 1000 }));
        // A portal that does not say how big the picture is gets the box.
        Assert.Equal((1280, 720), PortalPipeline.FrameSize(new PortalStream(1, 0, 0, false), new ScreenOptions()));
    }

    [Fact]
    public void GStreamerReadsTheStreamFromTheDescriptorItInherits()
    {
        var args = PortalPipeline.GstArguments(Monitor1080, 9, new ScreenOptions { Fps = 30 });
        string line = string.Join(' ', args);
        Assert.StartsWith("-q pipewiresrc fd=9 path=57 keepalive-time=33 do-timestamp=true ! videoconvert ! videoscale ! videorate skip-to-first=true max-duplication-time=500000000 ! ", line);
        Assert.Contains("video/x-raw,format=I420,width=1280,height=720,framerate=30/1", line);
        Assert.EndsWith("! fdsink fd=1 sync=false", line);
    }

    [Fact]
    public void For_the_encoder_in_this_program_GStreamer_writes_NV12_which_the_encoder_takes_as_it_is()
    {
        var args = PortalPipeline.GstArguments(Monitor1080, 9, new ScreenOptions { Fps = 30 }, nv12: true);
        string line = string.Join(' ', args);
        Assert.Contains("video/x-raw,format=NV12,width=1280,height=720,framerate=30/1", line);
        Assert.DoesNotContain("I420", line);
        Assert.EndsWith("! fdsink fd=1 sync=false", line);
    }

    [Fact]
    public void FfmpegEncodesTheRawPicturesFromGStreamer()
    {
        var args = FfmpegArgs.CapturePortal(new ScreenOptions { Fps = 20 }, Monitor1080, ScreenCodec.Vp8);
        string line = string.Join(' ', args);
        Assert.Contains("-f rawvideo -pixel_format yuv420p -video_size 1280x720 -framerate 20 -i pipe:0", line);
        Assert.Contains("-vf format=yuv420p", line);
        Assert.Contains("-c:v libvpx", line);
        Assert.DoesNotContain("-nostdin", line);   // the pictures arrive on standard input
        Assert.DoesNotContain("x11grab", line);

        var vaapi = FfmpegArgs.CapturePortal(new ScreenOptions(), Monitor1080, ScreenCodec.H264, HardwareEncoder.Vaapi);
        Assert.True(vaapi.IndexOf("-vaapi_device") < vaapi.IndexOf("-i"));
        Assert.Contains("-vf format=nv12,hwupload", string.Join(' ', vaapi));
        Assert.Contains("-f h264 pipe:1", string.Join(' ', vaapi));
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegArgs.CapturePortal(new ScreenOptions { Fps = 0 }, Monitor1080, ScreenCodec.Vp8));
    }

    private const string SinkInputs = @"Sink Input #52
	Driver: PipeWire
	Corked: no
	Mute: no
	Properties:
		application.name = ""Firefox""
		application.process.id = ""4100""
		application.process.binary = ""firefox""
Sink Input #61
	Driver: PipeWire
	Corked: yes
	Properties:
		application.name = ""Spotify""
		application.process.id = ""5200""
		application.process.binary = ""spotify""
Sink Input #70
	Driver: PipeWire
	Corked: no
	Properties:
		application.name = ""Yaparr""
		application.process.id = ""6300""
		application.process.binary = ""Yaparr.Desktop""
Sink Input #75
	Corked: no
	Properties:
		media.name = ""audio stream""
";

    [Fact]
    public void ReadsTheSoundStreamsOfTheSoundServer()
    {
        var inputs = PulseAppCapture.ParseSinkInputs(SinkInputs);
        Assert.Equal(4, inputs.Count);
        Assert.Equal(new SinkInput(52, 4100, "firefox", "Firefox", false), inputs[0]);
        Assert.Equal(new SinkInput(61, 5200, "spotify", "Spotify", true), inputs[1]);
        Assert.Equal(new SinkInput(75, 0, "", "", false), inputs[3]);
        Assert.Empty(PulseAppCapture.ParseSinkInputs(""));
        Assert.Empty(PulseAppCapture.ParseSinkInputs("No PulseAudio daemon running"));
    }

    [Fact]
    public void AMonitorsSoundIsEveryStreamPlayingExceptYaparrs()
    {
        var inputs = PulseAppCapture.ParseSinkInputs(SinkInputs);
        // By process: Yaparr (6300) and the programs it started are left out, a paused stream is too.
        Assert.Equal(new[] { 52, 75 }, PulseAppCapture.ChooseAllExcept(inputs, new HashSet<int> { 6300 }, "").Select(i => i.Index));
        // By name, when the stream is played by another process of the same program.
        Assert.Equal(new[] { 52, 75 }, PulseAppCapture.ChooseAllExcept(inputs, new HashSet<int> { 1 }, "Yaparr.Desktop").Select(i => i.Index));
        // A stream that does not say which process plays it (process 0) is not mistaken for Yaparr's.
        Assert.Contains(75, PulseAppCapture.ChooseAllExcept(inputs, new HashSet<int> { 6300 }, "").Select(i => i.Index));
    }

    [Fact]
    public void StreamsAreAddedTogetherAndClipped()
    {
        var a = new PulseAppCapture.Track();
        var b = new PulseAppCapture.Track();
        a.Add(new float[] { 0.25f, -0.25f, 0.5f, 0.5f });
        b.Add(new float[] { 0.25f, 0.25f, 0.75f, -0.75f, 0.1f, 0.1f });
        var mix = new float[8];
        int count = PulseAppCapture.Mix(new[] { a, b }, mix, new float[8]);
        Assert.Equal(6, count);   // the longer stream sets how much goes on
        Assert.Equal(new[] { 0.5f, 0f, 1f, -0.25f, 0.1f, 0.1f }, mix.Take(6));   // 0.5 + 0.75 is clipped to 1
        Assert.Equal(0, PulseAppCapture.Mix(new[] { a, b }, mix, new float[8]));
    }

    [Fact]
    public void AStreamKeepsOnlyTheLastTwoHundredMilliseconds()
    {
        var track = new PulseAppCapture.Track();
        var old = new float[PulseAppCapture.Channels * PulseAppCapture.SampleRate / 5];
        Array.Fill(old, 0.1f);
        track.Add(old);
        track.Add(new float[] { 0.9f, 0.9f });
        var taken = new float[old.Length + 2];
        int n = track.Take(taken);
        Assert.Equal(old.Length, n);
        Assert.Equal(0.9f, taken[n - 1]);   // the newest sound is kept, the oldest dropped
        Assert.Equal(0.1f, taken[0]);
    }

    [Fact]
    public void AWindowsSoundIsItsProgramsStreamsNotTheVoiceChat()
    {
        var inputs = PulseAppCapture.ParseSinkInputs(SinkInputs);
        int[] Of(ISet<int> pids, string program, params string[] classes) => PulseAppCapture.ChooseOf(inputs, pids, program, classes).Select(i => i.Index).ToArray();
        // By process: the window's program, or one it started.
        Assert.Equal(new[] { 52 }, Of(new HashSet<int> { 100, 4100 }, ""));
        // By name: a browser plays its sound from another process than the one that owns the window.
        Assert.Equal(new[] { 52 }, Of(new HashSet<int> { 100 }, "firefox"));
        // By the window's class names (what a sandboxed program, whose process numbers are not ours, is known by), in any letter case.
        Assert.Equal(new[] { 52 }, Of(new HashSet<int>(), "", "Navigator", "Firefox"));
        // A paused stream is left alone, and other programs' streams (the voice chat is one) are not chosen.
        Assert.Empty(Of(new HashSet<int> { 5200 }, "spotify"));
        Assert.Empty(Of(new HashSet<int> { 100 }, "terminal"));
        // A stream that does not say which process plays it (0) is not taken for the window's program, whatever the program is.
        Assert.DoesNotContain(75, Of(new HashSet<int> { 0 }, ""));
        // All of a program's streams, not only the newest: a game with music and effects, a browser with two tabs.
        var two = new[] { new SinkInput(10, 7, "player", "p", false), new SinkInput(20, 7, "player", "p", false) };
        Assert.Equal(new[] { 10, 20 }, PulseAppCapture.ChooseOf(two, new HashSet<int> { 7 }, "").Select(i => i.Index));
    }

    [Fact]
    public void TheProgramsThatCouldBePickedAreTheOnesPlayingExceptYaparr()
    {
        var inputs = PulseAppCapture.ParseSinkInputs(SinkInputs);
        // Firefox plays; Spotify is paused; Yaparr is left out by process; the stream with no name has nothing to show.
        Assert.Equal(new[] { "Firefox" }, PulseAppCapture.PlayingPrograms(inputs, new HashSet<int> { 6300 }, ""));
        // Picked by that name, the program's sound is found.
        Assert.Equal(new[] { 52 }, PulseAppCapture.ChooseOf(inputs, new HashSet<int>(), "Firefox").Select(i => i.Index));
    }

    [Fact]
    public void AWindowsClassNamesAreRead()
    {
        var p = X11Windows.ParseProperties("_NET_WM_NAME(UTF8_STRING) = \"Docs\"\nWM_CLASS(STRING) = \"Navigator\", \"firefox\"\n_NET_WM_PID(CARDINAL) = 4100\n");
        Assert.Equal(new[] { "Navigator", "firefox" }, p.Classes);
        Assert.Empty(X11Windows.ParseProperties("WM_CLASS:  not found.\n").Classes);
    }

    [Fact]
    public void TheProgramAndItsChildrenAreTheProcessTree()
    {
        var tree = PulseAppCapture.ProcessTree(Environment.ProcessId);
        Assert.Contains(Environment.ProcessId, tree);
        if (!OperatingSystem.IsLinux()) return;
        using var child = Process.Start(new ProcessStartInfo("sleep", "5") { UseShellExecute = false })!;
        try { Assert.Contains(child.Id, PulseAppCapture.ProcessTree(Environment.ProcessId)); }
        finally { child.Kill(); }
    }

    // The sound of every program but Yaparr's: a tone played by a program Yaparr started (like the voice chat) is left out, one played by
    // another program (started apart from the test, with setsid) is recorded. Does nothing without a sound server.
    [Fact]
    public async Task AMonitorsSoundHasOtherProgramsButNotYaparrs()
    {
        if (!OperatingSystem.IsLinux() || !PulseAppCapture.IsSupported || PortalPipeline.Find("gst-launch-1.0") is not { } gst || PortalPipeline.Find("setsid") is not { } setsid || PortalPipeline.Find("pkill") is not { } pkill) return;
        string tone(int freq) => $"-q audiotestsrc wave=sine freq={freq} volume=0.5 is-live=true ! audioconvert ! pulsesink";
        using var ours = Process.Start(new ProcessStartInfo(gst, tone(440)) { UseShellExecute = false, RedirectStandardError = true })!;
        double peak = 0;
        int values = 0;
        PulseAppCapture recording;
        try
        {
            recording = PulseAppCapture.StartAllExceptThisProgram((samples, count) =>
            {
                double p = 0;
                for (int i = 0; i < count; i++) p = Math.Max(p, Math.Abs(samples[i]));
                lock (this) peak = Math.Max(peak, p);
                Interlocked.Add(ref values, count);
            });
        }
        catch (InvalidOperationException) { ours.Kill(); return; }   // no sound server is running here
        using (recording)
        {
            try
            {
                await Task.Delay(4000);
                lock (this) Assert.True(peak < 0.01, $"the sound of a program Yaparr started was recorded (peak {peak})");
                ours.Kill();   // one tone at a time: some sound servers (WSLg's) record two at once as silence
                await ours.WaitForExitAsync();
                await Task.Delay(1500);
                var psi = new ProcessStartInfo(setsid) { UseShellExecute = false };
                foreach (var a in new[] { "-f", gst }.Concat(tone(441).Split(' '))) psi.ArgumentList.Add(a);
                Process.Start(psi)!.WaitForExit();
                var until = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < until) { lock (this) if (peak > 0.1) break; await Task.Delay(200); }
                lock (this) Assert.True(peak > 0.1, $"the sound of another program was not recorded (peak {peak}, {values} values)");
            }
            finally
            {
                try { ours.Kill(); } catch (InvalidOperationException) { }
                Process.Start(pkill, "-f freq=441")?.WaitForExit();
            }
        }
    }

    // A sine wave played by a program of its own, recorded the way a shared window's sound is. Does nothing without a sound server.
    [Fact]
    public async Task ThePlayedSoundOfAProgramIsRecorded()
    {
        if (!OperatingSystem.IsLinux() || !PulseAppCapture.IsSupported || PortalPipeline.Find("gst-launch-1.0") is not { } gst) return;
        var psi = new ProcessStartInfo(gst, "-q audiotestsrc wave=sine freq=440 volume=0.5 is-live=true ! audioconvert ! pulsesink") { UseShellExecute = false, RedirectStandardError = true };
        using var player = Process.Start(psi)!;
        double peak = 0;
        int values = 0;
        using var heard = new ManualResetEventSlim();
        PulseAppCapture recording;
        try
        {
            recording = PulseAppCapture.Start(player.Id, (samples, count) =>
            {
                for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(samples[i]));
                if (Interlocked.Add(ref values, count) > 48000 && peak > 0.1) heard.Set();
            });
        }
        catch (InvalidOperationException) { player.Kill(); return; }   // no sound server is running here
        using (recording)
        {
            try { Assert.True(await Task.Run(() => heard.Wait(TimeSpan.FromSeconds(15))), $"no sound was recorded (peak {peak}, {values} values)"); }
            finally { try { player.Kill(); } catch (InvalidOperationException) { } }
        }
    }
}
