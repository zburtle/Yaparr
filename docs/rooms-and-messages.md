# Rooms and messages

[Documentation](README.md)

## Message forwarding

Group chat no longer needs every pair of members to be connected. A live group message is
signed by its author, so any member can pass it on without being able to alter or forge it. When a member receives a
new message it forwards it to the room members it is connected to, except the one it came from, the author, and
anyone who is already known to be connected to either of those (members tell each other which room members they are
directly connected to, which also lets `room.Members` list members you only reach through others, with
`IsRelayed = true`). Duplicates are dropped by message id, so loops end. Forwarding only ever goes to members of that
room, who proved they hold the key: a node that is not in the group never carries or sees the room's messages (it
can still forward opaque encrypted datagrams for a connection-level relay only if it shares a room with both ends,
see above). Messages are forwarded one hop at a time, so they travel any number of member-to-member hops.

## Connection limits

A node does not connect to every member of a room. It aims for `TargetConnections` (8)
neighbours per room and never keeps more than `MaxConnections` (12) in total; set `TargetConnections = 0` for the old
full mesh. Peers found through the DHT or learned from other members are kept as candidates and dialled at random
only while below the target. Members periodically swap small random samples of who they are connected to, so
candidates stay fresh, and a newcomer is only introduced to members that still have room. A node at its maximum
still accepts a newcomer that has almost no connections (two or fewer) by dropping one of its better-connected
neighbours, and refuses anyone else; dropped peers are not redialled for a minute. Connections you opened on purpose
(`ConnectAsync`, `OpenDirectChatAsync`, an open direct chat) are never dropped. Messages reach everyone by
forwarding (above), and catch-up repairs what forwarding misses.

## Presence

Because a node only sees its neighbours, `room.Members` also lists members it has heard about through
signed presence beacons: every `PresenceInterval` (30 s) each member signs "I'm here" for the room and neighbours
pass it on, and a member who leaves sends a signed "gone". A member counts as online for three intervals after their
last beacon (so a crash takes up to 90 s to show), and beacons from non-members are rejected.

## Catch-up

Group messages are signed by their author (ECDSA, bound to the room) and kept for good: in memory, or persisted with
`HistoryStore = new SqliteHistoryStore(path, key)` (the `Yaparr.History.Sqlite` project: one SQLite database for every room,
encrypted as a whole by SQLCipher, with a key made from the history key by `HistoryCipher.DatabaseKey`; the apps open it with
`AppData.OpenHistoryStore`, which also brings over the per-room files earlier versions kept and deletes them). Nothing is read
whole: a room opens with the newest 100 messages of each channel (`room.GetHistory()`, `ChatNode.InitialHistoryPerChannel`) and
earlier ones are read a page at a time with `room.GetEarlierHistory(channel, beforeKey, count)`, which the desktop app calls
when the list is scrolled up to the top. What is handed to other members is a window onto it, the same limits history had
(500 messages per channel and 7 days by default, `MaxHistoryMessages` and `MaxHistoryAge`). When two
members connect, each sends the keys of the messages it holds and the other replies with the ones missing, so a
member who was offline gets what they missed from whoever was online, and anyone can pass on a message without
being able to forge or alter it. Catch-up messages arrive via `MessageReceived` with `IsCatchUp = true` (sort by
`Timestamp`). Newcomers also receive recent history (the window); set
`SyncHistory = false` to disable. One-to-one chats are not synced with other people. Direct messages are kept in the same database (`dm-*.jsonl` files from earlier versions are brought over too), a chat opens with its newest 100 messages (`DirectChat.History`) and earlier ones are read with `DirectChat.GetEarlierHistory(beforeId, count)`; what your own devices swap with each other is the same kind of window as a room's.

## Replies and mentions

