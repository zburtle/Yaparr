using Yaparr.Screen;

namespace Yaparr.Tests;

public class RetargetTests
{
    [Fact]
    public void Changing_what_is_shared_keeps_the_quality_codec_and_sound()
    {
        var first = new Display(0, "one", 0, 0, 1920, 1080, true, Handle: 0x10);
        var second = new Display(1, "two", 1920, 0, 2560, 1440, false, Handle: 0x20);
        var options = new ScreenOptions
        {
            Display = first, Fps = 60, MaxWidth = 1920, MaxHeight = 1080, Kbps = 6000, KeyFrameSeconds = 2, Codec = ScreenCodec.H264,
            Encoder = HardwareEncoder.Amf, ShareSound = true, GpuScale = true, ShowCursor = false,
        };

        var monitor = options.Retarget(second, null);
        Assert.Equal(second, monitor.Display);
        Assert.Null(monitor.WindowHandle);

        var window = options.Retarget(null, 1234);
        Assert.Null(window.Display);
        Assert.Equal(1234, window.WindowHandle);

        foreach (var changed in new[] { monitor, window })
        {
            Assert.Equal(60, changed.Fps);
            Assert.Equal((1920, 1080, 6000), (changed.MaxWidth, changed.MaxHeight, changed.Kbps));
            Assert.Equal(2, changed.KeyFrameSeconds);
            Assert.Equal((ScreenCodec.H264, HardwareEncoder.Amf), (changed.Codec, changed.Encoder!.Value));
            Assert.True(changed.ShareSound && changed.GpuScale);
            Assert.False(changed.ShowCursor);
        }
    }
}
