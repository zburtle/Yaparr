using System.Net;
using System.Net.Sockets;
using System.Text;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Tests;

/// <summary>A TURN server with an allocation per client. Only packets that come from another allocation's relay address are forwarded, so no direct path exists.</summary>
internal sealed class FakeTurnHub : IDisposable
{
    private const string Realm = "yaparr.test";
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _key;
    private readonly string _user;
    private readonly Dictionary<IPEndPoint, UdpClient> _relays = new();

    public FakeTurnHub(string user, string password)
    {
        _user = user;
        _key = StunMessage.LongTermKey(user, Realm, password);
        _ = Task.Run(ServeAsync);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_socket.Client.LocalEndPoint!;

    private bool IsRelay(IPEndPoint ep)
    {
        lock (_relays) return _relays.Values.Any(r => ((IPEndPoint)r.Client.LocalEndPoint!).Equals(ep));
    }

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                var r = await _socket.ReceiveAsync(_cts.Token);
                var msg = StunMessage.Decode(r.Buffer);
                if (msg == null) continue;

                if (msg.Type == StunType.SendIndication)
                {
                    UdpClient? relay;
                    lock (_relays) _relays.TryGetValue(r.RemoteEndPoint, out relay);
                    var peer = msg.GetXorAddress(StunAttr.XorPeerAddress);
                    var data = msg.Get(StunAttr.Data);
                    if (relay != null && peer != null && data != null) await relay.SendAsync(data, peer);
                    continue;
                }

                var failure = (ushort)(msg.Type | 0x0110);
                if (msg.GetString(StunAttr.Username) != _user || !StunMessage.VerifyIntegrity(r.Buffer, _key))
                {
                    var error = new StunMessage(failure, msg.TransactionId)
                        .Add(StunAttr.ErrorCode, StunMessage.ErrorValue(401, "Unauthorized"))
                        .Add(StunAttr.Realm, Realm)
                        .Add(StunAttr.Nonce, "nonce-1");
                    await _socket.SendAsync(error.Encode(), r.RemoteEndPoint);
                    continue;
                }

                var ok = new StunMessage((ushort)(msg.Type | 0x0100), msg.TransactionId);
                switch (msg.Type)
                {
                    case StunType.AllocateRequest:
                        var relay2 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                        lock (_relays) _relays[r.RemoteEndPoint] = relay2;
                        _ = Task.Run(() => RelayAsync(relay2, r.RemoteEndPoint));
                        ok.AddXorAddress(StunAttr.XorRelayedAddress, (IPEndPoint)relay2.Client.LocalEndPoint!)
                          .AddXorAddress(StunAttr.XorMappedAddress, r.RemoteEndPoint)
                          .Add(StunAttr.Lifetime, new byte[] { 0, 0, 2, 0x58 });
                        break;
                    case StunType.RefreshRequest:
                        ok.Add(StunAttr.Lifetime, new byte[] { 0, 0, 2, 0x58 });
                        break;
                }
                await _socket.SendAsync(ok.Encode(), r.RemoteEndPoint);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task RelayAsync(UdpClient relay, IPEndPoint client)
    {
        try
        {
            while (true)
            {
                var r = await relay.ReceiveAsync(_cts.Token);
                if (!IsRelay(r.RemoteEndPoint)) continue;
                var indication = new StunMessage(StunType.DataIndication)
                    .AddXorAddress(StunAttr.XorPeerAddress, r.RemoteEndPoint)
                    .Add(StunAttr.Data, r.Buffer);
                await _socket.SendAsync(indication.Encode(), client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
        lock (_relays) foreach (var r in _relays.Values) r.Dispose();
    }
}

public class SharedRelayTests
{
    private sealed class Side : IDisposable
    {
        public readonly PeerIdentity Identity = PeerIdentity.Generate();
        public readonly UdpMux Mux = new();
        public readonly PeerNetwork Network;
        public readonly TurnClient Turn;

        public Side(FakeTurnHub hub)
        {
            Network = new PeerNetwork(Identity, Mux, TestUtil.FastNetwork);
            Mux.Start();
            Turn = new TurnClient(Mux, new TurnOptions { Server = hub.EndPoint.ToString(), Username = "u", Credential = "pw", Enabled = true }) { RelayLocalAddresses = true };
        }

        public void Dispose() { Turn.Dispose(); Network.Dispose(); Mux.Dispose(); Identity.Dispose(); }
    }

    [Fact]
    public async Task Two_clients_on_one_server_that_only_know_each_others_relay_address_can_talk()
    {
        using var hub = new FakeTurnHub("u", "pw");
        using var a = new Side(hub);
        using var b = new Side(hub);
        Assert.True(await a.Turn.AllocateAsync());
        Assert.True(await b.Turn.AllocateAsync());
        Assert.NotEqual(a.Turn.RelayedEndPoint, b.Turn.RelayedEndPoint);

        var received = new TaskCompletionSource<string>();
        b.Network.RegisterChannel(5, (_, _, data, _) => received.TrySetResult(Encoding.UTF8.GetString(data.Span)));

        var ca = a.Network.Connect(b.Turn.RelayedEndPoint!, b.Identity.Id);
        var cb = b.Network.Connect(a.Turn.RelayedEndPoint!, a.Identity.Id);
        await Task.WhenAll(ca.WhenEstablished, cb.WhenEstablished).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(b.Identity.Id, ca.RemoteId);
        ca.SendReliable(5, Encoding.UTF8.GetBytes("through the relay"));
        Assert.Equal("through the relay", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(a.Turn.UsesRelayFor(b.Turn.RelayedEndPoint!));
    }
}