A message can answer an earlier one and name people. `room.SendAsync(text, channelId, replyTo, mentions)` and `DirectChat.SendAsync(text, replyTo)` send it, and `ChatMessage.Parts` (or just `Body` for the words) reads it: `ReplyTo` is the `ChatMessage.Key` of the message it answers, and `Mentions` are the people named, each a `Mention` with their account and the name as it was written after the @ (so it can be found in the words even when it holds a space, or the person has renamed since). Up to 20 people can be named in one message. `Mention.Everyone` names the whole room; it only counts when the author is an admin or the owner (`ChatRoom.Names(message, account)` says whether a message names you). A picture can answer a message too: `Attachment.ToText(caption, replyTo)` puts the key in the picture's description, and `ChatMessage.Parts.ReplyTo` reads it.
- **Asking for a message.** The history handed over when someone connects is the window (the newest 500 messages of each channel of the last 7 days), but a request is answered from everything held, so an older message can be had from anyone who still has it. `room.RequestMessages(keys)` asks up to three connected members for messages by key (at most 20) with a `want` message, and they answer with the ones they hold, which arrive through `MessageReceived` like any caught-up message. A member answers one request every two seconds. Messages nobody holds any more cannot be had.

- **How it travels.** Inside the text of the signed message, behind a marker, the way an attachment does (`MessageEnvelope`). The signature covers it, so nobody can add a reply or a name to someone else's message, and every version still accepts the message; a version from before this one shows the marker next to the words.
- **Asking for earlier history.** A device that joined late, or was restored from a backup, lacks older messages that others still hold. When the message list is scrolled up to the oldest message a device has, the desktop app calls `room.RequestEarlierHistory(channel, beforeKey)`, which asks up to three connected members (directly connected ones first) with a `back` message (the channel in `Text`, the time and key of the message in `Ts` and `Id`, how many in `N`, at most `ChatNode.BackfillMax`, 100). A member answers one such request every couple of seconds, from everything it holds, with the messages before that one and the reactions on them, as ordinary `hist` frames of 20; they are checked like any message. They are kept and not shown as news (no sound, no unread count, no scrolling to the end): `room.EarlierHistoryReceived` is raised and `room.GetEarlierHistory` reads them. The app asks again a minute after an answer that held nothing (when the list is scrolled to the top again), and at once after one that did, so scrolling back through a long history carries on. A picture in a message that is read back into view (`GetHistory`, `GetEarlierHistory`) is fetched from whoever has it (the author, then the other members) like one in a new message. Direct chats work the same way with `DirectChat.RequestEarlierHistory(beforeId)` and `DirectChat.EarlierHistoryReceived` (see [Devices and accounts](devices-and-accounts.md)).
- **The key.** It comes from who wrote the message and its id, so nobody else can make a message that has it. A reply points at that key and does **not** copy the words it answers: the app looks the original up in its own history, so a reply can never misquote. When the original is not there the reply says "an earlier message (older than the history kept, or not received yet)": it may be older than what is kept, or it may only have not arrived yet (catching up can bring a reply before the message, which is then quoted when it comes), and nothing in the app can tell the two apart.
- **In the app,** see [The desktop app](desktop-app.md#replies-and-mentions).

## Reactions

An emoji can be put on a message, and taken off again. `room.SendReactionAsync(messageKey, emoji, on, channelId)` and `DirectChat.SendReactionAsync(messageKey, emoji, on)` send it; `room.GetReactions()` and `DirectChat.GetReactions()` give a `ReactionSet` (`For(messageKey)` is the emoji on a message, each with the accounts that put it there, `Has(...)` asks about one account); `ReactionsChanged` (the message key) is raised when any of it changes, including when reactions arrive with the history.

- **How it travels.** A reaction is a message of its own, signed and passed on like any, with the message it is on and the emoji in the envelope header (`MessageEnvelope.WrapReaction`), so rooms, direct chats, catch-up, linked devices, bans and blocks, rate limits and history all work for it without anything new. It is not shown as a message: it never reaches `MessageReceived` or `GetHistory`, and is not unread. A version that does not know reactions shows "reacted 👍".
- **What counts.** For each person, message and emoji the latest event wins (put on, or taken off; at the same moment the message key decides, so every device agrees). A person with several devices counts once, and a reaction put on from a linked device is the account's, so it shows as yours on every device and can be taken off from any of them. Reactions from people you blocked or the room banned are not counted.
- **Which emoji.** Only those of `EmojiData`: Unicode's emoji up to version 13.1 (newer ones are drawn as an empty box by older systems), 1,812 of them, without skin tone variants as entries (a skin tone is accepted on an emoji that has them). A receiver ignores a reaction with anything else, so it cannot be an arbitrary string. `EmojiData.Quick` are the thirteen offered first: 👍 💯 ❤️ 😂 😭 🔥 🙏 😍 🎉 👀 🤔 😮 👎. `EmojiData.Search` finds emoji by name and keyword (English, from CLDR); `EmojiData.Groups` are the emoji by group for a picker. The data is made by `tools/emoji/build_emoji_data.py` from Unicode's files; raise its version when the oldest system Yaparr runs on has caught up.
- **Custom emoji as a reaction.** The pictures of [custom emoji](#custom-emoji) (yours, or the room's) can be a reaction too: `room.SendReactionAsync(messageKey, customEmoji, on)` and `DirectChat.SendReactionAsync(messageKey, customEmoji, on)` (get one from `ChatNode.PrepareEmoji`). The reaction carries the emoji's description in its header (`k`: name, hash, size, type and size in pixels, the same limits as in a message), so everyone sees the same picture whichever set it came from, and a node that gets the reaction fetches the picture like one in a message (`AutoFetch`, from the person who sent it or anyone else who has it). A custom emoji is named by its name **and** the hash of its picture (`MessageEnvelope.CustomKey`, `:name:<hash>`), so two pictures that were given one name are two reactions; `ReactionSummary.Custom` holds the description of a custom one. A version from before this finds no standard emoji in it and leaves the reaction out.
- **History.** Reactions have a share of the history of their own, four times the number of messages kept, so many reactions do not push messages out (and the other way round). A reaction on a message that has left the history is not shown.

## Custom emoji

A message can use small pictures written as `:name:` (a name is 2 to 32 lower-case letters, digits or underscores). The message carries what is needed to show them: `MessageEnvelope.Wrap(body, replyTo, mentions, preview, emoji)` puts the emoji it uses in the header (`e`: name, hash, size, type and size in pixels, as an attachment is described), so everyone sees the same picture whether it came from the writer's own emoji or from a room's. The header is covered by the message's signature like the rest, and versions that do not know it show the words with the `:name:` in them.

- **What is carried.** Only the emoji the words use (`CustomEmojiRules.NamesIn`), each once, at most ten (`MaxPerMessage`). The same limits are checked when a header is read: a picture is a PNG, GIF, WebP or JPEG of at most 256 KB and 256 x 256 pixels (`CustomEmojiRules.MaxBytes`, `MaxSide`), and one that does not meet them, or that the words do not use, is left out.
- **The pictures.** Each is stored under its hash like a picture. A node that receives the message fetches the ones it lacks by itself (`AutoFetch`, from the sender or anyone else who has them, under the same rules as any picture), and `ChatNode.FetchEmojiAsync` asks again. `ChatNode.PrepareEmoji(name, bytes)` stores one for sending and describes it for the header.
- **A room's emoji.** `room.Emoji` lists them, `room.AddEmoji(name, bytes)` adds one. They are entries of the room's GIF shelf (see [pictures-and-gifs.md](pictures-and-gifs.md)) that carry a name: the same signed list, passed around the same way, with the same removals (`RemoveGif` by whoever added it, `DeleteGif` by the owner and admins) and the same hiding of people who are blocked or banned. The name is covered by the entry's signature only when there is one, so an entry for a picture of the shelf is exactly what older versions sign and check, and they ignore the entries that have a name. Where two members add the same name, the earlier statement keeps it (then the lower id), so everyone sees one picture; `AddEmoji` refuses a name that is taken or that is a standard emoji's. Emoji do not appear in `room.Gifs`.
- **Your own.** A folder on the device (`EmojiShelf`, `emojishelf` in the app data folder, so it is in a backup) with one picture for each name. Adding a picture that is too big shrinks it first where that can be done (`ImageTools.ForEmoji`).

## Link previews

A message with a web link can carry a card for it: the page's title, a short description and a small square picture. The link stays in the words as it was written; the card is shown below it. `room.SendAsync(text, channelId, replyTo, mentions, preview)` and `DirectChat.SendAsync(text, replyTo, preview)` send one, and `ChatMessage.Parts.Preview` reads it (a `LinkPreview`).

- **Who fetches.** Only the sender's app. `LinkPreviewFetcher` (in `Yaparr.AppCore`) requests the page when the message is sent, reads its Open Graph tags (`og:title`, `og:description`, `og:image`, then the `twitter:` ones, the page's own title and its description tag), fetches the picture, crops it to a square and shrinks it to at most 128 px and 3000 bytes. The people who read the message never contact the site, so it never sees their address. Sending waits up to four seconds for the page; after that the message goes without a card.
- **What is not fetched.** Only the first `http` or `https` link in the words. A page that is not HTML, has nothing to show, or does not answer gets no card. Yaparr's own invitation links never get one. Addresses inside the local network (this computer, a router, a machine next door) are refused, redirects included (`PublicWeb`), so a link cannot make the app probe a home or office network. No cookies and no proxy.
- **How it travels.** In the envelope header next to a reply and the names (`MessageEnvelope`), so the signature covers it and a version from before this one ignores it and shows the words. Limits: title 120 characters, description 220, address 600. If the message would be too long, the picture goes first, then the card; the words are never cut.
- **Trust.** The card is what the sender's app says the page contains; the readers have not looked at the page. A card is only shown when its address is in the words of the same message, the app shows the host name on it, and opening one asks first and shows the full address.
- **Off switch.** Settings, under Network: **Show a preview under the links I send** (on by default; `AppSettings.LinkPreviews`). Turned off, links go as plain words. This only changes what your app sends; cards that others send you are still shown.

## Delivery and reliability

- **Delivery receipts.** `SendAsync` on a room or direct chat returns the message id. `DeliveryChanged` reports `Sending`, `Delivered`, `PartlyDelivered`, `NotDelivered` or `Queued` (nobody connected). Unconfirmed messages are sent again when the recipient reconnects, and `Retry(id)` sends them on demand.
- **Dead sessions.** Data that goes unacknowledged for 15 seconds closes the session so discovery can reconnect. Hello messages carry a random per-run instance id, so a peer that restarted replaces its old session immediately.
- **Redial and peer cache.** A dropped peer is redialed after 1, 2, 4... up to 30 seconds. With `PeerCachePath` set, recently seen room members are remembered across restarts and dialed right away.
- **Network changes.** The node listens for address changes; call `NotifyNetworkChanged()` on resume from sleep. `CheckNetworkAsync()` explains in plain words what works and what does not, and `GetLink(peer)` reports how a person is reached.
- **Messages typed while offline.** A room message is kept and handed over by history sync; a direct message to someone whose conversation exists now waits (also across a restart, `pending.json` holds the ids) and is sent when one of their devices connects, for as long as messages are kept. A first message to someone you have never been connected to still needs a connection.
- **Delivery per recipient.** The status line under a message of yours has a tooltip: who has received it and who has not confirmed yet (about ten minutes after sending it is tracked; the tooltip then keeps what it last showed until the app is closed). In a room of more than 20 people it says how many have received it and names the first 20 that have not.
