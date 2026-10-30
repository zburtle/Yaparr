using System.Runtime.InteropServices;
using Yaparr.Chat;
using Yaparr.Transport;

// A headless Yaparr node that takes one role in a network test and prints what happens as JSON lines.
//
//   Yaparr.NetAgent --role share|watch|voice --room <invite> --name <name> [--duration 60] [--port 41000]
//     share:  --kbps 1500 --fps 15 --key-every 4 [--root-fanout 3] [--unsigned]
//     watch:  [--fanout 3] [--wait-group 60]
//     voice:  [--speak] [--tone 440] [--voice-threshold 9]
//     common: [--local] (find peers through temp files on this machine, for trying it out)
//             [--turn host:port --turn-user u --turn-pass p [--turn-always]] [--max-metered-kbps 128] [--verbose]

var a = AgentArgs.Parse(args);
var name = a.Str("name", Environment.MachineName);
Out.Node = name;

if (!a.Has("role") || !a.Has("room"))
{
    Console.Error.WriteLine("usage: Yaparr.NetAgent --role share|watch|voice --room <invite> --name <name> [options]");
    return 2;
}

RoomKey key;
try { key = RoomKey.Parse(a.Str("room")); }
catch (FormatException) { Console.Error.WriteLine("The room invite is not valid."); return 2; }

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(a.Dbl("duration", 60)));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

bool local = a.Flag("local");
TurnOptions? turn = null;
if (a.Str("turn") is { Length: > 0 } server)
    turn = new TurnOptions { Server = server, Username = a.Str("turn-user"), Credential = a.Str("turn-pass"), Enabled = true, Always = a.Flag("turn-always") };

var options = new ChatNodeOptions
{
    DisplayName = name,
    ListenPort = a.Int("port", 0),
    MediaFanout = a.Int("fanout", 3),
    MediaRootFanout = a.Int("root-fanout", 3),
    MaxMeteredStreamKbps = a.Int("max-metered-kbps", 128),
    VoiceTreeThreshold = a.Int("voice-threshold", 9),
    VoiceRootFanout = a.Int("voice-root-fanout", 6),
    EnableLocalDiscovery = false,
    DiscoveryFactory = local ? mux => new LocalFileDiscovery(mux.LocalPort) : null,
    DiscoveryInterval = local ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(10),
    Traversal = new TraversalOptions { Enabled = true, PortMapping = false, Turn = turn, UseDefaultRelay = false },
    // Only the lines that explain how the others were found (or not), unless everything is asked for.
    Log = a.Flag("quiet") ? null : s => { if (a.Flag("verbose") || Interesting(s)) Out.Write("log", ("msg", s)); },
};

using var node = new ChatNode(options);
node.NetworkStatusChanged += s => Out.Write("network",
    ("nat", s.Nat.Type.ToString()), ("publicEndPoint", s.Nat.PublicEndPoint?.ToString()), ("relay", s.Relay?.ToString()),
    ("describe", s.Describe()), ("relayCapable", node.IsRelayCapable));
node.PeerConnected += p => { Found.Connected(); Out.Write("peer", ("what", "connected"), ("peer", p.Id.Short), ("name", p.DisplayName), ("total", Found.PeersNow)); };
node.PeerDisconnected += p => { Found.Disconnected(); Out.Write("peer", ("what", "disconnected"), ("peer", p.Id.Short), ("name", p.DisplayName), ("total", Found.PeersNow)); };
// For trying things out on one machine, where the network cannot say whether this node could relay.
if (a.Has("relay-capable")) node.RelayCapableOverride = a.Flag("relay-capable");
node.Start();
Out.Write("hello", ("id", node.Id.Value), ("pid", Environment.ProcessId), ("role", a.Str("role")), ("port", node.LocalPort), ("local", local));

try
{
    switch (a.Str("role").ToLowerInvariant())
    {
        case "share": await Roles.ShareAsync(node, key, a, cts.Token); break;
        case "watch": await Roles.WatchAsync(node, key, a, cts.Token); break;
        case "voice": await Roles.VoiceAsync(node, key, a, cts.Token); break;
        default: Console.Error.WriteLine("The role must be share, watch or voice."); return 2;
    }
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    Out.Write("error", ("message", ex.Message), ("kind", ex.GetType().Name));
    return 1;
}
return 0;

static bool Interesting(string line) =>
    line.StartsWith("room '") || line.Contains("DHT") || line.StartsWith("dialing ") || line.StartsWith("connected to ")
    || line.StartsWith("could not connect") || line.StartsWith("network:") || line.Contains("published our address") || line.Contains("relay");
