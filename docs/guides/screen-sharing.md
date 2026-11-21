# Sharing a screen

## Sharing a window or a screen

A window is shared even when other windows are on top of it. A minimised window stops updating, and one that is resized keeps its shape inside the size it had when you started.

## Who can watch

Everyone in the room can watch. Each viewer passes the picture on to a few others, so a big room does not need a fast connection from you. Higher quality needs a faster upload.

## Picture formats

**AV1** looks better than H.264 at the same connection speed, but only some graphics cards can make it (NVIDIA RTX 40 and newer, AMD RX 7000 and newer, Intel Arc and newer). **H.265** also looks better than H.264, but only a graphics card makes it (NVIDIA, AMD or Intel; the card's maker has to support H.265 live). If yours cannot, Yaparr shares in H.264 instead.

Watchers need an up to date Yaparr, and a phone that can play the format. On a computer, AV1 and H.265 play in software if the graphics card cannot decode them, though a busy processor may struggle at high resolutions.

## Quality that adjusts

If most of the room is losing frames, the picture is sent at a lower bitrate until they catch up, then back up again. Everyone gets the same stream, so one viewer on a bad connection is not enough to lower it for the others.

## Sound

The sound is shared too: a window's own sound, or for a monitor everything you hear except Yaparr, so the voice chat is not sent back. Needs Windows 10 version 2004 or newer. On Linux, the desktop's picker may not say which program a window belongs to; to share only one program's sound, pick it in the list (it must be playing now).

See [Sharing a screen](../screen-sharing.md) for how it is built.
