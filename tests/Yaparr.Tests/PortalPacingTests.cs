using System.IO.Pipes;
using Yaparr.Screen;

namespace Yaparr.Tests;

// What GStreamer hands over can come in a burst (a desktop that makes it make up minutes of copies of the first picture at once); the encoder is
// given the pictures at the frame rate, whatever arrives.
public class PortalPacingTests
{
    private const int Width = 4, Height = 2, Bytes = Width * Height * 3 / 2;

    private static byte[] Picture(int marker) => Enumerable.Repeat((byte)marker, Bytes).ToArray();

    [Fact]
    public async Task A_burst_of_pictures_reaches_the_encoder_at_the_frame_rate_with_the_newest_kept()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        var sink = new MemoryStream();
        var pacing = Task.Run(() => ScreenSharer.PaceAsync(client, sink, (Width, Height), 30, () => { }));

        // 2,400 pictures as fast as they can be written: 80 seconds of video at 30 frames a second.
        var started = DateTime.UtcNow;
        for (int i = 0; i < 2400; i++) { await server.WriteAsync(Picture(i % 250 + 1)); }
        await server.FlushAsync();
        await Task.Delay(300);
        server.Dispose();
        await pacing.WaitAsync(TimeSpan.FromSeconds(10));

        int received = (int)(sink.Length / Bytes);
        double seconds = (DateTime.UtcNow - started).TotalSeconds;
        Assert.InRange(received, 1, (int)(seconds * 30) + 6);   // never more than the frame rate allows
        Assert.True(received < 100, $"{received} pictures for a burst of 2400");
    }

    [Fact]
    public async Task A_still_screen_gives_the_same_picture_again_at_the_frame_rate()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        var sink = new MemoryStream();
        var pacing = Task.Run(() => ScreenSharer.PaceAsync(client, sink, (Width, Height), 20, () => { }));

        await server.WriteAsync(Picture(7));
        await server.FlushAsync();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(1000);
        server.Dispose();
        await pacing.WaitAsync(TimeSpan.FromSeconds(10));
        double seconds = clock.Elapsed.TotalSeconds;

        // About 20 a second. A loaded machine (a shared build server) can hold the clock back for a while and send fewer, so the lower bound is a fraction of the time that really passed
        // (it got 8 of the 12 asked for in one run); what matters is that the same picture is repeated, and not faster than the rate.
        int received = (int)(sink.Length / Bytes);
        Assert.InRange(received, Math.Max(4, (int)(20 * seconds * 0.25)), (int)(20 * seconds * 1.3) + 2);
        var all = sink.ToArray();
        Assert.All(all, b => Assert.Equal(7, b));
    }

    [Fact]
    public async Task The_encoder_input_is_closed_when_the_pictures_stop()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        bool closed = false;
        var pacing = Task.Run(() => ScreenSharer.PaceAsync(client, new MemoryStream(), (Width, Height), 30, () => closed = true));
        server.Dispose();
        await pacing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(closed);
    }
}
