# Android app

`apps/Yaparr.Android` is the Android app, built with Avalonia. It uses the same library as the desktop app. It opens onto the list of your rooms and direct chats (the one with the newest message first, with a count of what is unread); tap one to read and send text messages. The + button makes a room, joins one from an invite, or opens a chat with a person from their ID. In a room the menu has the people (tap one to write to them), Copy invite and Leave; a room with several text channels shows them as buttons above the messages. The phone's back button goes back from a chat to the list. Rooms are kept in the settings the same way as on the desktop, so a room you join on one of your devices turns up on the others.

It works only while the app is open. There is no foreground service, no background connection and no notifications yet, and no screen sharing or watching one. Voice, pictures and GIFs, the GIF shelf, replies and mentions, moderation, settings, the connection check, backup and the soundboard are all there (see the sections below); what is still missing from them is listed in each section and in #127.

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

## Pictures

The **+** next to the message box sends a picture or GIF. It opens Android's own picture chooser, which hands Yaparr only the one you pick, so the app needs no permission for your photos. What you have typed in the box goes along as the caption. The picture is shrunk (up to 1600 pixels a side) and re-saved, which also drops the camera and location details; a GIF is sent as it is if it is within 8 MB. Pictures show in the chat and a tap opens one big. A picture someone sent you is fetched by itself as on the computer, and one that is too big to fetch on its own, or that nobody who is online has, says so and waits for a tap. **Save** in the big view asks Android where to put the file (the system's "save as"), again without a permission. Pictures are kept on the phone with the history, in the app's private storage.

The **GIF** button next to **+** opens the GIF shelf: pictures kept on the phone to send again with a tap (hold one to remove it; **Add a picture** chooses a file, and **Add to my GIF shelf** in the long-press menu of a picture keeps that one). The **This room** tab is the room's shared shelf: any member can add to it, everyone can send from it, and hold a picture for **Add to my shelf** or, for your own pictures (or any, for an admin), a way to remove it. Neither tab has a limit on how many pictures it holds. See [Pictures and GIFs](pictures-and-gifs.md#a-rooms-shelf).

Not there yet: taking a picture with the camera from the app (the chooser can still offer a camera app on some phones), and several pictures at once.

## Replies, mentions and message actions

A long press on a message opens a small window with what can be done with it: **Reply**, **Copy text**, and, for the owner or an admin of the room, **Delete for everyone** (after a question). A reply shows a bar above the message box ("Replying to ...", with a cross to drop it), and the sent message carries a quote of the one it answers, in the chat and for everyone else; a tap on the quote goes to that message, in its channel. A quote whose message is not on the phone says "An earlier message". A picture can be a reply too.

Typing an **@** and the start of a name in a room offers the people there, as buttons above the keyboard; choosing one puts their name in the text and names them in the message, so they are told. A message that names you has a yellow bubble.

Not there yet: @everyone, and asking the other members for an original message the phone does not have.

## Renaming a room

**Rename the room** in a room's menu gives it a name of your own, for you only: the others keep the name they gave it, and a name you choose is kept with the room across restarts.

## Roles, banning and blocking

**People** in a room's menu lists the people there, with **Owner** and **Admin** next to those who are. A tap on a person opens a small window: **Message** (a direct chat), and, depending on who you are, **Make admin** or **Remove as admin** (only the room's owner, who made the room), **Ban from this room** (an owner or admin, for an ordinary member) and **Block**. A long press on someone's message has **Ban sender** and **Block sender** as well.

