# Voice channels

[Documentation](README.md)

Every room has a voice channel you can join and leave whenever you like, with no calls to place or answer. Joining announces you with a small signed statement that spreads through the room like presence (and carries your muted and deafened state); everyone in the channel then connects directly to each other and sends Opus audio (48 kHz, mono, about 32 kbps, 20 ms frames, with forward error correction) as unreliable encrypted datagrams on channel 16. The roster is visible to the whole room, whether or not you have joined.

- `room.JoinVoice()` returns a `VoiceSession`; `Leave()` or `Dispose()` leaves. `room.VoiceParticipants` and `room.VoiceChanged` show who is in the channel.
- The library does not touch audio devices: call `session.PushCapture(samples)` with microphone samples and play what `session.PullPlayback(buffer)` fills in. The Desktop app does this with SoundFlow (miniaudio) in `VoiceAudio.cs`.
- `Activation` is voice activity (with `Sensitivity`), push-to-talk (`PushToTalkHeld`) or open microphone; `Muted`, `Deafened`, `SetVolume` and `SetLocalMute` do what they say. `SpeakingChanged` drives the speaking indicators.
- Each person's audio goes through a jitter buffer that reorders packets, grows when the network is rough, and asks Opus to conceal or recover lost packets.
- Up to about eight people everyone sends to everyone. From `VoiceTreeThreshold` (9) people the channel passes the audio along a tree instead, see "Streams and large rooms".
- **Echo cancellation and noise suppression.** Give `session.Processor` an `IVoiceProcessor` and the session passes it every 20 ms microphone frame (to clean in place before it is encoded) and every block handed to the speakers (the reference echo cancellation needs). The library has no signal processing of its own. The `Yaparr.Voice.WebRtc` project provides `WebRtcVoiceProcessor`, which uses Google's WebRTC audio processing: in a test with a made-up room it removes about 39 dB of echo once settled, keeps your voice at full level while the other side talks, and takes 6 to 14 dB out of steady hiss depending on the level. It costs about half a percent of one core. `WebRtcVoiceProcessor.TryCreate` returns false instead of throwing when the native library cannot load (on Linux it needs glibc 2.38 or newer, so Ubuntu 24.04 or Debian 13 and later), and a processor that throws is switched off by the session (`ProcessorFailed`) while the microphone carries on. The Desktop app turns echo cancellation on and noise suppression to Moderate by default, both changeable in the voice settings. Automatic volume is left off.

## Push-to-talk

Voice settings has "Push to talk" and a key or mouse button to hold: any keyboard key, or the middle, back (button 4) or forward (button 5) button of the mouse (the left and right buttons are not offered). On Windows a low-level keyboard or mouse hook (`GlobalKeyHook`, `GlobalMouseHook`) sees it while another program is in front (not for programs running as administrator); the click or key still reaches that program. Elsewhere it works while the Yaparr window is in front. "Play a short tick when the key goes down" (off by default, and independent of the Notifications sounds) plays a 35 ms tick on the speakers when the key or button goes down, so you know the microphone is open without looking.

## Volume per person

The speaker button next to a person in the voice list opens a 0 to 200% slider; it is kept by account in the settings.

## Large calls

From nine people the audio travels along a tree, see [Streams and large rooms](streams-and-large-rooms.md). Nothing is sent directly to someone who is deafened (a tree still reaches them, since they may pass audio on), and in a call with more than 8 people only the 8 who are saying the most are decoded and mixed (judged by the size of their packets); someone muted, or everyone when you are deafened, is not decoded at all.

## When the connection is lost

In a call an orange bar across the top says the connection to the call is lost and Yaparr is trying again (with more after a minute), and a falling three-note sound plays; a rising one plays and the bar goes when it is back. It counts as lost when you could reach someone in the call a moment ago and now reach nobody for four seconds, and the system saying the network is gone counts at once. Next to a person in the voice list "reconnecting…" shows once they have been unreachable for five seconds. In a call everyone pings the others every couple of seconds, and someone silent for six seconds counts as unreachable if they are seen to ping that way, fourteen if not (older versions only ping every ten; a connection otherwise looks established for 35); people leaving the call, and joining one where nobody answers yet, say nothing (`ConnectionWatch`, `ChatRoom.VoiceReach`). Watching a screen, "Lost the connection to the screen. Reconnecting…" shows over the picture with the same two sounds when the stream loses its parent. Outside a call and a screen watch, the "reconnecting…" under a person's name is all there is, on purpose.
