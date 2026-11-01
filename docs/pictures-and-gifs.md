# Pictures and GIFs

[Documentation](README.md)

A message can carry a picture or GIF (PNG, JPEG, GIF or WebP, up to 8 MB; you pick the file yourself, there is no GIF search service). The message holds only a small description: a hash of the file (covered by the message signature), its size and dimensions, and a tiny preview. The file itself is fetched on a separate channel from the sender or from any connected room member that already has it, checked against the hash, and kept under `attachments` in the app data folder for as long as the message history. Chat keeps flowing while a file downloads.

- `ChatNode.PrepareAttachment` stores a file for sending; put the result in a message with `Attachment.ToText(caption)`. Received messages carry `ChatMessage.Attachment`, and `FetchAttachmentAsync` / `AttachmentChanged` / `ReadAttachment` fetch and read the file.
- Files up to `MaxAutoFetchBytes` (8 MB) download automatically; while the traffic goes through our own TURN relay the limit is `MaxMeteredAutoFetchBytes` (1 MB) and bigger files wait for a click.
- The Desktop app shrinks and re-encodes still pictures before sending, which also removes camera and location metadata; GIFs are sent as they are.
- Headers are checked before anything is decoded (size, dimensions, GIF frame count), so a malicious file cannot make the app decode something huge.
- A picture opens in a larger view when you click it, "Save picture as" is in the message menu, and animated WebP is sent like a GIF with the same limits.
