# Yaparr.Screen

Shares a monitor or a window with a room, and shows a share. FFmpeg does the capture, the encoding and the decoding, as a separate program.

## Which FFmpeg

Yaparr needs an FFmpeg **LGPL build (no GPL parts such as x264) with `libvpx` and the `gfxcapture` filter (Windows Graphics Capture) as fixed on 24 October 2026**. The app does not use whatever FFmpeg it finds first and hope: the build it downloads is one exact file, named in `FfmpegPackage.Default` (`FfmpegInstaller.cs`) with its SHA-256, and a download that does not match is thrown away. An FFmpeg you supply yourself (`YAPARR_FFMPEG`, an `ffmpeg` folder next to the app, or the PATH) is used as it is, and one that is too old fails with a message saying so.

**The Windows installer bundles it.** `installer/build.ps1` fetches the pinned file (`Yaparr.ScreenDemo --install-ffmpeg`, which runs `FfmpegInstaller` and so checks the hash) and puts `ffmpeg.exe`, `LICENSE.txt`, `SOURCE.txt`, `BUILD-INFO.txt` and the `licenses` folder into an `ffmpeg` folder next to the app, where `FfmpegLocator` looks first; this makes the MSI only a few MB bigger (63.5 MB in total; with the 175 MB general-purpose build it was 105.8 MB, and 111 MB for v1.9.0). The file itself is the asset of the Yaparr release `ffmpeg-1` (a pre-release, so the updater's "latest release" never picks it). The in-app download remains for builds that do not bundle it.

**No numbered release works yet.** FFmpeg 8.0 has no `gfxcapture`. 8.1.3 and 9.0.2 (and BtbN's `n8.1-latest` and `n9.0-latest` builds, which follow those branches) have it, but capture one picture and then deliver nothing: four fixes to `vsrc_gfxcapture` (the timestamp default, the callback state, a device-context lock and the `dup_frames` option) were made on the development branch on 24 October 2026 and are in neither release branch. Measured on 30 October 2026 with a moving picture on the monitor, the 9.0 branch build sent one frame in ten seconds, and the development build `N-128212` delivered 9.8, 14.8 and 29.7 pictures a second when 10, 15 and 30 were asked for. So the pinned build is `N-128212`, a development-branch build. When a numbered release contains those four fixes (look for 8.1.4 or 9.0.3, and `git log -- libavfilter/vsrc_gfxcapture*` for the commits), test it as below and move to it.

## Yaparr's own build

The FFmpeg Yaparr pins is one it builds itself, in its own repository, [Yaparr.FFmpeg](https://github.com/zburtle/Yaparr.FFmpeg) (`build.sh`, `verify.sh` and a README that explains it): its `Build` workflow compiles the commit above with only what Yaparr uses (libvpx, the VP8 and H.264 decoders, the NVIDIA, AMD and Intel encoders, Graphics Capture and the plumbing around them) and checks it. It is a single static `ffmpeg.exe` of about 9.5 MB (3.4 MB zipped), where the BtbN build it replaced was 175 MB zipped. Every build is published as a release named `ffmpeg-N` (up to `ffmpeg-3` as pre-releases of this repository, which stay there) together with the exact FFmpeg source it was made from and the licences of what is built in, which is what the LGPL asks for when the program is passed on. The downloader keeps those notices next to the program (`FfmpegPackage.NoticesRoot`).

## Pinning a build

Do this for the first pin and whenever a newer FFmpeg is wanted. The file is the zip of a published `ffmpeg-N` release (see the README of [Yaparr.FFmpeg](https://github.com/zburtle/Yaparr.FFmpeg) for how to make one, and how to build another commit), so the exact file that passes below is the file that gets hosted.

1. Download the zip from the release and unpack it.
2. Run the integration tests against it, on a Windows PC with a monitor:

   ```
   set YAPARR_FFMPEG=C:\path\to\ffmpeg-...\bin\ffmpeg.exe
   dotnet test tests/Yaparr.Tests --filter FfmpegIntegration
   ```

   They capture a real monitor with the exact command Yaparr uses, check that the stream is VP8 in IVF (and, on a computer where the graphics card can encode, H.264) with a key frame first and the size it says, and decode the frames back into a picture. A build without the fixes fails them (the capture stops after one frame). They do nothing when there is no FFmpeg or no monitor, so the build server skips them.
3. Try `tools/Yaparr.ScreenDemo` on a monitor and a browser window (`--list` shows the numbers), with something moving on screen, at 10, 15 and 30 pictures a second: the viewer should get about the rate asked, with nothing dropped or lost.
4. Take the SHA-256 and the size of the zip from the release (`SHA256SUMS.txt`).
5. Put the version, URL, SHA-256, size, the paths inside the zip and its notices folder (`NoticesRoot`) into `FfmpegPackage.Default`, and say in the commit which FFmpeg version it is.

## Encoding

`ScreenOptions.Codec` is `Auto` unless a caller says otherwise. The sharer then tries **H.264 on the graphics card**, NVIDIA's `h264_nvenc` first, then AMD's `h264_amf`, then Intel's `h264_qsv` (`ScreenOptions.Encoder` names one), and when FFmpeg stops before the first picture (no such card, an old driver, all the encoder sessions of the card in use) it tries the next, and last **VP8 in software** (`libvpx`). Intel's encoder takes NV12 and not planar YUV, so its command converts to that. `ScreenSharer.Codec` and `ScreenSharer.Encoder` say what is in use and `FallbackReason` why each card that was tried was not used. `H264Reader.RepeatParameterSets` puts the parameter sets back in front of an IDR picture that comes without them, so a late viewer can always start on it. Nothing is probed beforehand: starting the encoder is the test, and a failure costs well under a second.

- **H.264** is written as an Annex B byte stream with an access unit delimiter in front of every picture (`-aud 1`), no B-frames, a key frame (an IDR picture with its parameter sets) every `KeyFrameSeconds`, and a variable rate (a fixed rate would pad a still screen with filler up to the bitrate). `H264Reader` cuts the stream into pictures, each released when the next delimiter arrives, so the sender is one picture (100 ms at 10 fps) behind the encoder. `H264.TryGetSize` reads the picture size from the sequence parameter set, cropping included.
- **VP8** is written as IVF, as before.
- A viewer does not need to be told: the key frame says which codec it is (`ScreenCodecs.TryGetKeyFrame`), and a new key frame with another codec or size restarts the decoder. The viewer's FFmpeg decodes both in software; the H.264 decoder is given a delimiter after every picture (`H264.EndMarker`) so that it shows a picture as soon as it arrives instead of one picture late.
- Measured on 30 October 2026 on an RTX 3060 (same moving picture, the demo sharing to itself): at 10 fps and 800 kbit/s asked, H.264 sent 543 kbit/s and VP8 756 kbit/s, and both delivered 9.4 to 9.5 pictures a second; at 30 fps and 2000 kbit/s H.264 sent 655 and VP8 1153 kbit/s, and both delivered 29.5 to 29.8. The sending FFmpeg alone (capture, conversion and encoding, six seconds per run, repeated) used 8 to 18% of a core with H.264 and 13 to 66% with VP8 at 30 fps, and about the same with both at 10 fps; repeat runs of one setting differed by up to three times, so take it as a rough size only. The rest of what the demo reports is the viewer's decoding. Most of what is left on the sender is the download of every picture from the graphics card for scaling, which is the next thing to remove (see BACKLOG.md).

## Things that went wrong once

- `-fflags nobuffer` on the decoder fails to decode frames that arrive in a burst, which is what a late joiner gets from its parent's cache. It is not used (`FfmpegArgs.Decode`, with a test).
- The older screen-copying capture (`gdigrab`) returns a black picture for hardware-accelerated windows such as browsers. It was removed in favour of Graphics Capture.
