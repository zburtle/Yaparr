# Android app

`apps/Yaparr.Android` is the Android app, built with Avalonia. It uses the same library as the desktop app. It opens onto the list of your rooms and direct chats (the one with the newest message first, with a count of what is unread); tap one to read and send text messages. The + button makes a room, joins one from an invite, or opens a chat with a person from their ID. In a room the menu has the people (tap one to write to them), Copy invite and Leave; a room with several text channels shows them as buttons above the messages. The phone's back button goes back from a chat to the list. Rooms are kept in the settings the same way as on the desktop, so a room you join on one of your devices turns up on the others.

It works only while the app is open. There is no foreground service, no background connection and no notifications yet, and no voice, pictures, soundboard or screen sharing. These and the other desktop features are tracked in #126 and #127.

## Build and run

You need the Android workload, a JDK (21) and the Android SDK:

```
dotnet workload install android
dotnet build apps/Yaparr.Android -c Debug -p:EmbedAssembliesIntoApk=true
```

If the build cannot find them, pass `-p:JavaSdkDirectory=<jdk>` and `-p:AndroidSdkDirectory=<sdk>`. The package is `bin/Debug/net10.0-android/org.yaparr.android-Signed.apk`; install it with `adb install -r`.

The project is not in `Yaparr.slnx`, so `dotnet build` and `dotnet test` at the repository root do not need the workload.

## Linking to your computer

On a new phone, "Already have Yaparr on a computer? Link with it" takes the account over from a computer: your name, and the account, so people see you as one person. On the computer open Settings, Devices, Link another device and choose Show a QR code (it closes by itself after two minutes); on the phone choose Scan the QR code. If scanning does not work, paste the link code and passphrase instead.

The QR code leaves out your list of rooms, because a code with many rooms is too big for a QR code. Rooms follow from the computer once the phone has connected to it, as they do for any linked device. Scanning uses the phone's camera through CameraX (part of Android's own support libraries) and ZXing to read the code, all on the phone: nothing goes through Google Play services, so it works on any Android phone with a camera. Yaparr asks for the camera permission the first time you scan, and only for this; if you refuse, paste the code instead.

## Updating

Every build is signed with the same key, `apps/Yaparr.Android/yaparr-debug.keystore`, so a new build installs over the old one (`adb install -r`, or opening the file on the phone) and keeps your chats and account. The version number is the CI run number, so each build from CI is newer than the last. A build from before this key was added was signed with a key of its own, so it has to be uninstalled once; after that, updates work.

The key is only for builds that are tried on a phone. It is in the repository, so it proves nothing about who made a package. A release for other people has to be signed with a key kept secret (pass its path and passwords as `AndroidSigningKeyStore` and so on, which win over the project's), and Google Play needs its own signing setup.

## What is stored

In the app's private storage (`files/Yaparr`): the identity key, the name, the last invite, the message history and the peer cache. Android keeps this away from other apps.

## Known limits

- Tried on an emulator (Android 16): first start, making a room, sending a message, going back to the list, the room menu, and restarting the app (it comes back on the list with the room and its last message). Not tried with a second person, because a peer on the same Wi-Fi as the emulator cannot reach it (the emulator sits behind its own NAT, and the connection attempts time out). So receiving messages, the unread counts, direct chats, the list of people, channel buttons, and rooms arriving from a linked computer are written but not checked: test them with a real phone.
- Not there yet: voice channels (they are left out of the channel buttons until #126), pictures, replies, mentions, roles and moderation, blocking, and settings (#127).
- Linking: pasting the code and passphrase was tried on the emulator, and so was scanning. The camera scan was tried on the emulator's virtual camera (the scene had the QR code on posters around the camera): it asked for the camera permission, read the code and linked. A real phone is not tried yet.
