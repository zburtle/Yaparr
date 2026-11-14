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
    private long _keys, _keyBytes, _deltas, _deltaBytes, _encodes, _encodeTicks, _overBudget, _reads, _readTicks, _bunched, _stalled, _answers, _answerMs, _repeated;
    private long _sends, _sendTicks, _queueTicks, _queueLate, _dropped;
    private long _keyMaxEver, _deltaMaxEver, _encodeMaxEver, _readMaxEver, _answerMaxEver, _sendMaxEver, _queueMaxEver;
    private readonly long[] _keyMax = new long[2], _deltaMax = new long[2], _encodeMax = new long[2], _readMax = new long[2], _answerMax = new long[2], _sendMax = new long[2], _queueMax = new long[2];

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

    /// <summary>
    /// The time spent waiting for the next raw picture from the capture: long waits mean the capture, not the encoder, is the slow part. With
    /// <paramref name="frameTicks"/>, the time between two pictures, it also counts pictures that came in a burst (hardly any wait: the capture
    /// was catching up) and waits longer than two pictures (a stall); bursts after stalls are what makes a viewer's playback uneven.
    /// </summary>
    public void Waited(long ticks, long frameTicks)
    {
        Interlocked.Increment(ref _reads);
        Interlocked.Add(ref _readTicks, ticks);
        if (ticks * 4 < frameTicks) Interlocked.Increment(ref _bunched);
        else if (ticks > frameTicks * 2) Interlocked.Increment(ref _stalled);
        Raise(ref _readMaxEver, ticks);
        Raise(_readMax, ticks);
    }

    /// <summary>The share's own clock came round and the capture had nothing new, so the last picture was encoded again (capture in this program only).</summary>
    public void Repeated() => Interlocked.Increment(ref _repeated);

    /// <summary>
    /// A frame went out: <paramref name="sendTicks"/> is how long sending it took (its packets are spread out at a multiple of the bitrate, so a big frame takes
    /// longer), and <paramref name="queuedTicks"/> how long it waited behind the frames before it. Waits longer than <paramref name="frameTicks"/>, the time between
    /// two pictures, are counted: they are the moments when sending, not encoding, held the stream back.
    /// </summary>
    public void Sent(long sendTicks, long queuedTicks, long frameTicks)
    {
        Interlocked.Increment(ref _sends);
        Interlocked.Add(ref _sendTicks, sendTicks);
        Interlocked.Add(ref _queueTicks, queuedTicks);
        if (queuedTicks > frameTicks) Interlocked.Increment(ref _queueLate);
        Raise(ref _sendMaxEver, sendTicks);
        Raise(_sendMax, sendTicks);
        Raise(ref _queueMaxEver, queuedTicks);
        Raise(_queueMax, queuedTicks);
    }

    /// <summary>Frames thrown away because sending fell more than a second behind (the queue is emptied and the next key frame is waited for).</summary>
    public void DroppedFromQueue(int frames) => Interlocked.Add(ref _dropped, frames);

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
        Interlocked.Read(ref _encodes), Interlocked.Read(ref _encodeTicks), Interlocked.Read(ref _overBudget), Interlocked.Read(ref _reads), Interlocked.Read(ref _readTicks), Interlocked.Read(ref _bunched), Interlocked.Read(ref _stalled),
        pacer.Requested, pacer.Forced, pacer.Deferred, pacer.Satisfied, Interlocked.Read(ref _answers), Interlocked.Read(ref _answerMs), restarts,
        Interlocked.Read(ref _keyMaxEver), Interlocked.Read(ref _deltaMaxEver), Interlocked.Read(ref _encodeMaxEver), Interlocked.Read(ref _readMaxEver), Interlocked.Read(ref _answerMaxEver),
        Interlocked.Read(ref _repeated),
        Interlocked.Read(ref _sends), Interlocked.Read(ref _sendTicks), Interlocked.Read(ref _queueTicks), Interlocked.Read(ref _queueLate), Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _sendMaxEver), Interlocked.Read(ref _queueMaxEver));

    /// <summary>The largest values since this reader (0 or 1) last asked, and starts again from nothing.</summary>
    public ShareMaxima TakeMaxima(int reader) => new(
        Interlocked.Exchange(ref _keyMax[reader], 0), Interlocked.Exchange(ref _deltaMax[reader], 0), Interlocked.Exchange(ref _encodeMax[reader], 0),
        Interlocked.Exchange(ref _readMax[reader], 0), Interlocked.Exchange(ref _answerMax[reader], 0),
        Interlocked.Exchange(ref _sendMax[reader], 0), Interlocked.Exchange(ref _queueMax[reader], 0));
}

/// <summary>The largest values over a period: the key frame and the other frames in bytes, encoding, waiting, sending and queueing in ticks (100 ns), answering in milliseconds.</summary>
public readonly record struct ShareMaxima(long KeyBytes, long DeltaBytes, long EncodeTicks, long WaitTicks, long AnswerMs, long SendTicks = 0, long QueueTicks = 0)
{
    public double EncodeMs => EncodeTicks / (double)TimeSpan.TicksPerMillisecond;
    public double WaitMs => WaitTicks / (double)TimeSpan.TicksPerMillisecond;
    public double SendMs => SendTicks / (double)TimeSpan.TicksPerMillisecond;
    public double QueueMs => QueueTicks / (double)TimeSpan.TicksPerMillisecond;
}

