using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

/// <summary>A message of the upload test between two members: a request, the go-ahead, the end of the train and the result.</summary>
internal sealed class WireSpeed
{
    /// <summary>"req" and "go" or "no" to start, "end" after the packets, "res" with what arrived.</summary>
    public string Op { get; set; } = "";
    public string Id { get; set; } = "";
    public int N { get; set; }
    public int Size { get; set; }

    /// <summary>In a result: how many packets arrived, how many bytes came after the first, and the microseconds between the first and the last.</summary>
    public int Got { get; set; }
    public long Bytes { get; set; }
    public long Us { get; set; }
}

/// <summary>What the measured upload speed of a member means for the streams it passes on.</summary>
internal static class UploadPlan
{
    public const int MinForward = 128 * 1024, MaxForward = 2 * 1024 * 1024, MaxFanout = 6;

    /// <summary>How much a member forwards for others: two fifths of what it can send, within limits. The configured number until it is measured.</summary>
    public static int ForwardBytesPerSecond(long? measured, int configured) =>
        measured is > 0 ? (int)Math.Clamp(measured.Value * 2 / 5, MinForward, MaxForward) : configured;

    /// <summary>How many members it can pass a stream of this bitrate on to. The configured number until it is measured.</summary>
    public static int Fanout(long? measured, int kbps, int configured)
    {
        if (measured is not > 0) return configured;
        double stream = Math.Max(8, kbps) * 125.0;   // kilobits per second in bytes per second
        return (int)Math.Clamp(ForwardBytesPerSecond(measured, 0) / stream, 1, MaxFanout);
    }
}

// How much this device can really send. Streams are passed on from member to member, so how many members one can serve depends on its upload,
// which is not a thing the app can read off the machine. One member sends a short burst of packets to a peer who is far enough away to
// say something about the internet connection, and the peer reports how fast they arrived. It costs about 130 KB now and then.
public sealed partial class ChatNode
{
    private const int SpeedPackets = 120, SpeedPacketBytes = 1100, SpeedMaxPackets = 200, SpeedMinPackets = 30;
    private static readonly TimeSpan SpeedWait = TimeSpan.FromSeconds(6);

    private long _measuredUpload;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WireSpeed>> _speedWaits = new();
    private readonly ConcurrentDictionary<string, SpeedTrain> _speedTrains = new();
    private readonly ConcurrentDictionary<PeerId, long> _speedLastServed = new();

    private sealed class SpeedTrain
    {
        public long First, Last;
        public int Got;
        public long Bytes;
    }

    /// <summary>What this device can send to the internet, in bytes per second, as last measured; null until it has been.</summary>
    public long? MeasuredUploadBytesPerSecond => Interlocked.Read(ref _measuredUpload) is var v and > 0 ? v : null;

    /// <summary>What this device passes on for other members, in kilobytes per second: from its measured upload, or the configured amount.</summary>
    public int RelayForwardKilobytes => ForwardBytesPerSecond / 1024;

    internal int ForwardBytesPerSecond => UploadPlan.ForwardBytesPerSecond(MeasuredUploadBytesPerSecond, _options.MediaBytesPerSecond);

