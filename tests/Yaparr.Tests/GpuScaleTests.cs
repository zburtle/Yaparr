using Yaparr.Screen;

namespace Yaparr.Tests;

public class GpuScaleTests
{
    private static readonly Display Monitor = new(0, "x", 0, 0, 1920, 1080, true, Handle: 0x1A2B);

    [Fact]
    public void NvidiaH264ScalesOnTheCardWhenAsked()
    {
        string line = string.Join(' ', FfmpegArgs.Capture(new ScreenOptions { Display = Monitor }, ScreenCodec.H264, HardwareEncoder.Nvenc, gpuScale: true));
        Assert.Contains("scale_d3d11=", line);
        Assert.Contains("format=nv12", line);
        Assert.DoesNotContain("hwdownload", line);
        Assert.Contains("-c:v h264_nvenc", line);
    }

    [Fact]
    public void TheProcessorPathIsUnchangedWithoutGpuScale()
    {
        var options = new ScreenOptions { Display = Monitor, Kbps = 2000 };
        Assert.Equal(FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Nvenc), FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Nvenc, gpuScale: false));
        string line = string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Nvenc, gpuScale: false));
        Assert.Contains("hwdownload", line);
        Assert.DoesNotContain("scale_d3d11", line);
    }

    [Fact]
    public void GpuScaleIsOnlyForNvidiaH264()
    {
        var options = new ScreenOptions { Display = Monitor };
        Assert.Contains("hwdownload", string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.Vp8, HardwareEncoder.Nvenc, gpuScale: true)));
        Assert.Contains("hwdownload", string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Amf, gpuScale: true)));
        Assert.Contains("hwdownload", string.Join(' ', FfmpegArgs.Capture(options, ScreenCodec.H264, HardwareEncoder.Qsv, gpuScale: true)));
    }
}
