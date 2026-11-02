# Backlog

What is left to do, roughly in priority order. Finished work is described in the [documentation](docs/README.md), so each item here says only what remains.

## Diagnostics and support

- A button that opens a pre-filled GitHub issue with the diagnostics (Help > "Copy diagnostics" is built, see [the desktop app](docs/desktop-app.md)).
- A setting for how many days of logs are kept (seven today).

## Security and safety

- **Secrets on disk (see [Security](docs/security.md)).** Still open: on Linux and macOS the key and files are only private to their owner (no password-free equivalent; a keyring would need libsecret), the soundboard lists (sound names) and thumbnails in memory are not encrypted, and a Windows password reset by an administrator makes sealed files unreadable (restore from an export).
- **A shared relay is one password for everyone.** Yaparr ships with no relay, but a relay code (Network > TURN relay > Share this relay) carries the server's user name and password, so anyone given it can use the relay and pass it on, and it is withdrawn only by changing the password for everybody. Fix it on the server: short-lived credentials per person (the TURN REST scheme), a quota per credential, and a ban on private address ranges.
- **Rate limits per peer.** Still open: limits on voice and media packets, on DHT requests, and a temporary ban for a peer that was disconnected for flooding (it can reconnect at once now).
- **Updater (see [Installer and updates](docs/installer-and-updates.md)).** Not covered: the checksum comes from the same GitHub account as the file, so it protects against a damaged or swapped download but not against someone who controls the release; on Windows a program running as the same user could swap the MSI between the check and the install (Linux is closed: root copies the package where only root can write, checks it again and installs the copy).
- **A setting to turn off the update check at start** (it contacts `api.github.com` every time the app starts).
- **An independent review of the protocol, the room proofs and the WireGuard client.** None of it has had one. Until then the VPN is described as experimental.
- **Run the FFmpeg that decodes other people's screens with less power** (a Windows job object or low integrity level, a restricted Linux process), so a flaw in a decoder reaches less. Pictures from members are decoded in the app itself by SkiaSharp; keep it current.
- **Pin the GitHub Actions to commit hashes** (they are pinned to version tags now, and Dependabot keeps them current), and the tools the release workflow runs through `npx`.
- **Code-sign the MSI (low priority, costs money).** Windows shows an "unknown publisher" warning. A signature the app checks itself (a key built into the app, the signature published next to the file) would close the gap above for free, but the key would live in the same CI as the release.

## Moderation

- **Room owner, bans and replacing the secret: known gaps.** Someone away for more than 30 days, or never seen by the owner's device, needs the new invite from the owner; a member that joined after the key was replaced cannot recognise holders of keys older than the one it joined with, so only longer-standing members can hand over; only the owner can replace the key, so a lost owner device can still do it; if the owner replaces it from two devices at once the lower package hash wins, which can briefly split the room; everyone seen in the room is on the list, so a new identity that slipped in before a ban is given the key unless it is banned first; the dialogs have not been run by hand. An order that arrives before the statement making its signer an admin is dropped and only comes back when the peer next joins (there is no periodic resend); a person banned under one device id whose certificate we have not seen yet can still get through that device until we have.
- **The owner's device.** A lost or stolen device of the owner can appoint admins for good: every linked device holds the account key itself, so it cannot be revoked, and the owner cannot be changed. This is accepted for now and the app says so when a room is made; the answer is a new room. The real fix is keeping the account key on one device (see the linked-device item below). Rooms made by earlier versions have no owner and no admins; the way to get them is a new room.
- **Channels: not done.** The desktop buttons and menus have not been run by hand; a channel removed while you are in it only sends you out of voice in the desktop app (the library leaves the session running); unread counts are not kept across restarts; the channel on show is not remembered; channel order cannot be changed after creation.
- **A list of everyone you have blocked** (for example File > Blocked people…). The People list shows the people of the open room, so someone you blocked can only be unblocked from the list of a room you met them in; someone you only knew from a direct chat has no list to be unblocked from.
- App lock (shelved). A PIN that only hides the window protects nothing, since the key files can be copied. A useful one encrypts the identity file and saved secrets with a key made from a passphrase or PIN (slow key derivation, on top of the Windows login protection already used), and on Windows can add Windows Hello with the TPM chip, which locks after failed tries. A short PIN can be guessed offline in minutes without that hardware lockout, so offer a passphrase and say so. It lowers the lost-device risk for room owners but does not remove it.
- A vote-based ban: signed votes counted the same way on every app; only members present for a while get a vote, since anyone can make new identities. A personal block, the signed ban list and replacing the room secret are built (see [Moderation](docs/moderation.md)).