A ban is for the room: every member's Yaparr stops showing the person's messages and voice. It does not lock them out, since they still hold the invite and can come back under a new identity. A block is yours alone: you stop seeing and hearing the person in every room, on all your devices, they disappear from your lists and your connection to them is closed; they are not told. **Blocked and banned** in the room menu (and in a direct chat's menu) lists who you have blocked and who is banned from the room, each with a button to lift it. A direct chat also has **Block this person**.

## Voice

A room has a voice bar under its title: one row for each voice channel of the room with the people in it and a **Join voice** button (a room has at least one; more can be made on the computer); joining another channel leaves the first. The first time, Android asks to allow the microphone (only for this; text chat never needs it). In a call there are four buttons: **Mute**, **Deafen**, **Speaker** (the loudspeaker, or the phone's earpiece, or a headset when one is connected; the button shows which one is on) and **Leave**, and everyone in the channel is shown, lit up while they talk. The call runs through the same library as on the computer (Opus over the existing connections), with speech levelling on so quiet voices are raised and a crowd does not clip. While you are in a call Android treats the phone as being in a call: other apps' audio gives way, and if a phone call or another app takes the audio your microphone is muted until you unmute it.

For now the call ends when Yaparr leaves the screen, because there is no foreground service yet. Echo and noise are handled by Android itself: Yaparr opens the microphone and speakers as a call's, which switches on the phone's own echo cancellation and noise suppression, the same ones phone calls and other calling apps use. How good they are depends on the phone, and unlike on the computer there are no settings for them. If you hear your own echo, use the phone (earpiece) or a headset instead of the loudspeaker. The default voice channel only; other voice channels come later.

## Soundboard

In a voice call, **Soundboard** opens a window with the room's sounds. **Play** sends a sound into the call so everyone in the channel hears it (not while you are deafened, and not before it has finished arriving). **Add a sound** takes an audio file (WAV, MP3 or OGG) with Android's file chooser, keeps the first 20 seconds, makes it as loud as the other sounds so one loud file does not blast everyone, and asks for a name. A sound can be removed by whoever added it, and by the owner or an admin of the room. "Silence everybody's sounds for me" turns the sounds of other people off for you (their voices are not affected) and is kept.

Not there yet: choosing which part of a long file to keep, renaming a sound, previewing a sound before sending it, and muting one person's sounds only.

## Settings

**Settings** at the top of the room list holds:

- **You:** your name, as people see it. Saving it changes it on your other devices too.
- **Appearance:** System (follows the phone), Light or Dark; it applies at once and is kept. Messages already on screen keep the colours they had until the chat is opened again.
- **Voice:** whether the microphone sends all the time or only when you talk, how loud you have to be to be heard (sensitivity), how loud everyone sounds, and evening out your own microphone. A call that is going takes the change at once. Echo and noise are handled by Android itself, so there is nothing to set for them.
- **Network:** **Check the connection** runs the same check as on the computer and says in plain words what works and what does not: whether the phone can reach the internet, whether finding people works, whether others can relay for it, and whether people who were found could be connected to. It takes up to a few seconds; **Again** runs it once more and **Copy** puts the report on the clipboard for a report of a problem.
- **Backup:** saves a copy of your account, room keys and message history as a zip file; Android asks where to put it (the system's "save as", so the app needs no permission for your files). Keep the file private: anyone who has it can pretend to be you and read your rooms.
- **About:** the version, and a button that copies who the phone is connected to and what it has been doing, for a report of a problem.

**Restore a backup** is on the first screen of a phone that has no account yet (so nothing is replaced): choose the zip file and Yaparr brings the account, the rooms and the history back and starts again. To use your account on a second device at the same time, link it instead of restoring; two devices running the same backup would clash.

Not there yet: choosing the microphone or speakers (the route button in a call picks where the sound goes), and the network options (the check is there, the settings are not).

## Links

Yaparr links open in the app: `yaparr://join/<invite>` and `yaparr://chat/<peer id>`, and the forms people share, `https://yaparr.com/join#<invite>` and `https://yaparr.com/chat#<peer id>`. A link never acts on its own: for a room the join sheet opens with the invite filled in, asking you to name the room and to confirm (joining lets the other members see your IP address); for a person a question asks first. If you are in the room or already chat with them, the link just shows it. In a room the menu has Copy invite link, and in a direct chat Copy chat link; the join sheet also takes a link instead of an invite.

The https links are claimed for the app only once yaparr.com vouches for it (an asset links file there, naming the certificate that signed the package); until then they open the web page, whose **Open Yaparr** button hands the link to the app. The page's button uses an `intent:` address that names the package, so no other app can pick the link up, which matters because an invite is a secret.

## Releases

The Android app is released on its own, with its own version and tags (`android-v1.2.3`), by `.github/workflows/release-android.yml`, which is run by hand from the Actions tab: when the changes since the last Android release that concern the app (its folder, the library, the shared layer) call for a release, it builds a signed package, `Yaparr-<version>.apk`, and attaches it to a release named "Android 1.2.3". It does not wait for the computer release and does not hold it up. That release is never marked as the latest one, because the computer app updates itself from the latest release; the website looks for the newest Android release separately and offers the package to visitors on an Android phone. How the releases are told apart and started is in [development](development.md#releases). It needs four secrets in the repository: `ANDROID_KEYSTORE_BASE64` (the keystore file, base64), `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD`. Without them no package is built, because a package signed with the key in the repository (which is public) could be replaced by anyone. Keep a copy of the keystore: a package signed with another key cannot update the installed ones, so losing it means everyone has to uninstall once. The version code is made from the version (1.36.2 is 1036002).

## Updating

Every build is signed with the same key, `apps/Yaparr.Android/yaparr-debug.keystore`, so a new build installs over the old one (`adb install -r`, or opening the file on the phone) and keeps your chats and account. The version number is the CI run number, so each build from CI is newer than the last. A build from before this key was added was signed with a key of its own, so it has to be uninstalled once; after that, updates work.

The key is only for builds that are tried on a phone. It is in the repository, so it proves nothing about who made a package. A release for other people has to be signed with a key kept secret (pass its path and passwords as `AndroidSigningKeyStore` and so on, which win over the project's), and Google Play needs its own signing setup.

## What is stored

In the app's private storage (`files/Yaparr`): the identity key, the name, the last invite, the message history and the peer cache. Android keeps this away from other apps.

## Known limits

- Tried on an emulator (Android 16): first start, making a room, sending a message, going back to the list, the room menu, and restarting the app (it comes back on the list with the room and its last message). Not tried with a second person, because a peer on the same Wi-Fi as the emulator cannot reach it (the emulator sits behind its own NAT, and the connection attempts time out). So receiving messages, the unread counts, direct chats, the list of people, channel buttons, and rooms arriving from a linked computer are written but not checked: test them with a real phone.
- Not there yet: making a text or voice channel in a room (the computer makes them; the phone shows and joins them), a foreground service, notifications, and screen sharing (when it is built it will support Android 14 and newer only, so that "Share one app" keeps notifications out of the picture). The smaller gaps are at the end of each section above.
- Everything above was tried on an emulator, not with a second person on the other end (bans, mentions and received replies in particular) and not on a real phone.
- Linking: pasting the code and passphrase was tried on the emulator, and so was scanning. The camera scan was tried on the emulator's virtual camera (the scene had the QR code on posters around the camera): it asked for the camera permission, read the code and linked. A real phone is not tried yet.
