# Pictures and GIFs

[Documentation](README.md)

A message can carry a picture or GIF (PNG, JPEG, GIF or WebP, up to 8 MB; you pick the file yourself, there is no GIF search service). The message holds only a small description: a hash of the file (covered by the message signature), its size and dimensions, and a tiny preview. The file itself is fetched on a separate channel from the sender or from any connected room member that already has it, checked against the hash, and kept under `attachments` in the app data folder for as long as the message history. Chat keeps flowing while a file downloads.

- `ChatNode.PrepareAttachment` stores a file for sending; put the result in a message with `Attachment.ToText(caption)`. Received messages carry `ChatMessage.Attachment`, and `FetchAttachmentAsync` / `AttachmentChanged` / `ReadAttachment` fetch and read the file.
- Files up to `MaxAutoFetchBytes` (8 MB) download automatically; while the traffic goes through our own TURN relay the limit is `MaxMeteredAutoFetchBytes` (1 MB) and bigger files wait for a click.
- The Desktop app shrinks and re-encodes still pictures before sending, which also removes camera and location metadata; GIFs are sent as they are.
- Headers are checked before anything is decoded (size, dimensions, GIF frame count), so a malicious file cannot make the app decode something huge.
- A picture opens in a larger view when you click it, "Save picture as" is in the message menu, and animated WebP is sent like a GIF with the same limits.

## The GIF shelf

There is no GIF search service, so there is nothing to look up and nobody learns what you search for. Instead each device has a **GIF shelf**: pictures and GIFs you keep to send again with one click or tap.

- Desktop: the **GIF** button next to the paperclip opens the shelf. Click a picture to send it (what is typed in the box goes along as the caption), right-click it to take it off the shelf, **Add…** puts files on it, and **Add to my GIF shelf** in the menu of a picture in a chat keeps that picture.
- Android: the **GIF** button next to **+** opens it. Tap to send, hold to remove, **Add a picture** to choose a file, and **Add to my GIF shelf** in the long-press menu of a picture.
- The most recently added or sent picture is first. Adding the same file twice keeps one copy.
- It is a folder (`gifshelf`) in the app data folder, so it is included in a backup and is never shared. There is no limit on how many pictures or how big; sending one is still bound by the usual limits above.
- In code it is `GifShelf` in `Yaparr.AppCore`: files are named by their content, so a name that is not one the shelf made is never touched.