## Voice

- Push-to-talk: a mouse button as the key, and a sound when the key goes down.
- Voice processing: the notification sounds play on their own device and are not part of the echo reference, so they are not cancelled. Echo cancellation is weaker when the microphone and speakers are different devices on different clocks (a USB headset microphone with laptop speakers). Try RNNoise in front of the WebRTC processor if keyboards and fans still come through. The Linux library needs glibc 2.38 or newer; for older systems build our own, and only if people ask. Needs a real test with a speaker and microphone in one room.
- Large voice channels are built (see [Streams and large rooms](docs/streams-and-large-rooms.md) and [Voice channels](docs/voice.md)): a tree from 9 people up, tested in one process with up to 10 people and a root that disappears. Not tried on real networks or with real people. Still to do:
  - Measure a real 10 to 20 person call: delay through two or three hops, the root's upload with many open microphones (19 open microphones is about 5 Mbit/s for a root with 6 children, over the default 3 Mbit/s forwarding limit), and CPU for decoding many talkers.
  - Voice packets in a tree are not signed, so a member in the tree could pass audio off as someone else's. Signing every packet nearly doubles it (64 bytes on about 90); signing a hash of several packets would be cheaper.
  - A voice tree starts at the first member who sorts by id and can relay; if the first several cannot, each waits its turn and it takes seconds. Announce who can relay in the voice beacon, and prefer the best connection.
  - **The root's failure (partly done).** A root that vanished without a goodbye (a crash, a lost connection) left a call without audio for 28 to 30 seconds, since everyone waited for its announcements to lapse (three of the ten seconds). A voice tree is now announced every second (`VoiceAnnounceInterval`), and the gap measured in `VoiceRootFailoverTests` with the app's own timings is 4 to 6 seconds to the first member on the new tree and 6 to 8 for everyone (a polite shutdown is 2 to 4). Not built: a prepared second root that the members already know about, which would cut it to 2 or 3 seconds; the rest is the 2 second upkeep tick and the rank wait.
- Voice over the TURN relay is capped by the server's per-session limit; check that 32 kbps per listener fits.
- Screen sharing: see "Screen sharing" below. Camera video would reuse the same pieces.

## Soundboard

The library part is built (see [Soundboard](docs/soundboard.md)). Not done:

- **The Desktop page is built but has only been compiled and started, never clicked through.** Check the window, the add dialog (including a file over 20 seconds), the right-click menu, the bell buttons and that the mutes come back after a restart. A soundboard mute is kept per device id, so a person on two devices has to be muted on each. There are no keyboard shortcuts for tiles. MP3 and OGG reading has not been tried on real files.
- **Loudness.** Sounds are matched to one level when added. If that is not enough, add a per-sound volume.
- **Trigger delay.** A sound starts when the play message arrives, so listeners on slow paths hear it a little later than others. Fine for effects; not for anything that must line up.

## Relaying

