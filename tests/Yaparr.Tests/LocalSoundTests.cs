using Yaparr.Voice;

namespace Yaparr.Tests;

public class LocalSoundTests
{
    [Fact]
    public void What_is_pushed_comes_out_in_order_and_silence_follows()
    {
        var sound = new LocalSound();
        sound.Push(new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
        var output = new float[6];
        output.AsSpan().Fill(9f);
        sound.Pull(output);
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0f, 0f }, output);
    }

    [Fact]
    public void When_it_is_not_pulled_the_oldest_is_dropped_and_the_gain_applies()
    {
        var sound = new LocalSound { Gain = 0.5f };
        var big = new float[48000 * 2 * 2 + 4];
        big[^4] = 0.8f; big[^3] = 0.6f; big[^2] = 0.4f; big[^1] = 0.2f;
        sound.Push(big);
        var output = new float[48000 * 2 * 2];
        sound.Pull(output);
        // The ring holds two seconds; 10 ms of it was dropped to catch up, and the last four values pushed are the last it has.
        int played = 48000 * 2 * 2 - 960;
        Assert.Equal(new[] { 0.4f, 0.3f, 0.2f, 0.1f }, output[(played - 4)..played]);
    }

    [Fact]
    public void A_delayed_sound_is_silent_until_that_much_has_arrived()
    {
        var sound = new LocalSound { DelayMs = 10 };   // 960 values of stereo
        var output = new float[10];
        sound.Push(Enumerable.Repeat(0.5f, 100).ToArray());
        sound.Pull(output);
        Assert.All(output, v => Assert.Equal(0f, v));
        sound.Push(Enumerable.Repeat(0.5f, 900).ToArray());
        sound.Pull(output);
        Assert.All(output, v => Assert.Equal(0.5f, v));
    }
}
