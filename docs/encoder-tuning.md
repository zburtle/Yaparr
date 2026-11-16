# Tuning the encoder (stage 5 of #225)

What the rate settings of the H.264 encoder do to the size of a key frame, to the burst a link has to carry, to the quality and to the bitrate that is really made. Measured with
`tools/Yaparr.EncoderTuning`, on NVIDIA's encoder (an RTX 3060) inside the app through FFmpeg's libraries, the same encoder and options the app uses.

## The settings

| setting | environment variable | default | before | what it is |
|---|---|---|---|---|
| rate buffer | `YAPARR_ENCODER_BUFFER` | 0.5 | 1.5 | the VBV buffer in seconds of the bitrate: how much the encoder may put out ahead of its average. A key frame can be as big as the buffer allows. |
| ceiling | `YAPARR_ENCODER_MAXRATE` | 1.0 | 1.5 | the maximum rate as a multiple of the bitrate |
| key spacing | `YAPARR_KEY_SECONDS` | 2 | 1 | seconds between key frames the encoder makes by itself (a viewer that asks gets one at once, whatever this says) |

They are options of the share (`ScreenOptions.BufferSeconds`, `MaxRateMultiple`, `KeyFrameSeconds`) and are kept across the levels of adaptive quality; the buffer is a time, so at a lower
bitrate it is a smaller number of bytes. The defaults are the ones the measurements below chose (0.5 s, 2 s, 1.0); before, the encoders had 1.5 s, 1 s and 1.5. A variable changes them for a run.

## The clips

Made by the tool, the same every time (nothing from anyone's screen). They stand for the three kinds of share, and are *synthetic*: they show how the settings compare, not what a given
picture will look like.

- **desktop**: a page of text scrolled three lines every two seconds, a cursor, typing, a coloured title bar; still most of the time.
- **video**: grainy, soft motion over the whole picture and two bright shapes moving.
- **game**: sharp texture panning fast in two layers, sprites, a fixed overlay, a cut every four seconds.

For each run it reports the bitrate made, the key frame sizes, the biggest frame, the most put out in any tenth of a second (a burst is what a weak link loses packets in: on the
Wi-Fi viewer of the first real runs the link was clean to about 3 Mbit/s and lossy from 3.7), and the luma PSNR of what comes out against the clip. 12 seconds each.

## What came out

**The buffer sets the size of a key frame, and with it the burst.** (key spacing 1 s, ceiling 1.5, 1080p30 at 4 Mbit/s, desktop)

| buffer | key frame | most in 100 ms | made | PSNR |
|---|---|---|---|---|
| 0.25 s | 95 KB | 8.8 Mbit/s | 1.6 Mbit/s | 51.6 dB |
| 0.5 s | 146 KB | 13.8 Mbit/s | 1.5 Mbit/s | 54.8 dB |
| 1 s | 189 KB | 17.8 Mbit/s | 1.8 Mbit/s | 56.3 dB |
| 1.5 s (before) | 189 KB | 17.8 Mbit/s | 1.7 Mbit/s | 56.3 dB |

The same at 720p30 and 2 Mbit/s: key frame 48, 105, 126, 126 KB; burst 4.8, 9.5, 11.9, 11.9 Mbit/s. Beyond one second the buffer does nothing: the encoder does not need more.

- A smaller buffer makes the key frame smaller (and worse at first; the pictures after it refine it). On the desktop clip that costs nothing anyone could see (45 to 56 dB is far above where
  PSNR shows). On the video clip the PSNR does not move at all (40.8 dB at every setting: its grain, not the buffer, limits it). On the game clip (1080p60 at 6 Mbit/s): 35.4, 35.2, 34.9 and
  33.8 dB at 1.5, 1, 0.5 and 0.25 s, so 0.5 costs 0.5 dB and 0.25 costs 1.6.
- Game key frames go from 298 KB (burst 35 Mbit/s) at the current setting to 260 KB (26) at 0.5 s and 135 KB (19) at 0.25 s.

**Key spacing: on a still screen the key frames are most of what is sent.** Desktop, 1080p30 at a 4 Mbit/s target, buffer 1.5: 1.74 Mbit/s with a key frame every second, 0.89 with one
every two seconds, 0.89 with four. The key frame is 189 KB and the P frames next to nothing, so a key frame a second is 1.5 Mbit/s of it. Key spacing does not change a key frame's size.
A viewer that loses frames does not wait for the next one: it asks, and the sharer answers on the very next picture (16 to 63 ms in the real logs), so a longer spacing costs a joiner or
a viewer that does not ask a little and saves half of a static share. For the game clip a longer spacing barely changes the bitrate.

**The ceiling matters little once the buffer is small, except for overshoot.** At buffer 0.5 and key spacing 2 s the ceiling 1, 1.25, 1.5 and 2 give a key frame within a few KB of each other
and the same PSNR within 0.3 dB for the desktop and game clips; for the video clip a ceiling of 1.0 makes 4.04 Mbit/s for a 4 Mbit/s target where 1.5 makes 4.39 (10% over), and a lower burst
(11.7 against 14.6 Mbit/s in 100 ms). On a link that carries just so much, a stream that is 10% over its bitrate is the difference between clean and lossy.

## What is set, and what is not known

From these numbers the defaults are now: a buffer of **0.5 s**, a key frame every **2 s**, a ceiling of **1.0**. Together they make the key frame about a quarter smaller, the burst about a fifth lower, the bitrate
true to what was asked, and a static share cost half as much, for 0.3 to 0.5 dB on the hardest clip. A buffer of 0.25 s halves the burst and costs 1.6 dB on a game: for a link that
cannot take more, and a person who can try it by setting the variable.

**Not known:** whether this makes a weak link clean. The numbers say the burst gets smaller; whether the viewer on the Wi-Fi link sees fewer lost packets with it is something only that
link can say, so the new defaults are a bet on the numbers. To compare, start Yaparr with the old values (for example in PowerShell:
`$env:YAPARR_ENCODER_BUFFER = "1.5"; $env:YAPARR_KEY_SECONDS = "1"; $env:YAPARR_ENCODER_MAXRATE = "1.5"; & "$env:LOCALAPPDATA\Programs\Yaparr\Yaparr.Desktop.exe"`), share the same window at the
same level, and compare what the viewer's log says about packets missing and never recovered. The share's setup line in the log says the buffer and ceiling used.
Not measured: the VA-API encoder (same options, a card that cannot be tried here), and the pacing of a key frame onto the link (`YAPARR_FRAME_PACE`), which is a separate setting.

## Running it again

```
set YAPARR_FFMPEG_LIBS=<folder with FFmpeg's libraries>
dotnet run --project tools/Yaparr.EncoderTuning -c Release -- --clips desktop,video,game --buffer 0.25,0.5,1,1.5 --key 1,2,4 --maxrate 1.5 --seconds 12 --csv out.csv
```
