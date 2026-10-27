# Backlog

Improvements we have agreed to do later, roughly in priority order. Done items are in the git history.

## Diagnostics and support

- **Copy diagnostics button.** Bundle the log, app version, NAT type, VPN/TURN state and the connection list into one block the user can paste into a bug report, with keys and passwords removed.
- **Log rotation.** Keep a few days of logs (one file per day) instead of deleting the file at 2 MB, which can cut a log off in the middle of a problem.

## Security and safety

- **Protect secrets on disk.** The WireGuard config (`vpn.conf`, contains the private key) and the TURN password in `network.json` are plain text. Protect them with Windows DPAPI so they are tied to the user account. Profile export should keep working.
- **Rate limits per peer.** Limit incoming connections and messages per peer so one bad peer cannot flood a room.
- **Updater and installer signing.** Check that the auto-updater verifies what it downloads, and code-sign the MSI so Windows does not show an "unknown publisher" warning.

## Polish

- Desktop notifications and unread counts (including in the taskbar).
- Queue messages typed while the app is offline and send them when it reconnects.
- First-run guide that explains the VPN and TURN options in plain words.
- Debian/Ubuntu installer (set aside earlier).
- Wire `network.json`, the VPN and the new connection/delivery status into `Yaparr.Host` and the Flutter UI (on the shelved `flutter-ui` branch).
- NAT-PMP / port mapping through the VPN tunnel.
- Faster first DHT lookup.
- Local network discovery is off by default in the WPF app for now; add a switch in the UI (it is the `localDiscovery` key in `network.json`).
- Show delivery status for each recipient in a room (today it is a count).
