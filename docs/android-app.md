# Android app

`apps/Yaparr.Android` is the Android app, built with Avalonia. It uses the same library as the desktop app. It opens onto the list of your rooms and direct chats (the one with the newest message first, with a count of what is unread); tap one to read and send text messages. The + button makes a room, joins one from an invite, or opens a chat with a person from their ID. In a room the menu has the people (tap one to write to them), Copy invite and Leave; a room with several text channels shows them as buttons above the messages. The phone's back button goes back from a chat to the list. Rooms are kept in the settings the same way as on the desktop, so a room you join on one of your devices turns up on the others.

It connects while the app is open, and keeps connected with the screen closed when you turn on **Stay connected in the background** (off by default); it notifies of messages when it is not on the screen (see Notifications below). (Sharing the phone's own screen is there on Android 14 and newer, and watching someone else's, from the phone's call). Voice, pictures and GIFs, the GIF shelf, replies and mentions, moderation, settings, the connection check, backup and the soundboard are all there (see the sections below); what is still missing from them is listed in each section and in #127.

## Build and run

You need the Android workload, a JDK (21) and the Android SDK:

```
dotnet workload install android
dotnet build apps/Yaparr.Android -c Debug -p:EmbedAssembliesIntoApk=true
```

If the build cannot find them, pass `-p:JavaSdkDirectory=<jdk>` and `-p:AndroidSdkDirectory=<sdk>`. The package is `bin/Debug/net10.0-android/org.yaparr.android-Signed.apk`; install it with `adb install -r`.

The project is not in `Yaparr.slnx`, so `dotnet build` and `dotnet test` at the repository root do not need the workload.

## Linking to your computer

**The first start is a short wizard.** The first screen asks "Have you used Yaparr before?". **No, I'm new here** goes to "What should people call you?" (the name others see next to your messages; it can be changed in Settings) and then to the rooms. **Yes, I already have an account** goes to "Bring your account here", which asks where the account is now: **On my computer** or **In a backup file**. A phone that already has an account but no name (it was closed before the name was typed) goes straight to the name. The back button, or Back, steps back through the wizard.

**On my computer** takes the account over from a computer: your name, and the account, so people see you as one person. On the computer open Settings, Devices, Link another device and choose Show a QR code (it closes by itself after two minutes); on the phone choose Scan the QR code. If scanning does not work, paste the link code and passphrase instead.

The QR code leaves out your list of rooms, because a code with many rooms is too big for a QR code. Rooms follow from the computer once the phone has connected to it, as they do for any linked device. Scanning uses the phone's camera through CameraX (part of Android's own support libraries) and ZXing to read the code, all on the phone: nothing goes through Google Play services, so it works on any Android phone with a camera. Yaparr asks for the camera permission the first time you scan, and only for this; if you refuse, paste the code instead.

## Pictures

The **+** next to the message box sends a picture or GIF. It opens Android's own picture chooser, which hands Yaparr only the one you pick, so the app needs no permission for your photos. What you have typed in the box goes along as the caption. The picture is shrunk (up to 1600 pixels a side) and re-saved, which also drops the camera and location details; a GIF is sent as it is if it is within 8 MB. Pictures show in the chat and a tap opens one big. A picture someone sent you is fetched by itself as on the computer, and one that is too big to fetch on its own, or that nobody who is online has, says so and waits for a tap. **Save** in the big view asks Android where to put the file (the system's "save as"), again without a permission. Pictures are kept on the phone with the history, in the app's private storage.

The **GIF** button next to **+** opens the GIF shelf: pictures kept on the phone to send again with a tap (hold one to remove it; **Add a picture** chooses a file, and **Add to my GIF shelf** in the long-press menu of a picture keeps that one). The **This room** tab is the room's shared shelf: any member can add to it, everyone can send from it, and hold a picture for **Add to my shelf** or, for your own pictures (or any, for an admin), a way to remove it. Neither tab has a limit on how many pictures it holds. See [Pictures and GIFs](pictures-and-gifs.md#a-rooms-shelf).

Not there yet: taking a picture with the camera from the app (the chooser can still offer a camera app on some phones), and several pictures at once.

## Replies, mentions and message actions

A long press on a message opens a small window with what can be done with it: **Reply**, **Copy text**, and, for the owner or an admin of the room, **Delete for everyone** (after a question). A reply shows a bar above the message box ("Replying to ...", with a cross to drop it), and the sent message carries a quote of the one it answers, in the chat and for everyone else; a tap on the quote goes to that message, in its channel. A quote whose message is not on the phone says "An earlier message". A picture can be a reply too.

Typing an **@** and the start of a name in a room offers the people there, as buttons above the keyboard; choosing one puts their name in the text and names them in the message, so they are told. A message that names you has a yellow bubble.

Not there yet: @everyone, and asking the other members for an original message the phone does not have.

## Reactions

