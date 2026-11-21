# Voice settings

- **Automatic volume (for others):** turns up people with a quiet microphone and down people who are loud, as they talk. Your slider for each person still applies on top.
- **Automatic volume (your microphone):** raises a quiet microphone and lowers a loud one before it is sent. The volume changes only while you talk and is held in pauses.
- **Noise suppression:** takes steady noise such as fans and hiss out of your microphone. A headset works best and needs no echo cancellation. *Yaparr Custom* is WebRTC's noise suppression, with a strength you set. *RNNoise* is a small neural network trained on speech: it has no strength to set, and takes out more than steady noise (keys and voices in the background too), at the cost of a little more processing.
- **Click suppression:** removes short, sharp clicks such as keys and mouse buttons, which noise suppression does not. Adds about 10 ms of delay to your voice.
- **Microphone threshold:** your microphone is sent when its level (the bar) goes above the slider. Move the slider just above the level you see while you are quiet.

See [Voice channels](../voice.md) for how it works underneath.
