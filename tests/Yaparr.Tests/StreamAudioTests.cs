using Yaparr.Voice;

namespace Yaparr.Tests;

public class StreamAudioTests
{
    [Fact]
    public async Task Sound_sent_with_a_shared_screen_arrives_as_the_same_stereo_tone()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 3, rootFanout: 3);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen-audio", "audio:test", kbps: 96);
        var views = await c.WatchAllAsync(source.Info.Id);
        using var sender = new StreamAudioSender(source);
        using var receiver = new StreamAudioReceiver(views[0], delayMs: 60);
        try
        {
            // Left carries a loud 440 Hz tone, right is silent, so the channels must come out unmixed.
            var tone = new float[960 * 2];
            long t = 0;
            var heard = new float[48000 * 2 * 2];
            int got = 0;
            var end = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < end && got < heard.Length)
            {
                for (int i = 0; i < 960; i++, t++) { tone[i * 2] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * t / 48000f); tone[i * 2 + 1] = 0; }
                sender.Push(tone);
                await Task.Delay(20);
                var chunk = new float[960 * 2];
                receiver.Pull(chunk);
                int n = Math.Min(chunk.Length, heard.Length - got);
                chunk.AsSpan(0, n).CopyTo(heard.AsSpan(got));
                got += n;
            }

            // Judge the last second, once the start-up delay has passed.
            var tail = heard.AsSpan(heard.Length / 2);
            double left = 0, right = 0;
            for (int i = 0; i + 1 < tail.Length; i += 2) { left += tail[i] * tail[i]; right += tail[i + 1] * tail[i + 1]; }
            left = Math.Sqrt(left / (tail.Length / 2));
            right = Math.Sqrt(right / (tail.Length / 2));
            Assert.True(left > 0.2, $"the left channel should carry the tone but its level is {left:0.000}");
            Assert.True(right < 0.05, $"the right channel should stay silent but its level is {right:0.000}");
        }
        finally { foreach (var v in views) v.Dispose(); }
    }
}