- Standby paths exist only for people reached through a member, and only when a second capable member knows them. Someone on a direct connection or on the TURN server has none, and a crash is still noticed only after the 15 s idle limit (the standby then takes over, but voice is silent until then).
- A standby costs a keep-alive every 4 s through the second member and a handshake; measure that in a large room before opening one for everyone.
- Count how connections end up (direct, through a member, through the TURN server) and show it in the network check, to see how much load the member relays take off the server.
- Show the relay counters in the Desktop app's network dialog.
- The "already connected" check for server addresses only knows an address's owner if we once connected to it directly; an address we have never reached is still dialled once.
- Rate limits are fixed numbers; scale them to the relay's measured upload.
- Relaying for people we share no room with ("Help others connect"), on by default with a setting to turn it off. A last tier: used only when no room member can relay and the TURN server is unavailable or overloaded. Needs a relay directory (capable peers announce under a DHT topic), choosing by latency, `MaySharedRoomRelay` relaxed to accept non-members, a separate and tighter budget for strangers, and a headless relay mode in the CLI so volunteers can run an always-on relay with bigger caps. This reverses the room-only rule, so [Relaying](docs/relaying.md) changes when it is built.
- What a stranger's relay learns: both IP addresses, both peer IDs, and when and how much they talk; never the contents (end to end encrypted). It is one hop, not Tor: there is no onion routing and no anonymity, and it cannot become an open proxy because it only forwards between two Yaparr peers. Say this plainly in a first-run notice, the Help menu and the README, since machines now carry strangers' traffic by default.
- Anyone can generate identities, so there is no reputation to rely on: lean on the budgets, and drop a relay that misbehaves. Phones and metered connections never relay (see Android).

## Multiple devices

Built (see [Devices and accounts](docs/devices-and-accounts.md)). Not done:

- **Direct messages between your own devices.** Still open: messages older than the limit are gone for good, and a device you lose keeps receiving everything until device revocation exists.
- **One device in voice.** Not done: the voice beacon does not name the device, and a device that was not connected at that moment does not leave.
- **Devices.** Not done: devices that are off (nothing remembers them), and names for devices.
- **Blocks across devices.** Not done: messages and sounds arriving before any beacon of that device has; those are caught on the next refresh.
- The mesh limits (`TargetConnections`, `MaxConnections`, introductions) count devices, not people, so a person on three devices uses three slots.
- Address records in the DHT are looked up by id, so an account id finds the first device's record only; other devices are found through the account topic they announce under. Publish a record per device and look them up by account.
- The CLI does not link devices.
- Not tried on real networks, and the Desktop menus and dialogs have not been looked at on screen.
- A linked device holds the account key, so a stolen device means a new identity. If the key stayed on one device, a lost one could be revoked: sign a revocation with the account key and pass it around with the account settings.
- The link code is encrypted with a passphrase and expires in a day, but can be used more than once. A one-time code would need the two devices to talk, for example through the DHT the way a room is found.
- Sync needs two of your devices online together. An always-on device (the CLI in a headless mode on a Raspberry Pi, a VPS or a home server) is just another device that never sleeps, and could hold history and receive direct messages for you. It can share the headless mode with volunteer relays.
- No network storage for backups. The mainline DHT (BEP 44) only fits tiny short-lived records: about 1000 bytes, expires in around two hours without a republish, and many nodes do not support it. To evaluate later if offline delivery without running anything is wanted: Nostr relays for encrypted store-and-forward. They are third-party servers, use secp256k1 where we use P-256, see connection metadata, and keep messages as long as each relay chooses.

## Screen sharing

The first layer is built (see [Sharing a screen](docs/screen-sharing.md)): sharing a monitor or a window on Windows, watching on any system. Still to do:

