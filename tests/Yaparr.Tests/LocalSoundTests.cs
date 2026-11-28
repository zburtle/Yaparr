using Yaparr.Voice;

namespace Yaparr.Tests;

public class LocalSoundTests
{
    [Fact]
    public void What_is_pushed_comes_out_fading_in_and_out_around_the_silence_that_follows()
    {
        var sound = new LocalSound();
        sound.Push(Enumerable.Repeat(0.5f, 20_000).ToArray());   // 10 000 stereo frames
        var output = new float[30_000];
        output.AsSpan().Fill(9f);
        sound.Pull(output);
        Assert.True(output[0] < 0.01f, "it starts from silence");
        Assert.Equal(0.5f, output[2 * 400]);   // fully in after 5 ms
        Assert.Equal(0.5f, output[5_000]);
        int last = Array.FindLastIndex(output, v => v > 0f);
        Assert.InRange(last, 19_000, 20_000);
        Assert.True(output[last] < 0.01f, "the end is the end of a fade out");
        Assert.All(output[(last + 1)..], v => Assert.Equal(0f, v));
        Assert.Equal(1, sound.Underruns);
    }

    [Fact]
    public void Far_too_much_held_is_cut_back_a_little_at_a_time()
    {
        var sound = new LocalSound { DelayMs = 100 };
        sound.Push(new float[48000 * 2]);   // a second of it
        Assert.InRange(sound.BufferedMs, 999, 1000);
        sound.Pull(new float[960]);   // 10 ms asked for
        Assert.Equal(1, sound.Trims);
        Assert.InRange(sound.BufferedMs, 970, 985);   // 10 ms played and 10 ms cut
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
        Assert.All(output, v => Assert.InRange(v, 0.0001f, 0.4999f));   // playing, still fading in
    }

    [Fact]
    public void Running_dry_and_starting_again_never_makes_a_click()
    {
        var sound = new LocalSound { DelayMs = 20 };
        var played = new List<float>();
        var block = new float[960];
        // A loud, steady sound that comes in pieces too small to keep up: it runs dry between them, again and again.
        for (int round = 0; round < 6; round++)
        {
            sound.Push(Enumerable.Repeat(0.8f, 2400).ToArray());   // 25 ms of stereo
            for (int i = 0; i < 4; i++) { sound.Pull(block); played.AddRange(block); }
        }
        Assert.True(played.Any(v => v > 0.7f), "the sound was played");
        Assert.True(played.Contains(0f), "and it did run dry");
        Assert.True(BiggestStep(played) < 0.02f, $"the biggest step between neighbouring samples was {BiggestStep(played)}: that is a click");
    }

    [Fact]
    public void A_sound_that_comes_a_little_faster_or_slower_than_it_is_played_neither_runs_dry_nor_gets_cut()
    {
        foreach (int every in new[] { 400, -400 })   // one frame more (or less) every 400 pieces: the film's clock a quarter of a percent off the card's
        {
            var sound = new LocalSound { DelayMs = 60 };
            var played = new List<float>();
            var block = new float[960];   // 10 ms
            double phase = 0;
            var rise = new List<int>();
            for (int round = 0; round < 6000; round++)   // a minute
            {
                int frames = 480 + (round % Math.Abs(every) == 0 ? Math.Sign(every) : 0);
                var piece = new float[frames * 2];
                for (int f = 0; f < frames; f++)
                {
                    float v = (float)(0.8 * Math.Sin(phase));
                    phase += 2 * Math.PI * 440 / 48000;
                    piece[f * 2] = piece[f * 2 + 1] = v;
                }
                sound.Push(piece);
                if (round >= 10) { sound.Pull(block); played.AddRange(block); if (round % 1000 == 0) rise.Add(sound.BufferedMs); }
            }
            Assert.Equal(0, sound.Trims);
            Assert.True(sound.Underruns <= 1, $"it ran dry {sound.Underruns} times");
            Assert.All(rise, ms => Assert.InRange(ms, 20, 120));   // what is held stays near what is wanted
            // A sine of 440 Hz changes by at most 0.047 between samples; playing it a quarter of a percent off adds nothing that could be heard as a click.
            Assert.True(BiggestStep(played.Skip(2000).ToList()) < 0.06f, $"the biggest step was {BiggestStep(played.Skip(2000).ToList())}");
        }
    }

    [Fact]
    public void An_adaptive_delay_grows_when_the_sound_runs_dry_and_comes_back_down_when_it_does_not()
    {
        var sound = new LocalSound { DelayMs = 20, MaxDelayMs = 100 };
        var block = new float[960];
        Assert.Equal(20, sound.TargetMs);
        for (int i = 0; i < 3; i++)   // pieces of 30 ms with 40 ms of playing between them: it runs dry each time
        {
            sound.Push(new float[2880]);
            for (int k = 0; k < 4; k++) sound.Pull(block);
        }
        Assert.True(sound.TargetMs >= 40, $"target {sound.TargetMs}");
        for (int i = 0; i < 100; i++) sound.Pull(block);   // silence: runs dry, grows to the limit
        Assert.True(sound.TargetMs <= 100);
        // Steady supply for a long while: it eases back.
        int before = sound.TargetMs;
        for (int round = 0; round < 4 * 6000; round++) { sound.Push(new float[960]); sound.Pull(block); }
        Assert.True(sound.TargetMs < before, $"{sound.TargetMs} vs {before}");
    }

    private static float BiggestStep(IReadOnlyList<float> played)
    {
        float biggest = 0;
        for (int i = 2; i < played.Count; i++) biggest = Math.Max(biggest, Math.Abs(played[i] - played[i - 2]));   // one frame to the next, in the same channel
        return biggest;
    }
}
