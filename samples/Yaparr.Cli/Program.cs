using Yaparr.Chat;
using Yaparr.Identity;

bool local = args.Contains("--local");
bool keepHistory = args.Contains("--history");
args = args.Where(a => !a.StartsWith("--")).ToArray();

if (args.Length < 2)
{
    Console.WriteLine("usage: Yaparr.Cli [--local] [--history] <display-name> room <name> | private [invite] | dm <peer-id> | wait");
    return;
}

using var node = new ChatNode(new ChatNodeOptions
{
    DisplayName = args[0],
    Log = s => Console.WriteLine($"  [{s}]"),
    DiscoveryFactory = local ? mux => new LocalFileDiscovery(mux.LocalPort) : null,
    HistoryStore = keepHistory ? new FileHistoryStore(Path.Combine(Path.GetTempPath(), "yaparr-cli-history", args[0])) : null,
    DiscoveryInterval = local ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(15),
});
node.PeerConnected += p => Console.WriteLine($"* {p.DisplayName} ({p.Id.Short}) connected from {p.EndPoint}");
node.PeerDisconnected += p => Console.WriteLine($"* {p.DisplayName} disconnected");
node.IncomingDirectChat += c => c.MessageReceived += Print;
node.Start();
Console.WriteLine($"Your peer id: {node.Id}");
Console.WriteLine(local ? "Local discovery (temp-dir files, loopback only)." : "Bootstrapping into the DHT...");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Func<string, Task> send;
switch (args[1])
{
    case "room":
        var room = await node.JoinRoomAsync(args[2], cts.Token);
        room.MessageReceived += Print;
        room.MemberJoined += p => Console.WriteLine($"* {p.DisplayName} joined");
        room.MemberLeft += p => Console.WriteLine($"* {p.DisplayName} left");
        send = room.SendAsync;
        Console.WriteLine($"Joined '{room.Name}'. Type to chat, Ctrl+C to quit.");
        break;
    case "private":
        var key = args.Length > 2 ? RoomKey.Parse(args[2]) : RoomKey.Generate();
        Console.WriteLine($"Room invite (share privately): {key}");
        var priv = await node.JoinPrivateRoomAsync(key, ct: cts.Token);
        priv.MessageReceived += Print;
        priv.MemberJoined += p => Console.WriteLine($"* {p.DisplayName} joined");
        priv.MemberLeft += p => Console.WriteLine($"* {p.DisplayName} left");
        send = priv.SendAsync;
        Console.WriteLine("Joined private room. Type to chat, Ctrl+C to quit.");
        break;
    case "dm":
        Console.WriteLine("Looking for peer (this can take a minute)...");
        var chat = await node.OpenDirectChatAsync(new PeerId(args[2]), cts.Token);
        chat.MessageReceived += Print;
        send = chat.SendAsync;
        Console.WriteLine("Connected. Type to chat, Ctrl+C to quit.");
        break;
    default:
        send = _ => Task.CompletedTask;
        Console.WriteLine("Waiting for incoming chats. Ctrl+C to quit.");
        break;
}

try
{
    while (!cts.IsCancellationRequested)
    {
        var line = await Task.Run(Console.ReadLine, cts.Token);
        if (line == null) break;
        if (line.Length > 0) await send(line);
    }
}
catch (OperationCanceledException) { }

static void Print(ChatMessage m) =>
    Console.WriteLine($"[{(m.Room == null ? "dm" : "room")}{(m.IsCatchUp ? ", missed" : "")}] {m.Timestamp.LocalDateTime:HH:mm:ss} {m.From.DisplayName}: {m.Text}");
