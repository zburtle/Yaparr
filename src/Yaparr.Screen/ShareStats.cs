using System.Diagnostics;

namespace Yaparr.Screen;

/// <summary>
/// Everything a share counts about itself beyond pictures and bytes, so that a log or a report can say what the encoder and the key frames were
/// doing, not only that something was slow. All figures are totals since the share began; whoever wants a minute subtracts the earlier ones
/// (<see cref="ShareFigures.Since"/>). The largest values are kept for each of two readers (the sharing panel and the log) so that each can start
/// afresh without taking them from the other (<see cref="ShareStats.TakeMaxima"/>).
/// </summary>
public sealed class ShareStats
{
    private long _keys, _keyBytes, _deltas, _deltaBytes, _encodes, _encodeTicks, _overBudget, _reads, _readTicks, _answers, _answerMs;
    private long _keyMaxEver, _deltaMaxEver, _encodeMaxEver, _readMaxEver, _answerMaxEver;
    private readonly long[] _keyMax = new long[2], _deltaMax = new long[2], _encodeMax = new long[2], _readMax = new long[2], _answerMax = new long[2];

    private static void Raise(ref long slot, long value)
    {
        long seen;
        while (value > (seen = Interlocked.Read(ref slot)) && Interlocked.CompareExchange(ref slot, value, seen) != seen) { }
    }

    private static void Raise(long[] slots, long value) { for (int i = 0; i < slots.Length; i++) Raise(ref slots[i], value); }

    /// <summary>A frame was sent: its size says how much a key frame costs compared with the others.</summary>
    public void Frame(int bytes, bool key)
    {
        if (key)
        {
            Interlocked.Increment(ref _keys);
            Interlocked.Add(ref _keyBytes, bytes);
            Raise(ref _keyMaxEver, bytes);
            Raise(_keyMax, bytes);
        }
        else
        {
            Interlocked.Increment(ref _deltas);
            Interlocked.Add(ref _deltaBytes, bytes);
            Raise(ref _deltaMaxEver, bytes);
            Raise(_deltaMax, bytes);
        }
    }

    /// <summary>One picture was encoded in this program; <paramref name="overBudget"/> when it took longer than the time between pictures.</summary>
    public void Encoded(long ticks, bool overBudget)
    {
        Interlocked.Increment(ref _encodes);
        Interlocked.Add(ref _encodeTicks, ticks);
        if (overBudget) Interlocked.Increment(ref _overBudget);
        Raise(ref _encodeMaxEver, ticks);
        Raise(_encodeMax, ticks);
    }

    /// <summary>The time spent waiting for the next raw picture from the capture: long waits mean the capture, not the encoder, is the slow part.</summary>
    public void Waited(long ticks)
    {
        Interlocked.Increment(ref _reads);
        Interlocked.Add(ref _readTicks, ticks);
        Raise(ref _readMaxEver, ticks);
        Raise(_readMax, ticks);
    }

    /// <summary>A key frame that a viewer asked for went out, this many milliseconds after the request arrived.</summary>
    public void Answered(long ms)
    {
        Interlocked.Increment(ref _answers);
        Interlocked.Add(ref _answerMs, ms);
        Raise(ref _answerMaxEver, ms);
        Raise(_answerMax, ms);
    }

    public ShareFigures Figures(KeyFramePacer pacer, int restarts) => new(
        Interlocked.Read(ref _keys), Interlocked.Read(ref _keyBytes), Interlocked.Read(ref _deltas), Interlocked.Read(ref _deltaBytes),
        Interlocked.Read(ref _encodes), Interlocked.Read(ref _encodeTicks), Interlocked.Read(ref _overBudget), Interlocked.Read(ref _reads), Interlocked.Read(ref _readTicks),
        pacer.Requested, pacer.Forced, pacer.Deferred, pacer.Satisfied, Interlocked.Read(ref _answers), Interlocked.Read(ref _answerMs), restarts,
        Interlocked.Read(ref _keyMaxEver), Interlocked.Read(ref _deltaMaxEver), Interlocked.Read(ref _encodeMaxEver), Interlocked.Read(ref _readMaxEver), Interlocked.Read(ref _answerMaxEver));

    /// <summary>The largest values since this reader (0 or 1) last asked, and starts again from nothing.</summary>
    public ShareMaxima TakeMaxima(int reader) => new(
        Interlocked.Exchange(ref _keyMax[reader], 0), Interlocked.Exchange(ref _deltaMax[reader], 0), Interlocked.Exchange(ref _encodeMax[reader], 0),
        Interlocked.Exchange(ref _readMax[reader], 0), Interlocked.Exchange(ref _answerMax[reader], 0));
}

