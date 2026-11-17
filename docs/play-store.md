# Google Play: drafts for the Console

Text to paste into the Play Console, written from what the app and the Yaparr Cloud server do today. **Check every line against the app before submitting**: a wrong answer in the Data safety form or a declaration is what gets an app rejected or removed. Things marked **(confirm)** are facts only the person running the service can give.

## Gaps to close first

1. **Deleting an account.** Built (Yaparr.Cloud `/account/delete`, linked from the app's Yaparr Cloud settings on the computer and the phone). It deletes the account, backup, synced records and wake-up registrations. Use `https://cloud.yaparr.com/account/delete` as the deletion web address in the Console, and say in the policy that this is where to delete. It takes effect once it is merged and deployed.
2. **A privacy policy page** at a stable address (suggestion: `https://yaparr.com/privacy`, in the Yaparr.Web repository). The text below is a draft for it.
3. **A contact address** for the policy and the listing **(confirm)**.

## Store listing

**App name:** Yaparr

**Short description (80 characters):** Chat, voice and screen sharing between friends. Nothing a server can read.

**Full description:**

> Yaparr is a chat app for you and your friends. Messages, pictures, voice and shared screens go straight from phone to phone. When a friend is away, a locked copy of your message can wait on Yaparr Cloud for up to 30 days; nobody at Yaparr can open it.
>
> • Rooms and direct messages, with replies, mentions, reactions, emoji and GIFs you upload yourself
> • Voice channels and a soundboard
> • Watch a friend's shared screen, or share your own (Android 14 and newer)
> • Your name, rooms and blocks are the same on all your devices (computer and phone)
> • Works without an account. An optional Yaparr Cloud account keeps an encrypted backup and wakes your phone when someone writes
> • Push notifications through UnifiedPush (an open standard), with Google's push only as a fallback
> • No ads, no tracking, no analytics
>
> Rooms are private: only people with the invite link can join, and everything in a room is encrypted. You can block anyone, and room owners can ban people.

(Check the screen-sharing line against what the Android app does at release; `docs/android-app.md` is the reference.)

## Data safety form

The form asks what is collected and shared. "Collected" means sent off the device to the developer or a service the developer runs. Messages and pictures go between the people's own devices; with a Yaparr Cloud account a locked copy of a room message also waits on the server for up to 30 days (the mailbox, `docs/mailbox.md`). The answers below are for the optional Yaparr Cloud only.

| Question | Answer | Why |
|---|---|---|
| Does the app collect or share any of the required user data types? | Yes (optional, only with a Yaparr Cloud account) | The account and the backup. |
| Is all of the data encrypted in transit? | Yes | Peer connections are encrypted; the server is https only. |
| Do you provide a way for users to request that data be deleted? | Yes (the deletion page, `/account/delete`) | Required. |
| **Personal info > Email address** | Collected, **not shared**; purpose: account management; **optional** | Names the Yaparr Cloud account. Nothing is ever emailed. |
| **Personal info > User IDs** | Collected, not shared; purpose: app functionality; optional | Account id, device ids, wake token (only a hash is kept). |
| **App activity / Messages** | **Collected (optional), not shared; purpose: app functionality; encrypted in transit and at rest** | With a Yaparr Cloud account, a copy of each room message and direct message the person writes, and of each picture they send, is left on the server for the other people, locked with a key that only they have (made from the room's secret, or from a secret shared by the two people in a direct chat), which the server never has: nobody at Yaparr can read it. Kept 30 days, then removed; removed with the account. Counted as collected because it is held on a service the developer runs, even though the developer cannot read it. If you prefer, answer "Other user-generated content" instead of "Messages". A backup is a file locked with a code only the person has; the server cannot read it either. |
| **Files and docs** | Not collected | The backup is locked on the device; counted as not readable by the developer. If you prefer to be conservative, declare "Other user-generated content: collected, optional, app functionality, encrypted so the developer cannot read it". |
| **Device or other IDs** | Collected, not shared; purpose: app functionality; optional | The push endpoint (a UnifiedPush address or a Firebase token) so the phone can be woken. |
| Location, contacts, calendar, health, financial info, web browsing, photos and videos, audio, diagnostics | Not collected | The camera is used on the phone only to read a link QR code, and the microphone only for voice; neither goes to the developer. Diagnostics stay in a log file on the device that the person can choose to share. |

Everything above is optional: the app works with no account at all.

## Permissions and foreground services

Play asks for a reason for each sensitive permission and for each foreground service type. Suggested text:

- **Foreground service, remote messaging** (`FOREGROUND_SERVICE_REMOTE_MESSAGING`): "Keeps the connection to the user's friends open while the app is in the background, so chat messages arrive, if the user turns on 'Stay connected in the background'. It shows a persistent notification with a Turn off button. It is also started for two minutes when a push notification arrives, so the waiting message can be fetched."
- **Foreground service, microphone** (`FOREGROUND_SERVICE_MICROPHONE`): "Keeps the microphone working during a voice call after the user leaves the app. It runs only while the user is in a voice channel, and the notification shows it."
- **Foreground service, media projection** (`FOREGROUND_SERVICE_MEDIA_PROJECTION`): "Shares the user's screen with the friends in a room when the user taps Share screen and accepts Android's own screen capture prompt. It runs only while sharing, and the notification shows it and has a stop button." Play may ask for a short video: record starting a share, the system prompt, the notification and stopping.
- **Camera:** "Reads the QR code that links the phone to the user's computer. Not used for anything else; the app works without a camera."
- **Microphone (`RECORD_AUDIO`):** "Voice channels."
- **Notifications:** "Notifies of direct messages, mentions and replies."
- **Not requested in the Play build:** `REQUEST_INSTALL_PACKAGES` (the Play build has no self-update).

## Content rating

Chat between people who join by invitation, with no moderation by the developer: the Console's questionnaire treats this as **user-generated content that is shared between users**. Answer "yes" to users interacting and sharing content; "no" to sharing location; violence, sexual content, language and drugs: none in the app itself. Declare that the app has user-blocking (the Block menu) and that room owners and admins can ban people (`docs/moderation.md`). Play's User Generated Content policy expects a way to **report** content. Yaparr has no central moderator who could see or act on a report (messages never reach a server, and nobody can ban a person from the platform), so a report button to "us" would be dishonest. What exists is block, and room bans by the room's owner and admins. The honest options are to say exactly that in the form, and, if a report action is wanted, to make it a **report to the room's owner and admins**, who can see the message and ban the person from their room.

## Privacy policy (draft for the page)

> # Yaparr privacy policy
>
> *Last changed: (date).*
>
> **In short.** Yaparr sends your messages, pictures, voice and shared screens directly between you and the people you talk to. When someone is offline and you have a Yaparr Cloud account, a copy of your room messages also waits for them on Yaparr Cloud, locked so that we cannot read it. We do not use ads, trackers or analytics.
>
> **What stays on your devices.** Your messages, pictures, rooms, blocks and settings are stored on your devices. A diagnostics log is kept on the device; it leaves only if you share it.
>
> **What the people you talk to see.** Because the connection is direct, the people in a room can see the network address (IP address) your device connects from, as with any direct connection. They also see your name and picture, and a small cloud next to your name if you are signed in to Yaparr Cloud.
>
> **Yaparr Cloud (optional).** If you sign in to a Yaparr Cloud server (ours, at cloud.yaparr.com, or one you or a friend run), it keeps:
> - **messages waiting for people who are offline**: a locked copy of each message you write in a room or in a direct chat, and of the pictures you send, for up to 30 days. It is locked on your device with a key that only the room's members have, so the server cannot read it. We can see that your account left or fetched something, when, how big it was and from which address, and an opaque id that stands for the room, but not what is in it or who the room's members are;
> - your **email address**, which only names the account (nothing is ever emailed), and a hash of your password;
> - an **encrypted backup** of your profile and an **encrypted copy of your name, rooms and blocks** that keeps your devices the same. They are locked on your device with a recovery code that never leaves your devices, so the server cannot read them;
> - **how to wake your phone**: a push address (from a UnifiedPush app, or a Firebase token if you choose Google's push), a device id, and a secret wake token (only a hash is kept). When someone writes to you while your phone is off, their app asks the server to wake your phone; the server sends a push with **no content** (no name, no words, no room). The sender needs no account;
> - **a short log** of server activity for running the service. A wake-up is logged by its outcome (sent, failed, rate limited) and nothing about who or where. **(confirm retention and what else the host logs, such as connection addresses)**.
>
> We do not sell or share this data. It is used only to provide these features.
>
> **Google's push (optional fallback).** If you choose Firebase push because you have no UnifiedPush app, Google delivers the content-free wake-up and sees your device token and the time. Nothing else is sent through it.
>
> **Deleting your data.** You can delete your account, with its backup, synced copy, push registrations and the locked messages it left for others, at (address of the deletion page). Removing the app removes the data on the device. People you talked to keep the messages they received.
>
> **Children.** Yaparr is not directed at children under 13 **(confirm the age you set in the Console)**.
>
> **Changes and contact.** Changes are posted on this page. Questions: (contact address).

## Before the first upload

- Release signing: the bundle is signed with the release key (the upload key); keep the keystore. See `docs/android-app.md`, "Google Play".
- Start with an **internal testing** track, then **closed testing**; a new personal account may have to run a closed test with a minimum number of testers for some weeks first (check the Console for the current numbers).
- Set the target audience, ads (none), and the data-safety and app-content forms to match this page.
