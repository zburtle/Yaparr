# Yaparr

Serverless chat and voice calls for .NET. Peers find each other on the BitTorrent mainline DHT, then open direct, encrypted UDP connections using hole punching. There is no server of ours.

```csharp
using var node = new ChatNode(new ChatNodeOptions { DisplayName = "zach" });
node.Start();

var key = RoomKey.Generate();                     // a room is its key; share key.ToString() with the people you want in
var room = await node.JoinRoomAsync(key, "my room");
room.MessageReceived += m => Console.WriteLine($"{m.From.DisplayName}: {m.Text}");
await room.SendAsync("hello");
```

This package is the chat library (`net10.0`). Screen sharing is in the separate `Yaparr.Screen` project and needs FFmpeg at run time.

Licence: GPL-3.0-or-later, like the rest of Yaparr. Source, documentation and the desktop app: https://github.com/zburtle/Yaparr
