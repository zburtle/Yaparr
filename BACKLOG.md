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

- Only a personal block exists. Next: room admins with a signed ban list for private rooms (the invite carries the admin's public key), then rotating the room secret so a banned person cannot rejoin, and a vote-based ban (signed votes counted the same way on every app; only members present for a while get a vote; private rooms only, since in a public room anyone can make new identities). A block does not hide a blocked person's messages from others, by design.
- Blocking someone does not stop them seeing you in rooms you share, or your messages: they can only be kept out by removing their access to the room.

## Voice

- Push-to-talk in the Desktop app needs a global hotkey (Avalonia has none); the library already supports it.
- Per-person volume sliders in the UI.
- Voice processing: the notification sounds play on their own device and are not part of the echo reference, so they are not cancelled. Echo cancellation is weaker when the microphone and speakers are different devices on different clocks (a USB headset microphone with laptop speakers). Try RNNoise in front of the WebRTC processor if keyboards and fans still come through. The Linux library needs glibc 2.38 or newer; for older systems build our own, and only if people ask. Needs a real test with a speaker and microphone in one room.
- Channels larger than about six people (target 15 to 20). A full mesh costs about 55 kbps per stream, so talking to 19 people is roughly 1 Mbps up; open mics, relay-bound peers and decoding many talkers are the pain points. Measure a real 8 to 10 person call first to see which limit hits. Staged plan:
  - Stage 1, no protocol change (comfortable to about 10 to 12): don't send to deafened people (state is already published); only dial and send to people actually in the channel; decode and mix only the loudest ~4 talkers; lower bitrate (24 kbps) if needed.
  - Stage 2, elected forwarder (15 to 20): one member with a good connection acts as a mini-SFU. Everyone sends once to it and it fans out. Chosen deterministically from the voice roster (best connectivity, then lowest ID), announced by a signed beacon, with failover to the next candidate. The forwarder pays about 1 Mbps up and adds one hop (about 20 to 60 ms). Needs a room-level voice key (per-sender keys, rotated when someone joins or leaves) so the forwarder only moves ciphertext and cannot listen; that is the main work.
  - Stage 3, only if needed: a tree of several forwarders, each serving a subset (little gain below about 30 people).
- Voice over the TURN relay is capped by the server's per-session limit; check that 32 kbps per listener fits.
- Video and screen sharing on the same media layer.

## Relaying

- Standby paths exist only for people reached through a member, and only when a second capable member knows them. Someone on a direct connection or on the TURN server has none, and a crash is still noticed only after the 15 s idle limit (the standby then takes over, but voice is silent until then).
- A standby costs a keep-alive every 4 s through the second member and a handshake; measure that in a large room before opening one for everyone.
- Count how connections end up (direct, through a member, through the TURN server) and show it in the network check, to see how much load the member relays take off the server.
- Show the relay counters in the Desktop app's network dialog.
- The "already connected" check for server addresses only knows an address's owner if we once connected to it directly; an address we have never reached is still dialled once.
- Rate limits are fixed numbers; scale them to the relay's measured upload.

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
- Local network discovery is off by default in the WPF app for now; add a switch in the UI (it is the `localDiscovery` key in `network.json`).
- Show delivery status for each recipient in a room (today it is a count).
