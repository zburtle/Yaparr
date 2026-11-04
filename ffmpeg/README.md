# The FFmpeg Yaparr runs

Yaparr shares and shows screens by running FFmpeg as a separate program (see `src/Yaparr.Screen`). This folder builds that FFmpeg: a static `ffmpeg.exe` for Windows x64 with only the parts Yaparr uses, under the LGPL.

## What is in it

| For | Parts |
|---|---|
| VP8 in software | `libvpx` (encoder), `vp8` (decoder), the IVF container |
| H.264 on the graphics card | `h264_nvenc` (NVIDIA), `h264_amf` (AMD), `h264_qsv` (Intel) |
| Viewing H.264 | the `h264` decoder and parser, and the `h264_d3d11va`, `h264_d3d11va2` and `h264_dxva2` hardware decoders (used only when the viewer is asked to decode on the graphics card) |
| Capture | the `gfxcapture` filter (Windows Graphics Capture), `hwdownload`, `format`, `scale` |
| Input and output | the `pipe` protocol, raw pictures, the raw H.264 stream |
| Tests only | `lavfi`, the `color` and `testsrc2` filters, the `null` muxer |

Nothing else: no GPL part (no x264, no x265), nothing "nonfree", no network, no other codec. `verify.sh` checks all of that on every build and fails if a GPL or nonfree flag, an unexpected codec or a DLL that would have to be shipped with it shows up.

## Building and publishing

The `FFmpeg build` workflow (`.github/workflows/ffmpeg.yml`) runs `build.sh` in MSYS2 on a Windows machine, then `verify.sh`. A pull request that changes this folder builds and checks it; the result is an artifact of the run.

To publish a build, run the workflow by hand and give it a release tag such as `ffmpeg-2`. That creates a **pre-release** (so the updater's "latest release" never picks it) holding:

- `ffmpeg-yaparr-g<commit>-win64.zip`: `bin/ffmpeg.exe`, `LICENSE.txt` (LGPL 2.1), `SOURCE.txt`, `BUILD-INFO.txt` (how it was configured, the versions of what was used) and `licenses/` (libvpx, oneVPL, AMF and NVIDIA headers, the compiler runtime);
- `ffmpeg-g<commit>-source.tar.xz`: the exact FFmpeg source it was built from;
- `SHA256SUMS.txt`.

The source file is what the LGPL asks for when the binary is passed on, so it always goes up with the build.

## Using a build

1. Run the workflow with a release tag.
2. Download the zip and run the integration tests against it: `set YAPARR_FFMPEG=...\bin\ffmpeg.exe` then `dotnet test tests/Yaparr.Tests --filter FfmpegIntegration`, and try `tools/Yaparr.ScreenDemo` (see `src/Yaparr.Screen/README.md`).
3. Put the new URL, SHA-256, size and the paths inside the zip into `FfmpegPackage.Default` (`src/Yaparr.Screen/FfmpegInstaller.cs`).

## Changing what is in it

Edit the `./configure` options in `build.sh` and the matching checks in `verify.sh`, and open a pull request: the build will say if something is missing. Keep to LGPL parts: anything GPL (x264, x265 and others) or "nonfree" (fdk-aac) must not go in. A different FFmpeg commit can be built by giving it to the workflow; Yaparr needs one with the `gfxcapture` fixes of 24 October 2026.
