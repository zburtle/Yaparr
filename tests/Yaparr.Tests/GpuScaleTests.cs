using Yaparr.Screen;

namespace Yaparr.Tests;

public class GpuScaleTests
{
    private static readonly Display Monitor = new(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B);

    [Fact]
    public void NvidiaH264ScalesOnTheCardWhenAsked()
    {
        string line = string.Join(' ', FfmpegArgs.Capture(new ScreenOptions { X11Display = null, Display = Monitor }, ScreenCodec.H264, HardwareEncoder.Nvenc, gpuScale: true));
        Assert.Contains(":width=1280:height=720", line);
        Assert.DoesNotContain("scale_d3d11", line);
        Assert.DoesNotContain("hwdownload", line);
        Assert.Contains("-c:v h264_nvenc", line);
    }

    [Fact]
    public void NothingIsScaledWhenTheMonitorAlreadyFits()
    {
        string line = string.Join(' ', FfmpegArgs.Capture(new ScreenOptions { X11Display = null, Display = Monitor, MaxWidth = 1920, MaxHeight = 1080 }, ScreenCodec.H264, HardwareEncoder.Nvenc, gpuScale: true));
        Assert.DoesNotContain("hwdownload", line);
        Assert.DoesNotContain(":width=", line);
    }

    [Fact]
    public void ASharedWindowTakesTheProcessorPath()
    {
        string line = string.Join(' ', FfmpegArgs.Capture(new ScreenOptions { X11Display = null, WindowHandle = 0x99 }, ScreenCodec.H264, HardwareEncoder.Nvenc, gpuScale: true));
        Assert.Contains("hwdownload", line);
    }

    [Fact]
    public void TheProcessorPathIsUnchangedWithoutGpuScale()
    {
        var options = new ScreenOptions { X11Display = null, Display = Monitor, Kbps = 2000 };
        Assert.Equal(FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Nvenc), FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Nvenc, gpuScale: false));
        string line = string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Nvenc, gpuScale: false));
        Assert.Contains("hwdownload", line);
        Assert.DoesNotContain("scale_d3d11", line);
    }

    [Fact]
    public void GpuScaleIsOnlyForNvidiaH264()
    {
        var options = new ScreenOptions { X11Display = null, Display = Monitor };
        Assert.Contains("hwdownload", string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.Vp8, HardwareEncoder.Nvenc, gpuScale: true)));
        Assert.Contains("hwdownload", string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Amf, gpuScale: true)));
        Assert.Contains("hwdownload", string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Qsv, gpuScale: true)));
    }
}
