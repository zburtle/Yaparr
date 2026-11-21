# Yaparr Cloud

## What Yaparr Cloud is

An optional account on a Yaparr Cloud server, such as cloud.yaparr.com, whose address someone gives you. It keeps your account in step on your computers and phones (your rooms, name, pins and message history), so a new device can pick up where you left off, and on a phone it can wake Yaparr when someone writes to you.

You sign in in your browser. The server cannot read what it holds: it is locked on your device with your Sync key, which is never sent to the server. Nothing is sent anywhere unless you use this.

## Syncing

Yaparr syncs by itself: soon after your rooms, keys or name change, and once a day for your message history. It never asks anything. If you are signed out it stops and says so in Settings. It starts once your Sync key has been made. **Sync now** does it straight away.

## Messages for people who are away

Messages and pictures go straight to the people who are online. Only when someone you know is away is a locked copy left on the server, for up to 7 days, so they get it when they are back. The server cannot read it.

Turn **Leave messages for people who are away** off and nothing is left there. You still receive what others leave for you.

## When your network blocks UDP

Some networks only let web traffic out. With **Use Yaparr Cloud for messages when this network blocks UDP** on, Yaparr sends its traffic to your Yaparr Cloud account, which passes it on. That is enough for messages and voice, but not for screen sharing.

## Push notifications

On a phone, Yaparr Cloud can wake the phone when someone writes while Yaparr is closed, without keeping it connected all the time. It uses your Yaparr Cloud account and a push app such as ntfy (UnifiedPush). With no push app, Google's Firebase is used instead. The push carries nothing: no names and no words.

## The Sync key

The Sync key locks everything Yaparr Cloud holds for you, so the server cannot read it. You need it to set up a phone or computer that has nothing. Keep it somewhere safe: if you lose it and have no device that is still signed in, what the server holds cannot be opened. **Replace Sync key** makes a new one, for when the old one was lost or someone else has seen it.

For how it works underneath, see [Yaparr Cloud (sync)](../backup-server.md) and [Synced state](../sync.md).
