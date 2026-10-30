# Backlog

Improvements we have agreed to do later, roughly in priority order. Done items are in the git history.

## Diagnostics and support

- **Copy diagnostics button.** Bundle the log, app version, NAT type, VPN/TURN state and the connection list into one block the user can paste into a bug report, with keys and passwords removed.
- **Log rotation.** Keep a few days of logs (one file per day) instead of deleting the file at 2 MB, which can cut a log off in the middle of a problem.

## Security and safety

- **Protect secrets on disk.** The WireGuard config (`vpn.conf`, contains the private key) and the TURN password in `network.json` are plain text. Protect them with Windows DPAPI so they are tied to the user account. Profile export should keep working.
- **Rate limits per peer.** Limit incoming connections and messages per peer so one bad peer cannot flood a room.
- **Updater and installer signing.** Check that the auto-updater verifies what it downloads, and code-sign the MSI so Windows does not show an "unknown publisher" warning.

## Moderation

- Only a personal block exists. Next: room admins with a signed ban list (the invite carries the admin's public key), then rotating the room secret so a banned person cannot rejoin, and a vote-based ban (signed votes counted the same way on every app; only members present for a while get a vote, since anyone can make new identities). A block does not hide a blocked person's messages from others, by design.
- Blocking someone does not stop them seeing you in rooms you share, or your messages: they can only be kept out by removing their access to the room.

## Voice

- Push-to-talk in the Desktop app needs a global hotkey (Avalonia has none); the library already supports it.
- Per-person volume sliders in the UI.
- Voice processing: the notification sounds play on their own device and are not part of the echo reference, so they are not cancelled. Echo cancellation is weaker when the microphone and speakers are different devices on different clocks (a USB headset microphone with laptop speakers). Try RNNoise in front of the WebRTC processor if keyboards and fans still come through. The Linux library needs glibc 2.38 or newer; for older systems build our own, and only if people ask. Needs a real test with a speaker and microphone in one room.
- Large voice channels are built (see the README, "Streams and large rooms"): a tree from 9 people up, tested in one process with up to 10 people and a root that disappears. Not tried on real networks or with real people. Still to do:
  - Measure a real 10 to 20 person call: delay through two or three hops, the root's upload with many open microphones (19 open microphones is about 5 Mbit/s for a root with 6 children, over the default 3 Mbit/s forwarding limit), and CPU for decoding many talkers.
  - Stage 1 savings that were not done: don't send to deafened people, and decode and mix only the loudest few talkers.
  - Voice packets in a tree are not signed, so a member in the tree could pass audio off as someone else's. Signing every packet nearly doubles it (64 bytes on about 90); signing a hash of several packets would be cheaper.
  - A voice tree starts at the first member who sorts by id and can relay; if the first several cannot, each waits its turn and it takes seconds. Announce who can relay in the voice beacon, and prefer the best connection.
  - The root's failure leaves a gap of a few seconds; keep a prepared second root.
- Voice over the TURN relay is capped by the server's per-session limit; check that 32 kbps per listener fits.
- Screen sharing: see "Screen sharing" below. Camera video would reuse the same pieces.

## Relaying

- Standby paths exist only for people reached through a member, and only when a second capable member knows them. Someone on a direct connection or on the TURN server has none, and a crash is still noticed only after the 15 s idle limit (the standby then takes over, but voice is silent until then).
- A standby costs a keep-alive every 4 s through the second member and a handshake; measure that in a large room before opening one for everyone.
- Count how connections end up (direct, through a member, through the TURN server) and show it in the network check, to see how much load the member relays take off the server.
- Show the relay counters in the Desktop app's network dialog.
- The "already connected" check for server addresses only knows an address's owner if we once connected to it directly; an address we have never reached is still dialled once.
- Rate limits are fixed numbers; scale them to the relay's measured upload.
- Relaying for people we share no room with ("Help others connect"), on by default with a setting to turn it off. A last tier: used only when no room member can relay and the TURN server is unavailable or overloaded. Needs a relay directory (capable peers announce under a DHT topic), choosing by latency, `MaySharedRoomRelay` relaxed to accept non-members, a separate and tighter budget for strangers, and a headless relay mode in the CLI so volunteers can run an always-on relay with bigger caps. This reverses the room-only rule, so the README's Relaying section changes when it is built.
- What a stranger's relay learns: both IP addresses, both peer IDs, and when and how much they talk; never the contents (end to end encrypted). It is one hop, not Tor: there is no onion routing and no anonymity, and it cannot become an open proxy because it only forwards between two Yaparr peers. Say this plainly in a first-run notice, the Help menu and the README, since machines now carry strangers' traffic by default.
- Anyone can generate identities, so there is no reputation to rely on: lean on the budgets, and drop a relay that misbehaves. Phones and metered connections never relay (see Android).

## Multiple devices

Built (see the README, "Using several devices"). Each device has its own key and a certificate from the account key, and is an ordinary peer; the account groups them. This replaced the first plan (one key on every device, with connections keyed by peer and device ID), which would have meant reworking every table keyed by peer ID and made older clients fight over the one slot.

Not done:

- Direct messages are not copied between your own devices: a message you send from one device, or receive while another is off, is not shown on the other. Make direct chats a signed log with IDs like room messages, so devices fill each other's gaps, and copy sent messages to your other devices. Check first how direct messages are kept (the Desktop app keeps them, not the library).
- Voice: both devices can join the same channel and show up as two people. One device per account: joining on a second moves you there and the first is told to leave; the voice beacon names the device.
- Show the account's devices in the app (a list, with the one in use) and a way to see which one a message came from.
- A block is recognised for a device once we have seen its certificate on a connection. A message that arrives only through other members from a device we have never connected to is not covered; put the certificate in signed messages and presence beacons.
- The mesh limits (`TargetConnections`, `MaxConnections`, introductions) count devices, not people, so a person on three devices uses three slots.
- Address records in the DHT are looked up by id, so an account id finds the first device's record only; other devices are found through the account topic they announce under. Publish a record per device and look them up by account.
- The CLI does not link devices.
- Not tried on real networks, and the Desktop menus and dialogs have not been looked at on screen.
- A linked device holds the account key, so a stolen device means a new identity. If the key stayed on one device, a lost one could be revoked: sign a revocation with the account key and pass it around with the account settings.
- The link code is encrypted with a passphrase and expires in a day, but can be used more than once. A one-time code would need the two devices to talk, for example through the DHT the way a room is found.
- Sync needs two of your devices online together. An always-on device (the CLI in a headless mode on a Raspberry Pi, a VPS or a home server) is just another device that never sleeps, and could hold history and receive direct messages for you. It can share the headless mode with volunteer relays.
- No network storage for backups. The mainline DHT (BEP 44) only fits tiny short-lived records: about 1000 bytes, expires in around two hours without a republish, and many nodes do not support it. To evaluate later if offline delivery without running anything is wanted: Nostr relays for encrypted store-and-forward. They are third-party servers, use secp256k1 where we use P-256, see connection metadata, and keep messages as long as each relay chooses.

## Screen sharing

The distribution is built: `StartMedia`, `WatchMedia` and the tree behind them pass signed packets from a sharer to a whole room (README, "Streams and large rooms"), with a cache of the packets since the last key for late viewers. Capture, encoding, decoding and the viewer are not started. Sharing a screen or window is Windows only for now; watching should work wherever the app runs. The plan is to use FFmpeg as a separate program the app starts, not linked in:

- Sender: FFmpeg's Windows screen sources (`ddagrab`, `gdigrab`) capture and encode. Use a hardware H.264 encoder (NVENC, AMF, Quick Sync) when the machine has one, otherwise VP8 or VP9 in software; pick with a short test at the start.
- Transport: the encoded packets go over an unreliable channel, split into datagrams of about 1100 bytes with sequence numbers, and a keyframe is requested when a viewer joins or loses packets.
- Viewer: FFmpeg decodes to raw frames drawn in a viewer tile. Windows bundles it; the `.deb` recommends the system `ffmpeg` package. Android can only watch (its own decoders handle both codecs).
- A share is announced like a voice beacon (who, which screen or window, size, codec), and nothing is sent to anyone who has not clicked Watch. A blocked person's share is never shown.
- Licence: ship an LGPL build only (no x264, which makes a build GPL), with its notices next to the existing ones. Check the H.264 patent position first, or ship VP8 and VP9 only. Which prebuilt LGPL builds exist is unchecked; if there is none, build a trimmed one in CI, which also keeps the installer small.
- Bandwidth is the real limit. Default to 720p at 5 to 15 frames per second and 0.5 to 1 Mbit/s, and a viewer passes it on to at most 3 others (more for the sharer if its upload allows). Peer relays (256 KB/s in total) cannot carry video, and the TURN server has its own limits, so a viewer reached through it gets lower quality or is refused. Adaptive bitrate comes after.
- Sound from the shared window needs its own capture; later.
- Prototype first: one Windows share and one viewer on the same machine, to measure CPU use and bitrate before choosing codecs and settings.

## Streams and trees

Built for screen sharing and large voice channels. Not done:

- Viewers choose their own parents only through the root, so the root and a member's parent must be reachable; a member who cannot reach the root (or whom the root cannot reach) is never attached. The relay path through other members could carry these connections too.
- The members that can be parents are those with a relay-capable connection; nothing measures upload speed, so `MediaFanout` and `MediaBytesPerSecond` are fixed numbers. Scale them to a measured upload (also the Relaying item about rate limits).
- Placement is only swapped at the root: if the sharer's places go to members who cannot pass the stream on, one steps aside; lower down, a capable newcomer waits if every place is held by a member who cannot.
- The root believes what members report about who sits under whom. A member can only mislead where newcomers are placed, but it should be checked or repaired by the root asking.
- No adaptive bitrate and no quality layers: every viewer gets the same stream, and a viewer with a bad link just loses packets. Forwarders could drop layers per child.
- Forwarders see the stream (they are viewers). Members who forward without viewing, such as the planned volunteer relays, would need a key held only by the viewers.
- A viewer sees the addresses of its parent and children, whom it might not otherwise connect to in a large room.
- The Desktop app has nothing for it yet: voice trees work without any UI, and a screen sharing UI needs the capture work above.

## Android

No technical blocker, but a lot of work. The core library is portable .NET and the audio libraries (miniaudio, SkiaSharp) ship Android builds. Not tried yet; everything below is from reading the code and the packages. Suggested order: an Android app reusing the library with text chat only as a side-loadable APK, then the foreground service, then voice, and Google Play last.

- The UI: the Desktop app is a desktop `Window` with menus, context menus, drag and drop and pop-up dialogs. Android needs its own app project with a one-screen layout, its own navigation and the dialogs rewritten (Avalonia supports Android). This is most of the work.
- Staying connected: there is no server, so no push notifications; messages only arrive while the app runs. Android freezes background apps, so it needs a foreground service with a permanent notification (and a microphone-type service for calls, with its extra rules).
- Phone networks: mobile data is almost always behind carrier NAT, so the TURN server carries most of the traffic. Phones should not relay for others: the "ideal NAT" test needs to also exclude battery saver, mobile data and metered connections.
- Network changes: `NetworkChange.NetworkAddressChanged` ([ChatNode.cs](src/Yaparr/Chat/ChatNode.cs)) may not work on Android; use Android's connectivity callback to call `NotifyNetworkChanged` when Wi-Fi and mobile data swap, and wrap the subscription so a missing implementation cannot crash.
- Multicast: UPnP (and local discovery, off by default) need a Wi-Fi multicast lock on Android.
- 16 KB pages: the WebRTC audio library for Android (`libwebrtc-apm.so`, arm64 and x64) is aligned to 4 KB, and Google Play needs 16 KB for new submissions. A side-loaded APK is fine, and on a 16 KB device the library fails to load, which `TryCreate` handles (no echo cancellation or noise suppression). Fix by building our own library or asking SoundFlow. miniaudio's Android library is already 16 KB aligned. There is no 32-bit ARM build of the WebRTC library at all.
- Permissions and audio: runtime prompts for microphone, notifications and Bluetooth; audio focus; speaker versus earpiece routing.
- Packaging and updates: the updater downloads an MSI or `.deb`, so Android needs another path. CI needs the Android SDK and Java, and a signing key kept as a secret.
- Google Play: chat apps with user content need an in-app way to report people as well as block them (only blocking exists), and foreground service and microphone use must be declared. A side-loaded APK avoids all of it.
- Testing: emulators have odd networking; use real phones.

## Polish

- Direct connections through symmetric NATs: try the ports just above and below the one a peer advertises ("birthday" or port-prediction punching), so fewer pairs need the relay. Needs a symmetric-NAT test setup to verify.
- Pictures: a "Save picture as" and a larger view, animated WebP, and checking more than the sender (for example members that already have the file) before showing a download button.
- Desktop notifications: system pop-ups, an unread count on the taskbar icon (Windows overlay icon), and the flashing taskbar button on Linux (needs the window manager's urgency hint, which Avalonia does not expose). Sounds are fixed tones; let people choose, set the volume, and turn off per room.
- Queue messages typed while the app is offline and send them when it reconnects.
- First-run guide that explains the VPN and TURN options in plain words.
- Wire `network.json`, the VPN and the new connection/delivery status into `Yaparr.Host` and the Flutter UI (on the shelved `flutter-ui` branch).
- NAT-PMP / port mapping through the VPN tunnel.
- Faster first DHT lookup.
- Local network discovery is off by default in the Desktop app for now; add a switch in the UI (it is the `localDiscovery` key in `network.json`).
- Show delivery status for each recipient in a room (today it is a count).
