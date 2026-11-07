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

Group messages are signed by their author (ECDSA, bound to the room) and kept in a bounded history
(500 messages / 7 days by default, in memory or persisted with `HistoryStore = new FileHistoryStore(dir)`). When two
members connect, each sends the keys of the messages it holds and the other replies with the ones missing, so a
member who was offline gets what they missed from whoever was online, and anyone can pass on a message without
being able to forge or alter it. Catch-up messages arrive via `MessageReceived` with `IsCatchUp = true` (sort by
`Timestamp`); `room.GetHistory()` returns everything held. Newcomers also receive recent history; set
`SyncHistory = false` to disable. One-to-one chats are not synced. Stored history is plain text on disk.

## Replies and mentions

A message can answer an earlier one and name people. `room.SendAsync(text, channelId, replyTo, mentions)` and `DirectChat.SendAsync(text, replyTo)` send it, and `ChatMessage.Parts` (or just `Body` for the words) reads it: `ReplyTo` is the `ChatMessage.Key` of the message it answers, and `Mentions` are the people named, each a `Mention` with their account and the name as it was written after the @ (so it can be found in the words even when it holds a space, or the person has renamed since). Up to 20 people can be named in one message. `Mention.Everyone` names the whole room; it only counts when the author is an admin or the owner (`ChatRoom.Names(message, account)` says whether a message names you). A picture can answer a message too: `Attachment.ToText(caption, replyTo)` puts the key in the picture's description, and `ChatMessage.Parts.ReplyTo` reads it.
- **Asking for a message.** The history handed over when someone connects is the newest 500 messages; a room keeps up to 500 per channel for 7 days. `room.RequestMessages(keys)` asks up to three connected members for messages by key (at most 20) with a `want` message, and they answer with the ones they hold, which arrive through `MessageReceived` like any caught-up message. A member answers one request every two seconds. Messages nobody holds any more, such as ones older than seven days, cannot be had.

- **How it travels.** Inside the text of the signed message, behind a marker, the way an attachment does (`MessageEnvelope`). The signature covers it, so nobody can add a reply or a name to someone else's message, and every version still accepts the message; a version from before this one shows the marker next to the words.
- **The key.** It comes from who wrote the message and its id, so nobody else can make a message that has it. A reply points at that key and does **not** copy the words it answers: the app looks the original up in its own history, so a reply can never misquote. When the original is not there (older than the history kept), the reply says "an earlier message that is not here".
- **In the app,** see [The desktop app](desktop-app.md#replies-and-mentions).

## Reactions

An emoji can be put on a message, and taken off again. `room.SendReactionAsync(messageKey, emoji, on, channelId)` and `DirectChat.SendReactionAsync(messageKey, emoji, on)` send it; `room.GetReactions()` and `DirectChat.GetReactions()` give a `ReactionSet` (`For(messageKey)` is the emoji on a message, each with the accounts that put it there, `Has(...)` asks about one account); `ReactionsChanged` (the message key) is raised when any of it changes, including when reactions arrive with the history.

- **How it travels.** A reaction is a message of its own, signed and passed on like any, with the message it is on and the emoji in the envelope header (`MessageEnvelope.WrapReaction`), so rooms, direct chats, catch-up, linked devices, bans and blocks, rate limits and history all work for it without anything new. It is not shown as a message: it never reaches `MessageReceived` or `GetHistory`, and is not unread. A version that does not know reactions shows "reacted 👍".
- **What counts.** For each person, message and emoji the latest event wins (put on, or taken off; at the same moment the message key decides, so every device agrees). A person with several devices counts once. Reactions from people you blocked or the room banned are not counted.
- **Which emoji.** Only those of `EmojiData`: Unicode's emoji up to version 13.1 (newer ones are drawn as an empty box by older systems), 1,812 of them, without skin tone variants as entries (a skin tone is accepted on an emoji that has them). A receiver ignores a reaction with anything else, so it cannot be an arbitrary string. `EmojiData.Quick` are the thirteen offered first: 👍 💯 ❤️ 😂 😭 🔥 🙏 😍 🎉 👀 🤔 😮 👎. `EmojiData.Search` finds emoji by name and keyword (English, from CLDR); `EmojiData.Groups` are the emoji by group for a picker. The data is made by `tools/emoji/build_emoji_data.py` from Unicode's files; raise its version when the oldest system Yaparr runs on has caught up.
- **History.** Reactions have a share of the history of their own, four times the number of messages kept, so many reactions do not push messages out (and the other way round). A reaction on a message that has left the history is not shown.

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
