# The desktop app

[Documentation](README.md)

`samples/Yaparr.Desktop` is the chat app, built with Avalonia so it runs on Windows and Linux: a list of rooms and direct chats,
messages with delivery marks, the people in the room, and buttons to create or join a room (the room key and copy-invite button are under the room title) or
open a direct chat by peer ID. Under each connected person in the People list it says how you reach them: *direct*, *via server* (through the TURN relay you set up or imported, whose bandwidth is limited) or *via member* (another member forwards your traffic; they cannot read it). Your name, identity key, joined rooms and message history are kept in
`%LOCALAPPDATA%\Yaparr` so you keep the same peer ID and rejoin your rooms on the next start. To run two copies on
one machine, give each its own folder: `Yaparr.Desktop.exe --name alice --data C:\temp\alice` (the launch profiles
`alice` and `bob` do this). Run it with `dotnet run --project samples/Yaparr.Desktop`.

The **People** list on the right is the people of the room that is open: those who are here now, then those seen in it before (a room made by this version remembers who has been in it; a room from an earlier version, which has no owner, only shows who is here now), then anyone banned from it and anyone you blocked whom you met in it. Open another room and the list changes to that room's people. For a direct chat there is nothing to list, so the panel closes and the chat takes the room.

## Replies and mentions

Right-click a message and choose **Reply**: a bar above the box shows what you are answering (Esc or the ✕ cancels it), and your message carries the quoted message above its words. A reply can carry a picture: with a reply bar open, send a picture and it answers that message. Click a quote to jump to the message it answers, in another text channel of the room too. When the message is no longer in your history, the app asks the members who are connected for it and jumps to it if one still holds it.

Type **@** at the start of a word in a room to choose someone from the people of the room (arrow keys and Enter or Tab, or click); their name is written into your message and marked. Admins and the owner can also type **@everyone** to name every member; from anyone else it is only words. A message that names you, or answers one of yours, gets an outline, and the unread badge of that room shows an **@** until you read it. In a direct chat there is nobody to name, but replies work.

## Channels

A room made by this version lists its channels under it, text and voice. Click a text channel to show it, a voice channel to join it. The owner and admins right-click a channel to rename it, move it up or down (the first text and voice channels stay first) or delete it. The app remembers which text channel was on show in each room, and keeps count of what you have not read in each channel across restarts: the number on a room is what has arrived in its channels since you last looked at them, and a first start after an upgrade counts everything already there as read.

To install it, see [Installer and updates](installer-and-updates.md).

## Notifications

A short sound plays when a message arrives in a conversation or text channel you are not looking at, or while the window is in the background, and when someone joins or leaves the voice channel you are in (with a different sound for you joining or leaving). On Windows the taskbar button also flashes until you bring the window forward. Messages that arrive while catching up on history are never announced. Both can be switched off under **Notifications** in the menu; the sounds play on the speakers chosen in the voice settings.

Being named or answered has a sound of its own (three rising notes). It plays whenever you are not looking at that message, also when the window is active but the mention is in another room or another text channel, and it is never held back by the short gap between message sounds. For busy rooms, **Notifications > Only for mentions, replies and direct messages** makes every other room message silent (it still counts as unread). **Notifications > Go to the first unread mention** opens the oldest unread message that names you or answers you, in whichever room it is. A mention that arrives while history is being caught up is marked (the **@** on the badge) but not played.

A message that arrives while the window is in the background also shows a pop-up in the system's notification area (Notifications > Show pop-ups). On Windows it is a tray balloon, which Windows 10 and 11 show as an ordinary toast; the tray icon exists for a few seconds after the last pop-up, and clicking the pop-up brings the window forward and opens that conversation. On Linux the unread count also goes on the launcher icon where the desktop reads it (KDE and the Ubuntu dock), and under X11 the window manager is asked to highlight the window (it does nothing under Wayland, where the pop-up is the signal). The Linux parts go through the desktop's own D-Bus services and were only built, not tried on a Linux desktop.

The Windows taskbar button shows the number of unread messages (Notifications > Show the unread count), and the sound of a new message can be chosen (Notifications > Message sound).

## Diagnostics

Help > "Connection guide" explains in plain words when the VPN and TURN options help and opens them. It also comes up once on the first start.

Help > "Copy diagnostics" puts the version, system, network state and check, how each person is reached, the voice settings and the last 300 log lines on the clipboard. Names of people and rooms are left out, ids and addresses are shortened (`LogScrubber`) and keys and passwords removed.

## Logs

One file per day (`logs/yaparr-2026-10-31.log`), seven days kept, a day stops at 20 MB.