    private void StartSpeedProbes()
    {
        if (!_options.MeasureUpload) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.SpeedProbeDelay, _cts.Token).ConfigureAwait(false);
                while (!_cts.IsCancellationRequested)
                {
                    // A device that does not relay has no use for the number.
                    if (IsRelayCapable) await MeasureOnceAsync(_cts.Token).ConfigureAwait(false);
                    await Task.Delay(MeasuredUploadBytesPerSecond == null ? _options.SpeedProbeRetry : _options.SpeedProbeInterval, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _options.Log?.Invoke("upload test stopped: " + ex.Message); }
        });
    }

    // Tests the connection to a couple of peers and keeps the best result: a slow peer says little about how fast we can send.
    private async Task MeasureOnceAsync(CancellationToken ct)
    {
        var peers = _peers.Values
            .Where(p => p.Connection.State == ConnectionState.Established && !p.Connection.IsRelayed && !IsMetered(p.Connection) && !IsSibling(p)
                        && (_options.SpeedProbeLocalPeers || !IsLocalAddress(p.Connection.RemoteEndPoint.Address)))
            .OrderBy(_ => Random.Shared.Next()).Take(2).ToList();
        long best = 0;
        foreach (var peer in peers)
        {
            if (await MeasureUploadAsync(peer.Id, ct).ConfigureAwait(false) is { } rate) best = Math.Max(best, rate);
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        if (best == 0) return;
        long previous = Interlocked.Read(ref _measuredUpload);
        long blended = previous > 0 ? (previous + best) / 2 : best;
        Interlocked.Exchange(ref _measuredUpload, blended);
        _options.Log?.Invoke($"upload speed measured: about {blended * 8 / 1_000_000.0:0.0} Mbit/s; passing on {ForwardBytesPerSecond / 1024} KB/s of streams");
    }

    private static bool IsLocalAddress(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (a.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    /// <summary>
    /// Sends a burst of packets to one peer and works out the speed from how closely together they arrived. Null if the peer would not take
    /// part, too few packets arrived, or it did not answer.
    /// </summary>
    internal async Task<long?> MeasureUploadAsync(PeerId peerId, CancellationToken ct)
    {
        if (!_peers.TryGetValue(peerId, out var peer) || peer.Connection.State != ConnectionState.Established || peer.Connection.IsRelayed) return null;
        var idBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
        string id = Convert.ToHexString(idBytes);
        var go = new TaskCompletionSource<WireSpeed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<WireSpeed>(TaskCreationOptions.RunContinuationsAsynchronously);
        _speedWaits[id] = go;
        _speedWaits[id + "/res"] = result;
        try
        {
            Send(peer.Connection, new Wire { Type = Wire.Speed, Spd = new WireSpeed { Op = "req", Id = id, N = SpeedPackets, Size = SpeedPacketBytes } });
            if ((await go.Task.WaitAsync(SpeedWait, ct).ConfigureAwait(false)).Op != "go") return null;

            var packet = new byte[SpeedPacketBytes];
            idBytes.CopyTo(packet, 0);
            for (int i = 0; i < SpeedPackets; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), (uint)i);
                try { peer.Connection.SendUnreliable(ChannelIds.Speed, packet); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return null; }
            }
            Send(peer.Connection, new Wire { Type = Wire.Speed, Spd = new WireSpeed { Op = "end", Id = id, N = SpeedPackets } });

            var res = await result.Task.WaitAsync(SpeedWait, ct).ConfigureAwait(false);
            if (res.Got < SpeedMinPackets || res.Us <= 0 || res.Bytes <= 0) return null;
            // The first packet only starts the clock; the bytes that arrived after it over the time they took are the speed of the narrowest link.
            return (long)Math.Clamp(res.Bytes * 1_000_000.0 / res.Us, 16 * 1024, 200L * 1024 * 1024);
        }
        catch (TimeoutException) { return null; }
        finally
        {
            _speedWaits.TryRemove(id, out _);
            _speedWaits.TryRemove(id + "/res", out _);
        }
    }

    private void OnSpeed(PeerState peer, WireSpeed m)
    {
        if (m.Id.Length != 16) return;
        switch (m.Op)
        {
            case "req":
            {
                // From someone connected straight to us only, at most now and then, and no bigger than the test needs.
                long now = Environment.TickCount64;
                bool allowed = !peer.Connection.IsRelayed && m.N is >= SpeedMinPackets and <= SpeedMaxPackets && m.Size is >= 200 and <= PeerConnection.MaxUnreliablePayload
                               && _speedTrains.Count < 4
                               && now - _speedLastServed.GetValueOrDefault(peer.Id, long.MinValue / 2) >= (long)_options.SpeedTestMinGap.TotalMilliseconds;
                if (allowed)
                {
                    _speedLastServed[peer.Id] = now;
                    _speedTrains[m.Id] = new SpeedTrain();
                    // A train that never ends is forgotten.
                    _ = Task.Delay(30_000).ContinueWith(_ => _speedTrains.TryRemove(m.Id, out SpeedTrain? _));
                }
                Send(peer.Connection, new Wire { Type = Wire.Speed, Spd = new WireSpeed { Op = allowed ? "go" : "no", Id = m.Id } });
                break;
            }
            case "end":
                if (!_speedTrains.ContainsKey(m.Id)) break;
                // Packets can still be on their way when the end arrives.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    if (!_speedTrains.TryRemove(m.Id, out var train)) return;
                    long ticks;
                    int got;
                    long bytes;
                    lock (train) { ticks = train.Last - train.First; got = train.Got; bytes = train.Bytes; }
                    Send(peer.Connection, new Wire { Type = Wire.Speed, Spd = new WireSpeed { Op = "res", Id = m.Id, Got = got, Bytes = bytes, Us = (long)(ticks * 1_000_000.0 / Stopwatch.Frequency) } });
                });
                break;
            case "go" or "no":
                if (_speedWaits.TryGetValue(m.Id, out var go)) go.TrySetResult(m);
                break;
            case "res":
                if (_speedWaits.TryGetValue(m.Id + "/res", out var res)) res.TrySetResult(m);
                break;
        }
    }

    // A packet of a train: only the arrival time and size are kept.
    private void OnSpeedData(PeerConnection conn, byte channel, ReadOnlyMemory<byte> data, bool reliable)
    {
        if (reliable || data.Length < 12) return;
        if (!_speedTrains.TryGetValue(Convert.ToHexString(data.Span[..8]), out var train)) return;
        long now = Stopwatch.GetTimestamp();
        lock (train)
        {
            if (train.Got == 0) train.First = now;
            else train.Bytes += data.Length;
            train.Last = now;
            train.Got++;
        }
    }
}
