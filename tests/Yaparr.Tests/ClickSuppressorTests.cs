using Yaparr.Voice;

namespace Yaparr.Tests;

// The click suppressor on made-up signals: room hiss, short sharp clicks on top of it, and a sustained sound that must be left alone.
public class ClickSuppressorTests
{
    private const int Rate = ClickSuppressor.SampleRate, Frame = 960, Delay = ClickSuppressor.DelaySamples;

    private static float[] Hiss(int seconds, double sigma, int seed)
    {
        var rng = new Random(seed);
        var x = new float[Rate * seconds];
        for (int i = 0; i < x.Length; i++)
        {
            double g = 0;
            for (int k = 0; k < 12; k++) g += rng.NextDouble();
            x[i] = (float)((g - 6) * sigma);
        }
        return x;
    }

    // A click: a few milliseconds of loud crackle that dies away quickly.
    private static void AddClick(float[] x, int at, int length, double amplitude, Random rng)
    {
        for (int j = 0; j < length; j++)
            x[at + j] += (float)(amplitude * (rng.NextDouble() * 2 - 1) * (1 - j / (double)length));
    }

    // Feeds the input in frames the way the voice session does, and returns what comes out (the same length).
    private static float[] Run(float[] input)
    {
        var suppressor = new ClickSuppressor();
        var output = new float[input.Length];
        var frame = new float[Frame];
        for (int at = 0; at < input.Length; at += Frame)
        {
            Array.Copy(input, at, frame, 0, Frame);
            suppressor.Process(frame);
            Array.Copy(frame, 0, output, at, Frame);
        }
        return output;
    }

    private static double Db(double ratio) => 10 * Math.Log10(ratio);

    // The output lags the input by the delay: output[i + Delay] is input[i].
    private static double Energy(float[] x, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) sum += (double)x[i] * x[i];
        return sum;
    }

    [Fact]
    public void Clicks_on_room_hiss_are_taken_out()
    {
        var rng = new Random(7);
        var input = Hiss(3, 0.003, 1);
        var clicks = new List<int>();
        for (int k = 0; k < 6; k++)
        {
            int at = Rate / 2 + k * Rate / 4;
            clicks.Add(at);
            AddClick(input, at, 3 * Rate / 1000, 0.3, rng);
        }

        var output = Run(input);

        foreach (int at in clicks)
        {
            int from = at, to = at + 4 * Rate / 1000;          // the click, in the input
            double before = Energy(input, from, to);
            double after = Energy(output, from + Delay, to + Delay);
            Assert.True(Db(after / before) < -15, $"click at {at} only lost {-Db(after / before):0.0} dB");
        }
    }

    [Fact]
    public void Sustained_sound_is_left_alone_even_with_a_sharp_start()
    {
        var input = Hiss(2, 0.003, 2);
        int start = Rate / 2, length = 300 * Rate / 1000;
        var rng = new Random(3);
        for (int j = 0; j < length; j++)
            input[start + j] += (float)(0.2 * Math.Sin(2 * Math.PI * 200 * j / Rate) * (0.9 + 0.1 * rng.NextDouble()));

        var output = Run(input);

        int from = start + 40 * Rate / 1000, to = start + length - 40 * Rate / 1000;   // away from the edges
        double change = Db(Energy(output, from + Delay, to + Delay) / Energy(input, from, to));
        Assert.True(Math.Abs(change) < 1, $"a sustained sound changed by {change:0.0} dB");
    }

    [Fact]
    public void Room_hiss_alone_is_not_changed()
    {
        var input = Hiss(2, 0.003, 4);
        var output = Run(input);

        int from = Rate / 4, to = input.Length - Delay - Rate / 4;
        double change = Db(Energy(output, from + Delay, to + Delay) / Energy(input, from, to));
        Assert.True(Math.Abs(change) < 0.5, $"hiss changed by {change:0.0} dB");
    }

    [Fact]
    public void The_output_is_the_input_ten_milliseconds_later()
    {
        var input = Hiss(1, 0.003, 5);
        var output = Run(input);

        for (int i = 0; i < Delay; i++) Assert.Equal(0f, output[i]);
        for (int i = 0; i + Delay < input.Length; i++)
            Assert.True(Math.Abs(output[i + Delay] - input[i]) < 1e-6f, $"sample {i} differs");
    }
}
