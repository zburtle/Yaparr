# Streams and large rooms

[Documentation](README.md)

Something that many people take part in cannot be sent to each person from one machine: a screen is about 1 Mbit/s per
viewer, and a voice channel of 20 is 19 streams each way. So such a stream travels along a **tree**. The sharer (the
root) sends to a few members, and each of those passes it on to a few more. Only members of the room take part, and
the stream is announced like a voice beacon, signed by the sharer.

- **Where you attach.** A newcomer asks the root, which answers with the shallowest member who has a free place (or
  itself). The newcomer connects there and asks to attach; if that member has since filled up it says no and the
  newcomer asks the root again. Because the root decides, the tree has no loops and stays shallow. A member sends to at
  most `MediaFanout` (3) others, the sharer to `MediaRootFanout` (3), and only members whose connection can relay
  (`IsRelayCapable`) are ever used as parents. If the sharer's places are all held by members who cannot pass the stream
  on, one of them steps aside for one who can.
- **When someone leaves.** Neighbours ping each other every maintenance interval. A member whose parent goes silent
  (`MediaEdgeTimeout`, 3 s) or whose connection closes asks the root again and is placed elsewhere, with its own
  children still attached to it. A newcomer is given the packets since the last key straight away by whoever it
  attaches to, so it does not wait for the sharer to produce another.
- **What is protected.** Packets are encrypted hop by hop like everything else. In a `signed` stream the sharer signs every
  packet and a member passing it on cannot change it or add to it; the viewers check. Forwarders see the content, as they
  are viewers themselves. Each member forwards at most `MediaBytesPerSecond` (about 3 Mbit/s) for others.
- **Using it.** `node.StartMedia(room, kind, name, kbps: 1000)` returns a `MediaSource`; `room.MediaGroups` lists what is
  shared; `node.WatchMedia(room, id)` returns a `MediaView`. For small things use `Send` and the `Received` event, one
  packet each. For video use `await source.SendFrameAsync(frame, isKey)` (a frame can be up to 4 MB) and the
  `FrameReceived` event. The library does not capture or encode a screen.
- **Frames and loss.** A frame is cut into packets, spread out over time (at about four times its declared bitrate, so a
  big key frame does not arrive as one burst) and put back together by the viewer, who gets frames in order and without
  gaps. Every member keeps the last 600 or so packets. A member that sees a gap asks its parent for the missing
  packets, every 80 ms until it comes, so one lost packet costs a round trip and not a key frame; after the last frame, two tiny
  probe packets show what was missing at the end. A frame that is still incomplete after 0.4 seconds counts as
  lost: the viewer drops everything until the next key frame and asks for one. The request goes up the tree, and the
  first member whose copy runs without a hole from the key to now replays it (paced; the receiver is told which numbers to expect and asks for any that do not come, while the replay is still
  arriving);
  only if nobody has one, the sharer gets `KeyFrameRequested` (at most once a second) and should send a key frame. A
  viewer that joins late is given the same copy at once. A replay starts again from the key, so the viewer is handed
  every frame since it, quickly; it is a catch-up, not a jump to the present.
- **The shared server.** The TURN server's bandwidth is limited, so a stream that declares more than
  `MaxMeteredStreamKbps` (128) is not carried over a connection that runs through it. A member who can only reach the tree
  that way is not attached; `MediaStatus.OverServer` says so, and it tries again every ten seconds. Voice (declared at
  64) is carried as before. A stream sent without a declared bitrate is taken to be 1000.
- **Voice.** When a voice channel reaches `VoiceTreeThreshold` people, the member who sorts first by id and can relay starts
  a tree (the others wait a little longer for each place further down, so only one does). Everybody joins it and sends
  their audio up it; the root and the other members pass it on. The root sends to `VoiceRootFanout` (6) members. Until
  a member is attached, and whenever there is no tree, audio goes straight to each person as before. If the root
  disappears, the next one in line starts a new tree; the gap is a few seconds. When the channel shrinks to half the
  threshold the root ends the tree. Voice trees are not signed (each packet would nearly double in size), so a member in
  the tree could pass off audio as someone else's.
- **Upload speed.** A member that can relay sends a burst of 120 packets (about 130 KB) to a directly connected peer outside its own network 30 seconds after starting and every 30 minutes, and the peer reports how closely together they arrived; that gives the speed of the narrowest link (`ChatNode.Speed.cs`, the best of two peers, blended with the last result). From it a member passes on two fifths of its upload (128 KB/s to 2 MB/s instead of a fixed 384 KB/s) and serves as many members as fit the stream's bitrate (1 to 6 instead of a fixed 3). Until it is measured nothing changes.
