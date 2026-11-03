# Installer and updates

[Documentation](README.md)

## Installer

`.\installer\build.ps1` publishes the desktop app self-contained (no .NET install needed on the target machine) and wraps it in `artifacts\Yaparr-<version>-x64.msi` (about 48 MB) using WiX 5, which is restored as a local dotnet tool. The MSI installs per user to `%LOCALAPPDATA%\Programs\Yaparr` by default with a Start menu shortcut and needs no administrator rights. It is a single file: double-clicking opens a wizard (welcome, install folder, progress, finish with a "Launch Yaparr" checkbox), and `msiexec /i Yaparr-1.0.0-x64.msi /qn` installs it silently. Installing a newer version upgrades in place and uninstalling leaves your chats and identity in `%LOCALAPPDATA%\Yaparr`. Pass `-Version` to set the version; the default is the `Version` in `samples/Yaparr.Desktop/Yaparr.Desktop.csproj`, and releases pass the version worked out from the tag (see [Releases](development.md#releases)). The installer is unsigned, so Windows SmartScreen will warn on first run, and Windows Firewall will ask to allow the app (allow private networks for LAN discovery; allow public for internet peers).

## Flatpak

Each release also has a Flatpak bundle for 64-bit Intel/AMD and for ARM (`yaparr_<version>_amd64.flatpak` and `yaparr_<version>_arm64.flatpak`). Install one with `flatpak install --user yaparr_<version>_amd64.flatpak` and start it from the menu or with `flatpak run com.yaparr.Yaparr`. The app id is `com.yaparr.Yaparr`.

- It is built by `installer/build-flatpak.sh` from the manifest in `installer/flatpak/`: the self-contained app, plus a small FFmpeg built from source that only decodes VP8 and H.264, so watching a screen works without installing anything else.
- The sandbox has network access (the DHT, hole punching, port mapping and local discovery all need it), X11 or XWayland for the window, and PulseAudio (or PipeWire's PulseAudio service) for the microphone and speakers. It cannot see your home folder; pictures are picked through the file dialog.
- Its data is in `~/.var/app/com.yaparr.Yaparr/data/Yaparr`, not `~/.local/share/Yaparr`, so a profile from the `.deb` is not found. Use File > Back up profile in the old app and Restore a backup in the new one.
- **Updates come from a hosted repository** at `https://flatpak.yaparr.com` (the `gh-pages` branch, served by GitHub Pages), signed with a key whose public half is `installer/flatpak/yaparr-flatpak.gpg`. Install from it with `flatpak install https://flatpak.yaparr.com/com.yaparr.Yaparr.flatpakref`, and `flatpak update` or the software centre keeps it current. A bundle file from a release has the same address built in, so it updates too. The app itself does not check for updates inside a Flatpak; it says so under Help > Check for updates.
- A release signs the bundles and adds them to the repository when the repository secret `FLATPAK_GPG_PRIVATE_KEY` holds the private key; without it the bundles are unsigned and do not update. Only the last three versions are kept in the repository. The branch is rewritten as a single commit each time, so it does not grow with history.
- The app's branch in the repository is `stable`; `installer/publish-flatpak-repo.sh` adds builds to the site.
- The `Flatpak build` workflow builds the bundles for a pull request that changes the packaging and when started by hand, installs them and checks that the FFmpeg and the audio libraries are inside. A release builds the same bundles and attaches them.

## FFmpeg in the installer

The Windows installer carries the FFmpeg that screen sharing runs (`installer/build.ps1` fetches the pinned build, checks its hash, and puts it in an `ffmpeg` folder next to the app), together with its licence, its note about where the source is and the licences of what is built in. Where the app has no copy, it offers to download the same pinned build. See [Sharing a screen](screen-sharing.md).

## Updates

The desktop app updates itself from this repository's releases, and the download shows a progress bar and can be cancelled. An update is only taken from this repository's release downloads over https, must have a published SHA-256 (an asset without one is refused), is downloaded under a `.part` name and only gets its real name after the checksum matches, and a mismatch deletes it. Tested against v1.7.1: good download, wrong checksum, no checksum, foreign address and a missing file.

**The update dialog** shows what is in the release as short lines under plain headings (New, Fixed, and so on), in a list that scrolls. `ReleaseNotes` makes them from the text of the GitHub release by dropping the version heading and the issue and commit links that semantic-release adds.

**Between the check and the install.** On Linux the package is copied where only root can write, checked again there and installed from that copy. On Windows the MSI sits in the user's own folder, so just before msiexec is started the file is opened so that no other program can write to it, delete it or rename it, and its checksum is taken again through that same handle (`UpdateVerifier.OpenVerifiedAsync`). The handle is kept until msiexec has the package open itself, asked of the Windows Restart Manager (`FileHolders`), for at most 15 seconds. This closes the gap in which another program of the same user could swap the file after the check. It is not a security boundary: such a program can also change the installed app, which lives in the same user's folder.

What the updater does not cover is listed in the [issues](https://github.com/zburtle/Yaparr/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3A+security%22).