/// <summary>Totals since a share began (see <see cref="ShareStats"/>).</summary>
public readonly record struct ShareFigures(
    long Keys, long KeyBytes, long Deltas, long DeltaBytes, long Encodes, long EncodeTicks, long OverBudget, long Reads, long ReadTicks, long Bunched, long Stalled,
    long KeyRequests, long KeysForced, long KeyRequestsHeldBack, long KeyRequestsMetByRegularKey, long Answers, long AnswerMs, int Restarts,
    long KeyMaxEver, long DeltaMaxEver, long EncodeMaxTicksEver, long WaitMaxTicksEver, long AnswerMaxMsEver, long Repeated = 0,
    long Sends = 0, long SendTicks = 0, long QueueTicks = 0, long QueueLate = 0, long Dropped = 0, long SendMaxTicksEver = 0, long QueueMaxTicksEver = 0)
{
    /// <summary>What happened between <paramref name="before"/> and now (the largest values are not totals, so they are left as they are).</summary>
    public ShareFigures Since(ShareFigures before) => this with
    {
        Keys = Keys - before.Keys, KeyBytes = KeyBytes - before.KeyBytes, Deltas = Deltas - before.Deltas, DeltaBytes = DeltaBytes - before.DeltaBytes,
        Encodes = Encodes - before.Encodes, EncodeTicks = EncodeTicks - before.EncodeTicks, OverBudget = OverBudget - before.OverBudget,
        Reads = Reads - before.Reads, ReadTicks = ReadTicks - before.ReadTicks, Bunched = Bunched - before.Bunched, Stalled = Stalled - before.Stalled, KeyRequests = KeyRequests - before.KeyRequests,
        KeysForced = KeysForced - before.KeysForced, KeyRequestsHeldBack = KeyRequestsHeldBack - before.KeyRequestsHeldBack,
        KeyRequestsMetByRegularKey = KeyRequestsMetByRegularKey - before.KeyRequestsMetByRegularKey, Answers = Answers - before.Answers,
        AnswerMs = AnswerMs - before.AnswerMs, Restarts = Restarts - before.Restarts, Repeated = Repeated - before.Repeated,
        Sends = Sends - before.Sends, SendTicks = SendTicks - before.SendTicks, QueueTicks = QueueTicks - before.QueueTicks, QueueLate = QueueLate - before.QueueLate,
        Dropped = Dropped - before.Dropped,
    };

    private static double Kb(double bytes) => bytes / 1024.0;
    private static double Ms(double ticks) => ticks / TimeSpan.TicksPerMillisecond;

    public double KeyAverageKb => Keys == 0 ? 0 : Kb(KeyBytes / (double)Keys);
    public double DeltaAverageKb => Deltas == 0 ? 0 : Kb(DeltaBytes / (double)Deltas);
    public double EncodeAverageMs => Encodes == 0 ? 0 : Ms(EncodeTicks / (double)Encodes);
    public double WaitAverageMs => Reads == 0 ? 0 : Ms(ReadTicks / (double)Reads);
    public double AnswerAverageMs => Answers == 0 ? 0 : AnswerMs / (double)Answers;
    public double SendAverageMs => Sends == 0 ? 0 : Ms(SendTicks / (double)Sends);
    public double QueueAverageMs => Sends == 0 ? 0 : Ms(QueueTicks / (double)Sends);

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
               + $"waiting for the capture {WaitAverageMs:0.0} ms on average and {waitMax:0.0} ms at most, {Stalled} waits of more than two pictures and {Bunched} pictures that came in a burst straight after another"
               + (Repeated > 0 ? $", {Repeated} pictures repeated because the capture had no new one" : "")
               + (Sends > 0 ? "; " + DescribeSending(budgetMs, maxima) : "");
    }

    /// <summary>
    /// One part about sending: how long a frame took to go out (its packets are spread out, so a big frame takes longer), how long frames waited behind others,
    /// and how many were thrown away because sending fell more than a second behind. Sending has a thread of its own, so it holds back the stream, never the encoder.
    /// </summary>
    public string DescribeSending(double budgetMs, ShareMaxima? maxima = null)
    {
        double sendMax = maxima?.SendMs ?? Ms(SendMaxTicksEver), queueMax = maxima?.QueueMs ?? Ms(QueueMaxTicksEver);
        return $"sending {SendAverageMs:0.0} ms per frame on average and {sendMax:0.0} ms at most, "
               + $"waiting to be sent {QueueAverageMs:0.0} ms on average and {queueMax:0.0} ms at most ({QueueLate} of {Sends} waited longer than the {budgetMs:0.0} ms between pictures)"
               + (Dropped > 0 ? $", {Dropped} frames thrown away when sending fell more than a second behind" : "");
    }
}