- The rest of the Desktop UI (showing who is watching)
- A key frame on request (the sharer only sends one every 2 s, so a joiner waits up to that long)
- Keeping the picture on the graphics card from capture to encoder (gfxcapture can scale itself, and NVENC takes Direct3D frames) to save the download of every picture
- The .deb recommending the system `ffmpeg` package
- Use the sharing window and the viewer (inside the app and in a window of its own, popping out and back) with a second person and look at them closely; they have been built and started but not tried with viewers.
- Try a minimised or resized shared window with viewers (a minimised window stops updating, and a resized window keeps its shape inside its first size).
- **H.264 patents.** Yaparr ships a trimmed LGPL FFmpeg (no x264, which would make a build GPL) with its source and licences, and decodes H.264 with FFmpeg's own decoder while the encoding happens on the user's graphics card. The copyright side is settled; the patent side is not. The AVC/H.264 Patent Portfolio License (Via LA) is free for the first 100,000 units a year per company and says nothing about free downloads or open-source software; free projects such as VLC have shipped without a licence for years, but that is not a defence. Ask Via LA (info@via-la.com) whether a licence is needed and how units are counted; VP8 alone avoids the question, at the cost of encoding on the processor. The last major US H.264 patent is reported to expire in late 2027.
- The AMD graphics card encoder (`h264_amf`) has never run on an AMD card: only its options are checked against the real encoder. Try it on one, and check that its key frames carry the parameter sets and that the stream is as steady as the other two.
- Auto tries the graphics card encoders one after the other, and each one that cannot start costs about half a second (about a second on a computer with only Intel graphics). Choose from the installed graphics adapters instead, and skip the ones that cannot work.
- Compare AV1 (SVT-AV1 in software, hardware AV1 on recent cards) with VP8 for quality per bit and processor use; it has no royalty and would be the codec to move to if VP8 is not enough.
- Bandwidth is the real limit. Default to 720p at 5 to 15 frames per second and 0.5 to 1 Mbit/s, and a viewer passes it on to at most 3 others (more for the sharer if its upload allows). Peer relays (256 KB/s in total) cannot carry video, and the TURN server has its own limits, so a viewer reached through it gets lower quality or is refused. Adaptive bitrate comes after.
- Sound from the shared window needs its own capture; later.

## Streams and trees

Built for screen sharing and large voice channels (see [Streams and large rooms](docs/streams-and-large-rooms.md)). Not done:

- Viewers choose their own parents only through the root, so the root and a member's parent must be reachable; a member who cannot reach the root (or whom the root cannot reach) is never attached. The relay path through other members could carry these connections too.
- **Upload speed.** Not done: a peer's own download or a cap on their side also limits the result (hence the best of two); mobile and metered connections are not told apart.
- Placement is only swapped at the root: if the sharer's places go to members who cannot pass the stream on, one steps aside; lower down, a capable newcomer waits if every place is held by a member who cannot.
- The root believes what members report about who sits under whom. A member can only mislead where newcomers are placed, but it should be checked or repaired by the root asking.
- No adaptive bitrate and no quality layers: every viewer gets the same stream, and a viewer with a bad link just loses packets. Forwarders could drop layers per child. A viewer whose only way in is the shared server gets nothing from a big stream; a lower layer would give it something.
- Lost packets are asked for again from the parent, which keeps about 600; there is no forward error correction, so a burst of loss longer than that, or a parent that missed the same packet, costs a key frame. Pacing is a fixed multiple of the declared bitrate, not what the link can take. Measure on a real link with loss (and tune the 0.4 s wait for a late frame, the 80 ms between requests and the 40 ms before the first).
- The probe packets after a frame (two, 80 ms apart) are an extra 20 or so bytes; a stream that never pauses does not need them.
- Forwarders see the stream (they are viewers). Members who forward without viewing, such as the planned volunteer relays, would need a key held only by the viewers.
- A viewer sees the addresses of its parent and children, whom it might not otherwise connect to in a large room.
- The Desktop app has no screen for voice trees; they work without one.

## Network tests

Built: `tools/Yaparr.NetAgent` and `tools/Yaparr.NetTest` (see tools/README.md). They create DigitalOcean machines, run the agents with simulated NATs, packet loss, blocked paths and failures, and check the result. Everything that can run on one PC has been run (the local demo scenario, the tests of scenario files, scripts and judging); nothing has run on real machines yet. Still to do:

