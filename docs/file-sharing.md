# File sharing

[Documentation](README.md)

Any file, any size, in a room channel or a direct chat. **Nothing is sent until someone chooses Download**; the file then moves peer to peer, from the sender and from every member that already has parts of it. It never goes through Yaparr Cloud, so Cloud's limits do not apply to it. It survives dropped connections, lost packets and restarts without starting over.

This is separate from [pictures](pictures-and-gifs.md), which stay small (8 MB) and download by themselves.

## What goes in the message

The message carries a small **offer** (behind a marker, like an attachment, so it is covered by the message's signature): the file's name, its size, the size of its pieces, and a **root hash**. Nothing else; the file is not touched or copied.

- The file is cut into **pieces** of 1 MiB (the offer says the size, 256 KiB to 16 MiB, so a future version can choose better). Each piece has a SHA-256.
- The **root** is the SHA-256 of a header (the size and the piece size) followed by every piece's hash. So the root fixes the size, the piece size and the content of every piece. Anyone who has the offer can check any list of piece hashes against it, and any piece against that list.
- Sending a file means hashing it first (in the background, with progress), because the root has to be in the message. The sender keeps the file **where it is**; it is read when someone downloads. If it was moved or changed, a piece that does not match its hash is never sent, and the sender says it does not have it.
- A file is announced once for a room channel or a chat; the same file shared again is the same root.

## Getting it

1. **Interest.** On Download, the downloader tells everyone it is connected to who could hold the file (the room's members, or the other person and their devices in a direct chat) that it wants this root. A holder answers with what it has.
2. **Piece list.** The downloader asks a holder for the list of piece hashes (in blocks of up to 4096 hashes), and checks the whole list against the root before it trusts any of it.
3. **Pieces.** It asks holders for pieces, a few at a time from each (`FileRequestPipeline`), and writes each verified piece into a partial file. A holder sends blocks of 16 KiB. A piece that fails its hash is thrown away, the holder that sent it is given a strike (and dropped from this download after three), and the piece is asked for again from someone else.
4. **Becoming a source.** As soon as a piece is verified it is offered to the other downloaders. A member that is still downloading serves what it has, so the sender does not have to send everyone everything.
5. **Done.** When every piece is in, the file is moved to where the person chose, and it stays a source for as long as it stays there.

## Branching and relaying

The same ideas as [streams](streams-and-large-rooms.md) and [relaying](relaying.md), for something that is not live:

- **Slots.** A member serves only a few downloaders at a time (`ChatNode.FileUploadSlots`: from its measured upload, 2 to 6, like a stream's fan-out, 3 until it is measured; a place is free again after 3 seconds without a request from its holder). A downloader past the limit is told *busy*, with up to three members it knows have pieces, and goes to them. With a room of twenty, the sender feeds a handful and they feed the rest, so the sender's upload is not the limit.
- **Who is asked first.** Holders are ranked by what each has delivered so far (bytes a second, smoothed), and a piece that only few holders have is asked for first, so the pieces spread out and no one holds all of what the others lack. Members that can relay (`IsRelayCapable`) rank above those that cannot when nothing else tells them apart.
- **Connections.** Files travel over the ordinary peer connections, so a connection that is relayed through a member, or moved to a standby path, carries a transfer without the transfer noticing. Connections that run through the shared TURN server are not used for files unless the person allows it, because its bandwidth is limited (it is "metered" as for pictures).
- **Rate.** What a member sends is capped by `ChatNodeOptions.FileBytesPerSecond` (bytes a second over all files; 0, the default, is no cap), and it waits while a connection still has 256 KB it has not sent, so a big file never piles up in memory. The file channel has a window four times the usual on a direct path (`PeerConnection.WindowFor`), which is what lets one connection carry more than about 13 Mbit/s at 40 ms.

## Reliability

- Everything that is verified is kept: the partial file lives under `files/<root>/` in the app data folder with a record of which pieces are in (`state.json`, written after each verified piece). Closing the app, a crash or a lost connection costs at most the pieces in flight.
- A request that gets no answer within a stall time is cancelled and asked of someone else; a holder that disconnects has its requests handed to the others at once, and when it comes back it is asked again. When no holder is connected the download waits (state *waiting for someone who has it*) and carries on when one appears, with no timeout.
- A piece is always asked for whole but received in blocks, so a piece cut in two by a disconnect keeps the blocks that did arrive.
- The piece list and every piece are checked against the root, so a holder cannot make up or change content. A holder cannot make the downloader hold more than a piece list and the pieces in flight.
- Requests are only answered for a file whose offer was posted where the asker is: a member of a room (not banned in it) in which this offer was seen, the other person's devices in the direct chat it was posted in, or one of our own devices. Where an offer was seen is remembered with the file (`FileScope`), and the root itself is 256 random-looking bits that only the people who read the message have.

## In code

`Yaparr.Chat.Files`: `FileOffer` (the offer and its marker), `PieceHashes` (hashing and the root), `PartialFile` (the partial file and its record), `FileWire` (the messages on the file channel), `FileDownload` (the scheduler), and `FileServer` (answering). `ChatNode.ShareFileAsync` (hashes the file and remembers where it is; the app then sends `offer.ToText(caption)` as an ordinary message), `StartFileDownload(message, destination)`, `PauseFile`, `ResumeFile`, `CancelFile`, `HasFile`, `FilePath` and the `FileTransferChanged` event are the API for the apps; `ChatMessage.File` is the offer a message carries. `ChatNodeOptions.FilesDirectory` is where records and the parts of files being downloaded are kept (`files.json`, `hashes/` and `partial/<root>/`); without it file sharing is off. Shared files and downloads under way are remembered across restarts, and a restart carries downloads on by itself.

## In the apps

- **Computer.** The 📄 button next to the paperclip offers any file in the open room or chat (with what is typed as its caption); the file is read once to get it ready, and stays where it is. A file someone offered shows in the message as a card with its name and size and a **Download** button, which asks where to save it. While it comes the card shows the percentage, speed, time left and how many people it comes from, with **Pause**, **Resume** and **Cancel** (cancel throws away what had arrived). When it is done the card says so and has **Show in folder**. There is deliberately no "Open": a program someone sent would run.
- **Phone.** The **+** button now asks for **Picture or GIF** or **File of any size**. A file is chosen with Android's own file chooser, which hands over an address and not the bytes (`ChatNodeOptions.OpenSharedFile` reads it through the app, and the app asks Android to keep the permission so the file can still be sent after the app is closed). A download is kept in the app's own folder for received files (`Android/data/org.yaparr.android/files/received`), which needs no permission and is where it is given on to others from; **Save a copy** puts a copy somewhere the person picks, in pieces. Before a download starts the phone checks that there is room for it.
- Both show the offer in the chat list and in notifications as "File: name" and "Sent a file: name".
- The words (sizes, speeds, the status line) are `FileTexts` in `Yaparr.AppCore`, shared by both apps and tested.
- Files are not part of a backup: the `files` folder is not in the list of folders that are backed up, and downloads in progress and the record of what is shared stay on the device.

## Not done yet

- Tried on screen: the phone (offering a 25 MB file from the file chooser on an emulator) and the computer app starting up. The receiving side's card and a real transfer between a computer and a phone have not been tried by eye, because the emulator cannot connect to the computer (see [Connecting across networks](connecting-across-networks.md)); the transfer itself is covered by the tests.
- No drag and drop of files onto the computer window, and no sending of several files at once.
- On the phone a file from some cloud providers (read through a pipe, without a length) cannot be shared; it says so.
- Measuring real transfers. The window of the file channel is 256 segments (about 250 KB in flight) on a direct path, a guess that should be tried over real networks, together with the number of pieces asked of a holder at once (4) and a congestion response (a lossy link keeps its window).
- The last pieces of a download wait for their holder; asking a second holder for the same piece near the end would shave the tail.
- Files whose offer was seen in a room before this version have no remembered place until the message is seen again (it is, when the room's history is opened).
