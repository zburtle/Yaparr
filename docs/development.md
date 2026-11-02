# Development

[Documentation](README.md)

## Layout

| Folder | What it is |
|---|---|
| `src/Yaparr` | The library: discovery, transport, chat, voice, media. |
| `src/Yaparr.Screen` | Screen capture, encoding and decoding with FFmpeg. |
| `src/Yaparr.Voice.WebRtc` | `WebRtcVoiceProcessor`: echo cancellation and noise suppression. |
| `samples/Yaparr.Cli` | A command line chat. |
| `samples/Yaparr.Desktop` | The desktop app. |
| `tests/Yaparr.Tests` | The tests. |
| `tools` | Network test agent and runner, and the screen sharing demo. |
| `installer` | The MSI and .deb builds. |

## Tests

`dotnet test` runs everything.

## Debugging the command line

`samples/Yaparr.Cli/Properties/launchSettings.json` has profiles for Visual Studio / Rider
(or `dotnet run --launch-profile "<name>"`): alice/bob/carol in a shared local room,
a "wait for a DM" instance, and one on the real DHT. The local ones pass `--local`, which swaps the DHT for a
temp-directory discovery so instances on one machine find each other in seconds. Start one profile at a time,
e.g. from separate terminals. `--history` keeps each name's group history on disk under the temp directory: run
alice and bob, stop bob, chat as alice, then start bob again to see the `missed` messages arrive.

## Network tests

`tools/` has a headless agent and a tool that runs it on DigitalOcean machines (with simulated NATs, packet loss and failures) and checks the result; see [tools/README.md](../tools/README.md).

## Licence

Everything is GPL-3.0-or-later (see [LICENSE.txt](../LICENSE.txt)), and a contribution is under the same licence. Anything new you depend on has to be compatible with GPL version 3: MIT, BSD, Apache-2.0, LGPL and similar are fine; GPL-2.0-only, proprietary and "non-commercial" licences are not.

## The FFmpeg build

Screen sharing runs a trimmed FFmpeg that this repository builds: `ffmpeg/build.sh` and `ffmpeg/verify.sh`, run by the `FFmpeg build` workflow on a Windows machine in MSYS2. A pull request that touches `ffmpeg/` builds and checks it; running the workflow by hand with a tag such as `ffmpeg-2` publishes the build, its exact source and its licences as a pre-release. `ffmpeg/README.md` has what is in the build, how to change it and how to pin a new one in `FfmpegPackage.Default`. Nothing GPL or "nonfree" may go into it.

## Releases

Every push to `master` runs `.github/workflows/release.yml`, where [semantic-release](https://semantic-release.org) reads the commit messages since the last tag. `fix:` makes a patch release, `feat:` a minor one, and a `!` after the type (`feat!:`) or a `BREAKING CHANGE:` footer a major one; `docs:`, `chore:`, `test:`, `ci:` and `refactor:` release nothing. When a release is due, the MSI and both .deb packages are built with that version, then the tag, the release notes and the downloads are published. A commit with no such prefix is ignored, so use them from now on.

Pull requests are squash-merged, so the pull request title becomes the commit on `master`. A check fails a title that is not a conventional commit, such as `fix: ...` or `feat: ...`.