- Second real run (basic, nyc3 / sfo3 / ams3, all public addresses): everyone found everyone after 39 to 44 s and the stream worked: attached in 0.2 s, delay 59 to 82 ms median and 103 to 109 ms at the 95th percentile, 129 lost packets repaired on one path, and one 2.4 s outage on New York to Amsterdam that cost 33 frames (the viewer resumed by itself within a second of data returning). The time to find each other is the first DHT lookup on a cold node: about 31 s, then one more cycle. The code explains it: the bootstrap runs a full self-lookup (up to 24 rounds) before anything else, each round waits for its slowest query, and about half the nodes asked never answer, so most rounds cost the full 2 s timeout; the room lookup then does the same. Fixes to try, in order: do not wait for the bootstrap self-lookup (a couple of rounds, the rest in the background), keep four queries in flight instead of waiting for the whole batch, a 1.5 s query timeout, and a quicker second search while no one has been found. The agent keeps no node cache; the Desktop app does, so a real second start is quicker.
- Discovery speed-ups for that (done, not yet measured on DigitalOcean): the bootstrap waits for two rounds only and finishes the self-lookup in the background; a lookup keeps six queries in flight and replaces each as it finishes instead of waiting for the slowest of a batch; the query timeout is 1.5 s; a plain search stops as soon as it has a peer; while alone a room is searched again every 4 s (eight times) instead of every interval, without announcing again each time. Re-run `basic` and compare "Found a peer s" with the 39 to 44 s above.
- Silent nodes are remembered (done, not yet measured): a node that did not answer a lookup is skipped by later lookups for five minutes, or until it answers. In the voice run the second search of a new machine took 8.5 s with only 15 of 32 nodes answering. Compare "Found a peer s" and the lookup log lines ("X of Y node(s) answered in N ms") on the next runs.
- Extra self-addresses ("could not connect to X: is ourselves"): seen in the first three `basic` runs (addresses outside DigitalOcean's ranges with random high ports, so mapped addresses, not the machine's own), and in none of the seven runs since, which had the faster discovery. Not understood and not reproduced; harmless, the dial gives up and backs off. If it comes back, log which DHT node returned the address.
- Found in the real runs and fixed (not yet re-measured): a viewer below a killed parent waited about 7 s (6 s edge timeout checked every 2 s); the timeout is now 3 s and streams are checked every 0.75 s. A member behind a symmetric router was sometimes placed under a parent it had no connection to and spent 13 s trying (`symmetric2` in `crowd`); a joiner now tells the root whom it is connected to and the root prefers such a parent. Re-run `failover` and `crowd` to compare.
- The first real run will need fixes: the DigitalOcean calls, machine setup (cloud-init), the NAT and traffic-shaping scripts and the TURN server are untried. Try `check`, then `run basic`, then the others.
- Measure the delay properly: the agents compare the sender's and the receiver's clocks, good to about 50 ms. A frame carrying a ping-pong round trip would give true delays.
- A GitHub Actions job started by hand, running the suite and attaching the report.
- Carrier-grade NAT and Wi-Fi cannot be simulated on a datacentre machine; keep a manual run with a laptop on a phone hotspot.
- The voice scenario needs ten machines; the default account limit is lower.
- The agent plays a tone and does not use audio devices or echo cancellation, so it says nothing about the audio processing.
- Scenarios for: a member on the shared TURN server among direct viewers, many viewers joining at once, a viewer that joins late after a long stream, and a sharer that restarts.

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

