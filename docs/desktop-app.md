# The desktop app

[Documentation](README.md)

`apps/Yaparr.Desktop` is the chat app, built with Avalonia so it runs on Windows and Linux: a list of rooms and direct chats,
messages with delivery marks, the people in the room, and buttons to create or join a room (the room key is under the room title: click it to copy it, or use Copy link to copy a link that opens Yaparr; see [Links](#links)) or
open a direct chat by peer ID. Under each connected person in the People list it says how you reach them: *direct*, *via server* (through the TURN relay you set up or imported, whose bandwidth is limited) or *via member* (another member forwards your traffic; they cannot read it). Your name, identity key, joined rooms and message history are kept in
`%LOCALAPPDATA%\Yaparr` so you keep the same peer ID and rejoin your rooms on the next start. To run two copies on
one machine, give each its own folder: `Yaparr.Desktop.exe --name alice --data C:\temp\alice` (the launch profiles
`alice` and `bob` do this). Run it with `dotnet run --project apps/Yaparr.Desktop`.

**Settings** (File > Settings… or Ctrl+,) opens over the window with a tree of sections on the left and the section's controls on the right, in a panel that fills the height of the window (the same for every section): Devices (a table of this device and your other connected ones, live, with Link another device… under it), Notifications, Voice & audio, Network (the connection check, run when you open it, with VPN and TURN relay as items under it), Blocked people (everyone you blocked, each with an Unblock button), Backup and About (which also has Copy diagnostics, Open log file and Open log folder). Esc, the ✕ or a click outside closes it and the keyboard goes back to where it was. In a narrow window (under about 640 pixels) the tree of sections becomes a drop-down above the page. The keyboard focus starts in the sections, the arrow keys move between them, Tab stays inside the settings, and the sections, the drop-down and the ✕ have names for screen readers. The notification options change as soon as you click them. Voice & audio shows all the voice settings (microphone, speakers, volume, echo cancellation, noise suppression, always send, push to talk and the sensitivity with a live level bar) and applies a change at once, also to the call you are in; "Voice settings…" in a room's menu opens it. The VPN and the TURN relay are shown in full on their own pages, with their fields and buttons right there; the connection guide opens the right one. Your profile is the name above the conversations: click it to open your ID and click the ID to copy it, and point at the name to get a pencil that changes it. The menu bar only has File (create or join a room, Settings, go to the first unread mention, exit) and Help (connection guide, check for updates, report a problem, about).

The **People** list on the right is the people of the room that is open: those who are here now, then those seen in it before (a room made by this version remembers who has been in it; a room from an earlier version, which has no owner, only shows who is here now), then anyone banned from it and anyone you blocked whom you met in it. Open another room and the list changes to that room's people. For a direct chat there is nothing to list, so the panel closes and the chat takes the room.

## Replies and mentions

Right-click a message and choose **Reply**: a bar above the box shows what you are answering (Esc or the ✕ cancels it), and your message carries the quoted message above its words. A reply can carry a picture: with a reply bar open, send a picture and it answers that message. Click a quote to jump to the message it answers, in another text channel of the room too. When the message is no longer in your history, the app asks the members who are connected for it and jumps to it if one still holds it.

Type **@** at the start of a word in a room to choose someone from the people of the room (arrow keys and Enter or Tab, or click); their name is written into your message and marked. Admins and the owner can also type **@everyone** to name every member; from anyone else it is only words. A message that names you, or answers one of yours, gets an outline, and the unread badge of that room shows an **@** until you read it. In a direct chat there is nobody to name, but replies work.

## Channels

A room made by this version lists its channels under it, text and voice. Click a text channel to show it, a voice channel to join it. The owner and admins right-click a channel to rename it, move it up or down (the first text and voice channels stay first) or delete it. The app remembers which text channel was on show in each room, and keeps count of what you have not read in each channel across restarts: the number on a room is what has arrived in its channels since you last looked at them, and a first start after an upgrade counts everything already there as read.

To install it, see [Installer and updates](installer-and-updates.md).

## Notifications

A short sound plays when a message arrives in a conversation or text channel you are not looking at, or while the window is in the background, and when someone joins or leaves the voice channel you are in (with a different sound for you joining or leaving). On Windows the taskbar button also flashes until you bring the window forward. Messages that arrive while catching up on history are never announced. Both can be switched off under **Notifications** in the menu; the sounds play on the speakers chosen in the voice settings.

Being named or answered has a sound of its own (three rising notes). It plays whenever you are not looking at that message, also when the window is active but the mention is in another room or another text channel, and it is never held back by the short gap between message sounds. For busy rooms, **Settings > Notifications > Only for mentions, replies and direct messages** makes every other room message silent (it still counts as unread). **File > Go to the first unread mention** opens the oldest unread message that names you or answers you, in whichever room it is. A mention that arrives while history is being caught up is marked (the **@** on the badge) but not played.

A message that arrives while the window is in the background also shows a pop-up in the system's notification area (Settings > Notifications > Show pop-ups). On Windows it is a tray balloon, which Windows 10 and 11 show as an ordinary toast; the tray icon exists for a few seconds after the last pop-up, and clicking the pop-up brings the window forward and opens that conversation. On Linux the unread count also goes on the launcher icon where the desktop reads it (KDE and the Ubuntu dock), and under X11 the window manager is asked to highlight the window (it does nothing under Wayland, where the pop-up is the signal). The Linux parts go through the desktop's own D-Bus services and were only built, not tried on a Linux desktop.

The Windows taskbar button shows the number of unread messages (Settings > Notifications > Show the unread count), and the sound of a new message can be chosen (Settings > Notifications > Message sound).

## Diagnostics

Help > "Connection guide" explains in plain words when the VPN and TURN options help and opens them. It also comes up once on the first start.

Settings > About > "Copy diagnostics" puts the version, system, network state and check, how each person is reached, the voice settings and the last 300 log lines on the clipboard. Names of people and rooms are left out, ids and addresses are shortened (`LogScrubber`) and keys and passwords removed.

Settings > About > "Copy sharing diagnostics" is the same for a problem with sharing a screen or watching one (a share that shows one picture and no more, say): the kind of desktop (session type, Wayland or X11, Flatpak or not), whether the portal or the X11 copy is used, which of GStreamer, xrandr and the sound tools are there, FFmpeg's version and the encoders it has, what the current or last share did (encoder, frames and key frames sent, frames per second, the FFmpeg arguments and what FFmpeg and GStreamer last said) and the log lines about sharing and watching. Names are left out and addresses shortened, as in the other report (`ScreenDiagnostics` and `ScreenSharer.Describe`).

Help > "Report a problem…" does that and then opens the bug report form on GitHub in your browser, with the version and system already filled in; the diagnostics are too long for a link, so they are on the clipboard to paste into the form's Diagnostics box (read them first). The repository's issue forms (bug report and feature request) are in `.github/ISSUE_TEMPLATE`, and a vulnerability is pointed to the private security report instead.

## Logs

One file per day (`logs/yaparr-2026-10-31.log`), seven days kept, a day stops at 20 MB.

## Links

A room or a person can be shared as a link: **Copy link** next to the room key copies `https://yaparr.com/join#<invite>`, and **Copy chat link** in a direct chat copies `https://yaparr.com/chat#<peer id>`. Opening one in a browser opens Yaparr (installed from the installer, the `.deb` or the Flatpak, which register the `yaparr://` address). Yaparr asks before it joins the room or starts the chat, and if you are already in it, it just shows it. A link pasted into "Join a room" or "New direct chat" works too. The secret part is after the `#`, so it never goes to yaparr.com; see [Security](security.md).