**React…** in a message's long-press menu opens a panel with the thirteen quick emoji (👍 💯 ❤️ 😂 😭 🔥 🙏 😍 🎉 👀 🤔 😮 👎), a search box and the rest of the emoji by group. The emoji shows under the message with how many put it there; tap one to add yours (the count goes up by one) or, if it is yours, to take it back (it goes down by one), and tap the small grey circle with a plus next to them to add another emoji. Rooms and direct chats; see [Rooms and messages](rooms-and-messages.md#reactions). Not there yet: seeing who put an emoji there.

## Link previews

A message with a web link carries a card below it with the page's title, description and picture, as on the computer: the phone reads the page when the message is sent, and the people reading it never contact the site. A tap on a card asks first and shows the full address, then opens it in the browser. Settings > **Link previews** turns sending cards off. See [Rooms and messages](rooms-and-messages.md#link-previews).

## Sharing the screen

On **Android 14 and newer**, a room's voice panel has **Share my screen**. It is shown while this phone is in the room's call, or while another device of yours (your computer, say) is in it and this phone is not: you can talk on the computer and share from the phone. With none of your devices in the call there is nothing to share into and the button is not shown. Older versions of Android are left out on purpose: only Android 14 and newer have the system's **Share one app** choice, which keeps the status bar, notifications and other apps out of the picture; before that the only choice was the whole screen, and every notification would show.

- **Quality and sound.** The button first opens a panel: a choice of **Light** (540p, 15 pictures a second, 0.7 Mbit/s), **Standard** (720p, 30, 1.8 Mbit/s) and **High** (1080p, 30, 3.5 Mbit/s), with a line saying how many people this phone's connection can send it to directly (the same rule as the computer's share dialog: the best quality that reaches three people at once is chosen, Standard until the upload has been measured), and **Share the sound of the app too**. The sound is what the shared app plays (what the whole screen plays when that is shared), caught with Android's audio playback capture (Android 10 and newer, and it needs the microphone permission, which Android uses for capturing audio, asked for here if the phone was not in the call); Yaparr's own sound, the voice chat included, is left out so that it does not go round. It is sent as the stream of its own that the computer's viewers already play next to the picture (stereo, Opus). If the sound cannot be shared, the picture is, and the reason is shown.
- **What happens.** "Choose what to share" opens Android's own dialog ("Share your screen with Yaparr?"), where you choose **Share one app** (and then the app) or the whole screen. Yaparr never sees anything before you agree. A red indicator shows in the status bar while it goes on, and a notification (**Yaparr is sharing your screen**, with a **Stop sharing** button) is shown for as long as it does: Android requires it, and it keeps the share running while another app is in front, which is the point. **Stop sharing my screen** in the room stops it too, and so does stopping it in Android's own indicator.
- **How it is sent.** The picture is encoded as H.264 by the phone's own encoder (a long side of at most 1280 pixels, up to 30 pictures a second, 2 seconds between key frames; a still screen sends a picture again every half second so the viewers' picture does not stall) and sent to the room the way the computer's share is: it is the same kind of stream, so people watching on the computer see it and nothing on their side changes. The bitrate follows the size of the picture (about 1.5 Mbit/s for a phone screen, 2.5 at most). When the line cannot keep up, pictures are dropped until the next key frame instead of building up a delay.
- **What it follows.** If what is shared changes size (you turn the phone, fold it, or the app's window changes) the encoder starts again at the new size; viewers take it at the next key frame. The app being shared going out of sight is noted, but nothing is done about it yet.
- **When it ends.** When you stop it; when you stop it in Android's indicator; when the encoder fails (the reason is shown); when this phone leaves the call and none of your other devices is in it; and when none of your devices has been in the call's channel for about twelve seconds (the announcements of a call can be late).
- **Not there yet.** Choosing the bitrate by hand, and keeping the share going when the shared app is out of sight in a special way (nothing is done about it).
- **Tried** on the emulator with Android 16: the system's dialog, sharing one app, the stream decoded on the computer with FFmpeg (one app only, no status bar), the Light quality (432 x 960 for this phone), the sound of the Settings app (the level of the captured sound moves when it plays a sample), stopping from the notification and from the room. Not tried on a real phone, with a person watching, with rotation or folding, or from the "another device is in the call" case on the phone.

## Watching a screen

While this phone is in a room's call, the screens shared in that voice channel are listed in the voice panel, each with **Watch** (a share from one of your own devices says **Yours** instead, so that the picture and its sound are not played back to you). **Watch** opens the picture over the whole page, with **Stop watching** under it (the back button does the same); the sound that goes with the picture plays on the phone's speakers.

- **How it is shown.** The frames of the stream go to the phone's own video decoder (`MediaCodec`), which draws on a native surface inside the page (`ScreenSurfaceHost`, a `TextureView` in a `NativeControlHost`), so no picture is copied through the app. The first key frame says which codec it is (H.264, or VP8 when the sharer's computer had no graphics card to encode on) and how big; a key frame of another size starts the decoder again. The picture keeps its shape and is as big as the space allows.
- **When the phone cannot keep up,** frames are dropped and the picture waits for the next key frame instead of falling behind.
- **When it ends.** When you stop it, when the sharer stops, and when this phone leaves the call (watching a screen goes with being in the call, as on the computer).
- **Tried** on the emulator with a recorded H.264 stream played through the real decoder and surface (the list, the picture, stopping, watching again). Not tried against a live share from another device, with VP8, or with the sound.