- **Direct connections through symmetric NATs (looked at, not built).** Guessing the peer's port (just above and below the advertised one) only works when both NATs hand out ports one after another and neither has made any other mapping since the advertised port was measured. A model of two symmetric, port-restricted NATs gave 100% with no stray mappings, 4% with one to five and 0% with one to forty (the app's socket is always talking to the DHT and to other people), and a random "birthday" spray of 512 probes per side about 0% against NATs that pick random ports, since both sides must hit each other's exact port. Fixing the offset needs each side to measure its newest port right before the attempt and tell the other, which needs a path that already works (and then a relay is at hand). A pair behind two symmetric NATs is covered by the paths through other members and by the shared TURN relay; the number to watch is how many connections end up on those (see "Network dialog stats").
- Pictures: checking more than the sender (for example members that already have the file) before showing a download button.
- **Desktop notifications.** Not done: system pop-ups, the flashing button on Linux (needs the window manager's urgency hint, which Avalonia does not expose), an unread count on Linux and macOS.
- **Connection lost warning to the sharer.** Tell the sharer when viewers cannot be reached (the call and the viewer are covered, see [Voice channels](docs/voice.md)).
- **Notifications for being named or answered.** Today a mention or a reply to you makes the ordinary message sound under the ordinary rules: nothing plays when you are looking at that conversation in an active window (even for a mention in another text channel of the open room, which is silent although it counts as unread, because `NotifyMessage` ignores the channel), nothing within 0.7 s of another message sound, and nothing for messages caught up from history. To do:
  - a sound of its own for being named or answered;
  - play it even when the window is focused if the mention is in another room or channel;
  - never hold it back for the 0.7 s throttle or the catch-up silence of a busy room (a mention that arrives while you were away should still be marked, if not played);
  - a setting to be told only about mentions, replies to you and direct messages, for busy rooms;
  - a way to jump to the first unread mention.
- **Replies and mentions: more.** A reply that carries a picture (a reply can quote one, but not send one); `@everyone`; a click on a quote to reach a message in another channel or one that has scrolled out of the history shown; and versions from before this show the marker (`yaparr-env1`) next to the words of a reply.
- First-run guide that explains the VPN and TURN options in plain words.
- Wire `network.json`, the VPN and the new connection/delivery status into `Yaparr.Host` and the Flutter UI (on the shelved `flutter-ui` branch).
- NAT-PMP / port mapping through the VPN tunnel.
- **Delivery per recipient.** Not done: a way to see it later, or an "everyone" view for a message in a very large room.

## Flatpak

- The amd64 bundle was built and started in a Linux container on a virtual screen (it ran, found the DHT and wrote its data under `~/.var/app/com.yaparr.Yaparr`). Not tried: arm64, a real desktop, voice with a real microphone and speakers, the file dialog through the portal, and watching a screen with a real stream.
- The hosted repository at `flatpak.yaparr.com` is built and tested against a local web server (install from a bundle, then `flatpak update` to a newer version), but not yet live: it needs the `FLATPAK_GPG_PRIVATE_KEY` secret, GitHub Pages on the `gh-pages` branch with the custom domain, and the DNS record for `flatpak.yaparr.com`. Keep a copy of the private key somewhere safe: if it is lost, everyone who installed has to add a new repository by hand.
- **Flathub** (not started). Read from Flathub's requirements, not tried:
  - The app must be built from source with no network during the build and no prebuilt binaries in the submission: a generated list of NuGet packages (`flatpak-builder-tools`) and a .NET 10 SDK extension instead of the published output. Their docs say nothing about .NET self-contained apps or about native libraries that come inside NuGet packages (SkiaSharp, miniaudio, the WebRTC audio library); ask the maintainers before investing.
  - Verify `yaparr.com`: a token at `https://yaparr.com/.well-known/org.flathub.VerifiedApps.txt`, with the site answering over HTTPS.
  - AppStream: screenshots (direct image URLs pinned to a tag or commit), a homepage URL, a brand colour, no warnings from `appstreamcli validate`.
  - Explain each permission in the pull request; run `flatpak-builder-lint`; the runtime must be the latest Flathub-hosted one.
  - Flathub does not allow AI-generated manifests or AI-opened pull requests, so the manifest and the submission have to be written and sent by hand. Submit as a pull request against `new-pr` of `flathub/flathub`.
- Importing a profile from the `.deb` (`~/.local/share/Yaparr`) the first time, with a one-time permission to read that folder.
- Push-to-talk works only while the window is focused; a global key would need the GlobalShortcuts portal.
- Native Wayland instead of XWayland, when Avalonia's Wayland backend is no longer experimental.
