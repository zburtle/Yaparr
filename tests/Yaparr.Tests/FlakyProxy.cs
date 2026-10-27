using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Yaparr.Tests;

/// <summary>
/// A stand-in for a bad network between two peers. Datagrams sent to <see cref="EndPoint"/> are passed to the
/// target from a different socket (so the target sees a rewritten address, like behind a NAT) and replies come back
/// the same way. It can lose, delay, reorder and duplicate datagrams, or swallow everything.
/// </summary>
internal sealed class FlakyProxy : IDisposable
{
    private readonly UdpClient _front = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly IPEndPoint _target;
    private readonly ConcurrentDictionary<IPEndPoint, UdpClient> _legs = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _random = new(1234);

    public FlakyProxy(IPEndPoint target)
    {
        _target = target;
        _ = Task.Run(() => PumpAsync(_front, null));
    }

    public IPEndPoint EndPoint => (IPEndPoint)_front.Client.LocalEndPoint!;

    /// <summary>Chance (0 to 1) that a datagram is dropped.</summary>
    public double Loss { get; set; }

    /// <summary>Chance (0 to 1) that a datagram is delivered twice.</summary>
    public double Duplicate { get; set; }

    /// <summary>Every datagram waits this long, plus up to <see cref="JitterMs"/> more, which reorders traffic.</summary>
    public int DelayMs { get; set; }

    public int JitterMs { get; set; }

    /// <summary>When set, nothing gets through in either direction.</summary>
    public volatile bool Blackhole;

    public long Forwarded;
    public long Dropped;

    private async Task PumpAsync(UdpClient socket, (IPEndPoint Client, UdpClient Front)? leg)
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await socket.ReceiveAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            if (leg is null)
            {
                var client = result.RemoteEndPoint;
                var outbound = _legs.GetOrAdd(client, c =>
                {
                    var s = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    _ = Task.Run(() => PumpAsync(s, (c, _front)));
                    return s;
                });
                Forward(result.Buffer, () => outbound.Send(result.Buffer, _target));
            }
            else
            {
                var (client, front) = leg.Value;
                Forward(result.Buffer, () => front.Send(result.Buffer, client));
            }
        }
    }

    private void Forward(byte[] data, Action send)
    {
        double roll, dupRoll;
        int wait;
        lock (_random)
        {
            roll = _random.NextDouble();
            dupRoll = _random.NextDouble();
            wait = DelayMs + (JitterMs > 0 ? _random.Next(JitterMs + 1) : 0);
        }

        if (Blackhole || roll < Loss)
        {
            Interlocked.Increment(ref Dropped);
            return;
        }

        Interlocked.Increment(ref Forwarded);
        Deliver(send, wait);
        if (dupRoll < Duplicate) Deliver(send, wait + 5);
    }

    private void Deliver(Action send, int waitMs)
    {
        if (waitMs <= 0) { Try(send); return; }
        _ = Task.Delay(waitMs, _cts.Token).ContinueWith(t => { if (!t.IsCanceled) Try(send); }, TaskScheduler.Default);
    }

    private static void Try(Action send)
    {
        try { send(); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _front.Dispose();
        foreach (var leg in _legs.Values) leg.Dispose();
    }
}
