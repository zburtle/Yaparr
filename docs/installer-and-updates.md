# Installer and updates

[Documentation](README.md)

## Installer

`.\installer\build.ps1` publishes the desktop app self-contained (no .NET install needed on the target machine) and wraps it in `artifacts\Yaparr-<version>-x64.msi` (about 48 MB) using WiX 5, which is restored as a local dotnet tool. The MSI installs per user to `%LOCALAPPDATA%\Programs\Yaparr` by default with a Start menu shortcut and needs no administrator rights. It is a single file: double-clicking opens a wizard (welcome, install folder, progress, finish with a "Launch Yaparr" checkbox), and `msiexec /i Yaparr-1.0.0-x64.msi /qn` installs it silently. Installing a newer version upgrades in place and uninstalling leaves your chats and identity in `%LOCALAPPDATA%\Yaparr`. Pass `-Version` to set the version; the default is the `Version` in `samples/Yaparr.Desktop/Yaparr.Desktop.csproj`, and releases pass the version worked out from the tag (see [Releases](development.md#releases)). The installer is unsigned, so Windows SmartScreen will warn on first run, and Windows Firewall will ask to allow the app (allow private networks for LAN discovery; allow public for internet peers).

## FFmpeg in the installer

The Windows installer carries the FFmpeg that screen sharing runs (`installer/build.ps1` fetches the pinned build, checks its hash, and puts it in an `ffmpeg` folder next to the app), together with its licence, its note about where the source is and the licences of what is built in. Where the app has no copy, it offers to download the same pinned build. See [Sharing a screen](screen-sharing.md).

## Updates

The desktop app updates itself from this repository's releases, and the download shows a progress bar and can be cancelled. An update is only taken from this repository's release downloads over https, must have a published SHA-256 (an asset without one is refused), is downloaded under a `.part` name and only gets its real name after the checksum matches, and a mismatch deletes it. Tested against v1.7.1: good download, wrong checksum, no checksum, foreign address and a missing file.

What the updater does not cover is listed in the [backlog](../BACKLOG.md).