## Renaming a room

**Rename the room** in a room's menu gives it a name of your own, for you only: the others keep the name they gave it, and a name you choose is kept with the room across restarts.

## Roles, banning and blocking

**People** in a room's menu lists the people there, with **Owner** and **Admin** next to those who are. A tap on a person opens a small window: **Message** (a direct chat), and, depending on who you are, **Make admin** or **Remove as admin** (only the room's owner, who made the room), **Ban from this room** (an owner or admin, for an ordinary member) and **Block**. A long press on someone's message has **Ban sender** and **Block sender** as well.

A ban is for the room: every member's Yaparr stops showing the person's messages and voice. It does not lock them out, since they still hold the invite and can come back under a new identity. A block is yours alone: you stop seeing and hearing the person in every room, on all your devices, they disappear from your lists and your connection to them is closed; they are not told. **Blocked and banned** in the room menu (and in a direct chat's menu) lists who you have blocked and who is banned from the room, each with a button to lift it. A direct chat also has **Block this person**.

## Voice

A room has a voice bar under its title: one row for each voice channel of the room with the people in it and a **Join voice** button (a room has at least one; more can be made on the computer); joining another channel leaves the first. The first time, Android asks to allow the microphone (only for this; text chat never needs it). In a call there are four buttons: **Mute**, **Deafen**, **Speaker** (the loudspeaker, or the phone's earpiece, or a headset when one is connected; the button shows which one is on) and **Leave**, and everyone in the channel is shown, lit up while they talk. The call runs through the same library as on the computer (Opus over the existing connections), with speech levelling on so quiet voices are raised and a crowd does not clip. While you are in a call Android treats the phone as being in a call: other apps' audio gives way, and if a phone call or another app takes the audio your microphone is muted until you unmute it.

A call goes on when you leave the screen: while you are in one, Yaparr runs a foreground service (a notification that is always showing, with **Leave call**), and the call ends when you leave it. A swipe away from the recent apps does not end a call. Echo and noise are handled by Android itself: Yaparr opens the microphone and speakers as a call's, which switches on the phone's own echo cancellation and noise suppression, the same ones phone calls and other calling apps use. How good they are depends on the phone, and unlike on the computer there are no settings for them. If you hear your own echo, use the phone (earpiece) or a headset instead of the loudspeaker. The default voice channel only; other voice channels come later.

## Notifications and staying connected

Yaparr notifies of a new message when it is not on the screen, for **direct messages, mentions and replies** to you, and for **every message** in a room you set that way in the room's menu (**Notify for every message**). Nothing is shown for your own messages, while the app is open, for messages older than two minutes (the history that arrives after a reconnect), or when **Notify me of messages** in Settings is off. There is one notification for each chat, which counts the new messages and shows the latest few once the phone is unlocked; **on the lock screen it says only who wrote and in which room, never the words** (Android's private notification). Opening the chat, or the app, takes it away, and a tap opens that chat. Android 13 and newer ask for permission the first time notifications are turned on; **Send a test notification** in Settings shows what one looks like.

Messages only arrive while the phone is connected, so **Stay connected in the background** (Settings, off by default, Android 8 and newer) keeps the connection going when you leave the app or swipe it away. It runs as a foreground service of the "remote messaging" kind (the microphone kind is added during a call), which means Android shows a quiet notification while it is on, with **Turn off**, and the phone uses some battery. Some phones stop apps anyway; **Let Yaparr run in the background** opens Android's battery settings, where Yaparr can be allowed. With the setting off, the connection ends when the app is closed, and nothing arrives until it is opened again.

- **Tried** on the emulator: the settings, the test notification (channel, lock screen version), the service starting and stopping with the setting, and the connection carrying on, and picked up again by the app, after a swipe away. The message trigger itself is covered by tests of its rules. **Not tried** with real messages (the emulator cannot be reached by another device), on a real phone, or with a phone's own battery limits.
- Not there yet: replying from the notification, a sound or vibration choice per room (Android's own channel settings apply), muting a room, and a delay before a message notifies when you are active on another of your devices.

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

**In a backup file** is on the first screens of a phone that has no account yet (so nothing is replaced): choose the zip file and Yaparr brings the account, the rooms and the history back and starts again. To use your account on a second device at the same time, link it instead of restoring; two devices running the same backup would clash.

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
- Not there yet: making a text or voice channel in a room (the computer makes them; the phone shows and joins them), watching someone else's screen from a notification, replying from a notification, and muting a room. The smaller gaps are at the end of each section above.
- Everything above was tried on an emulator, not with a second person on the other end (bans, mentions and received replies in particular) and not on a real phone.
- Linking: pasting the code and passphrase was tried on the emulator, and so was scanning. The camera scan was tried on the emulator's virtual camera (the scene had the QR code on posters around the camera): it asked for the camera permission, read the code and linked. A real phone is not tried yet.