/// <summary>The largest values over a period: the key frame and the other frames in bytes, encoding and waiting in ticks (100 ns), answering in milliseconds.</summary>
public readonly record struct ShareMaxima(long KeyBytes, long DeltaBytes, long EncodeTicks, long WaitTicks, long AnswerMs)
{
    public double EncodeMs => EncodeTicks / (double)TimeSpan.TicksPerMillisecond;
    public double WaitMs => WaitTicks / (double)TimeSpan.TicksPerMillisecond;
}

/// <summary>Totals since a share began (see <see cref="ShareStats"/>).</summary>
public readonly record struct ShareFigures(
    long Keys, long KeyBytes, long Deltas, long DeltaBytes, long Encodes, long EncodeTicks, long OverBudget, long Reads, long ReadTicks,
    long KeyRequests, long KeysForced, long KeyRequestsHeldBack, long KeyRequestsMetByRegularKey, long Answers, long AnswerMs, int Restarts,
    long KeyMaxEver, long DeltaMaxEver, long EncodeMaxTicksEver, long WaitMaxTicksEver, long AnswerMaxMsEver)
{
    /// <summary>What happened between <paramref name="before"/> and now (the largest values are not totals, so they are left as they are).</summary>
    public ShareFigures Since(ShareFigures before) => this with
    {
        Keys = Keys - before.Keys, KeyBytes = KeyBytes - before.KeyBytes, Deltas = Deltas - before.Deltas, DeltaBytes = DeltaBytes - before.DeltaBytes,
        Encodes = Encodes - before.Encodes, EncodeTicks = EncodeTicks - before.EncodeTicks, OverBudget = OverBudget - before.OverBudget,
        Reads = Reads - before.Reads, ReadTicks = ReadTicks - before.ReadTicks, KeyRequests = KeyRequests - before.KeyRequests,
        KeysForced = KeysForced - before.KeysForced, KeyRequestsHeldBack = KeyRequestsHeldBack - before.KeyRequestsHeldBack,
        KeyRequestsMetByRegularKey = KeyRequestsMetByRegularKey - before.KeyRequestsMetByRegularKey, Answers = Answers - before.Answers,
        AnswerMs = AnswerMs - before.AnswerMs, Restarts = Restarts - before.Restarts,
    };

    private static double Kb(double bytes) => bytes / 1024.0;
    private static double Ms(double ticks) => ticks / TimeSpan.TicksPerMillisecond;

    public double KeyAverageKb => Keys == 0 ? 0 : Kb(KeyBytes / (double)Keys);
    public double DeltaAverageKb => Deltas == 0 ? 0 : Kb(DeltaBytes / (double)Deltas);
    public double EncodeAverageMs => Encodes == 0 ? 0 : Ms(EncodeTicks / (double)Encodes);
    public double WaitAverageMs => Reads == 0 ? 0 : Ms(ReadTicks / (double)Reads);
    public double AnswerAverageMs => Answers == 0 ? 0 : AnswerMs / (double)Answers;

    /// <summary>One line about the key frames: how many, how big against the others, and what became of the requests for them.</summary>
    public string DescribeKeys(ShareMaxima? maxima = null)
    {
        double keyMax = Kb(maxima?.KeyBytes ?? KeyMaxEver), deltaMax = Kb(maxima?.DeltaBytes ?? DeltaMaxEver);
        double answerMax = maxima?.AnswerMs ?? AnswerMaxMsEver;
        return $"{Keys} key frames ({KeysForced} made because a viewer asked), key frame {KeyAverageKb:0} KB on average and {keyMax:0} KB at most, other frames {DeltaAverageKb:0.0} KB and {deltaMax:0} KB at most"
               + $"; requests from viewers {KeyRequests}: {KeysForced} answered with a new key frame{(Answers > 0 ? $" (after {AnswerAverageMs:0} ms on average, {answerMax:0} ms at most)" : "")}, {KeyRequestsHeldBack} held back for a moment, {KeyRequestsMetByRegularKey} met by a regular key frame";
    }

    /// <summary>One line about the encoder in this program; empty when none was used.</summary>
    public string DescribeEncoding(double budgetMs, ShareMaxima? maxima = null)
    {
        if (Encodes == 0) return "";
        double encodeMax = maxima?.EncodeMs ?? Ms(EncodeMaxTicksEver), waitMax = maxima?.WaitMs ?? Ms(WaitMaxTicksEver);
        return $"encoding {EncodeAverageMs:0.0} ms per picture on average and {encodeMax:0.0} ms at most ({OverBudget} of {Encodes} over the {budgetMs:0.0} ms there is for each), "
               + $"waiting for the capture {WaitAverageMs:0.0} ms on average and {waitMax:0.0} ms at most";
    }
}
