# The desktop app

[Documentation](README.md)

`samples/Yaparr.Desktop` is the chat app, built with Avalonia so it runs on Windows and Linux: a list of rooms and direct chats,
messages with delivery marks, the people in the room, and buttons to create or join a room (the room key and copy-invite button are under the room title) or
open a direct chat by peer ID. Under each connected person in the People list it says how you reach them: *direct*, *via server* (through the TURN relay you set up or imported, whose bandwidth is limited) or *via member* (another member forwards your traffic; they cannot read it). Your name, identity key, joined rooms and message history are kept in
`%LOCALAPPDATA%\Yaparr` so you keep the same peer ID and rejoin your rooms on the next start. To run two copies on
one machine, give each its own folder: `Yaparr.Desktop.exe --name alice --data C:\temp\alice` (the launch profiles
`alice` and `bob` do this). Run it with `dotnet run --project samples/Yaparr.Desktop`.

The **People** list on the right is the people of the room that is open: those who are here now, then those seen in it before (a room made by this version remembers who has been in it; a room from an earlier version, which has no owner, only shows who is here now), then anyone banned from it and anyone you blocked whom you met in it. Open another room and the list changes to that room's people. For a direct chat there is nothing to list, so the panel closes and the chat takes the room.

To install it, see [Installer and updates](installer-and-updates.md).

## Notifications

A short sound plays when a message arrives in a conversation you are not looking at, or while the window is in the background, and when someone joins or leaves the voice channel you are in (with a different sound for you joining or leaving). On Windows the taskbar button also flashes until you bring the window forward. Messages that arrive while catching up on history are never announced. Both can be switched off under **Notifications** in the menu; the sounds play on the speakers chosen in the voice settings.

The Windows taskbar button shows the number of unread messages (Notifications > Show the unread count), and the sound of a new message can be chosen (Notifications > Message sound).

## Diagnostics

Help > "Copy diagnostics" puts the version, system, network state and check, how each person is reached, the voice settings and the last 300 log lines on the clipboard. Names of people and rooms are left out, ids and addresses are shortened (`LogScrubber`) and keys and passwords removed.

## Logs

One file per day (`logs/yaparr-2026-10-31.log`), seven days kept, a day stops at 20 MB.
