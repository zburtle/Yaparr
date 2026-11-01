# Getting started

[Documentation](README.md)

Yaparr is a .NET library for serverless chat, voice and screen sharing, and a desktop app built on it. There is no server to run: peers find each other on the BitTorrent mainline DHT and connect directly. This page is for using the library from code; the app is described in [The desktop app](desktop-app.md).

## Use the library

```csharp
using var node = new ChatNode(new ChatNodeOptions { DisplayName = "zach" });
node.Start();
Console.WriteLine(node.Id);                       // share this id; others can call you by it

var key = RoomKey.Generate();                     // a room is its key; share key.ToString() with the people you want in
var room = await node.JoinRoomAsync(key, "my room");
room.MessageReceived += m => Console.WriteLine($"{m.From.DisplayName}: {m.Text}");
await room.SendAsync("hello");

var dm = await node.OpenDirectChatAsync(new PeerId("…"));   // one-to-one by peer id
await dm.SendAsync("hi");
```

## Try the command line

Try it: `dotnet run --project samples/Yaparr.Cli -- alice room` prints an invite; `dotnet run --project samples/Yaparr.Cli -- bob room <invite>` joins it, in two terminals or on two machines.
Run tests: `dotnet test`.

To debug the command line, see [Development](development.md).

## Where next

- [How it works](architecture.md) for discovery, hole punching and the layers.
- [Rooms and messages](rooms-and-messages.md) for what a room does with a message.
- [Voice channels](voice.md), [Soundboard](soundboard.md) and [Screen sharing](screen-sharing.md) for the media features.
