#!/usr/bin/env bash
# Builds a Flatpak bundle of the Avalonia app: artifacts/yaparr_<version>_<arch>.flatpak
# Usage: installer/build-flatpak.sh <version> [amd64|arm64]
# Needs the .NET SDK, flatpak and flatpak-builder, and builds for the architecture of the machine it runs on (the
# manifest compiles FFmpeg, so there is no cross build). A GitHub Actions ubuntu runner works for amd64, and
# ubuntu-24.04-arm for arm64.
set -euo pipefail

version="${1:?usage: build-flatpak.sh <version> [amd64|arm64]}"
arch="${2:-amd64}"
case "$arch" in
  amd64) rid=linux-x64; flatpak_arch=x86_64 ;;
  arm64) rid=linux-arm64; flatpak_arch=aarch64 ;;
  *) echo "unknown architecture: $arch" >&2; exit 1 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/artifacts"
app="$out/flatpak-app"
meta="$out/flatpak-meta"
repo="${GITHUB_REPOSITORY:-}"
app_id=com.yaparr.Yaparr

rm -rf "$app" "$meta" "$out/flatpak-build-$arch" "$out/flatpak-repo-$arch"
mkdir -p "$app" "$meta"

echo "Building Yaparr $version ($rid)"
# Self-contained, so the Flatpak runtime needs no .NET. Invariant globalization matches the .deb.
dotnet publish "$root/samples/Yaparr.Desktop/Yaparr.Desktop.csproj" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" -p:UpdateRepository="$repo" -p:InvariantGlobalization=true -p:DebugType=None \
  -o "$app"

# The AppStream file names the release; the template has placeholders for it.
sed -e "s/@VERSION@/$version/" -e "s/@DATE@/$(date -u +%Y-%m-%d)/" \
  "$root/installer/flatpak/$app_id.metainfo.xml.in" > "$meta/$app_id.metainfo.xml"

flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak-builder --user --force-clean --install-deps-from=flathub --arch="$flatpak_arch" \
  --repo="$out/flatpak-repo-$arch" "$out/flatpak-build-$arch" "$root/installer/flatpak/$app_id.yml"

bundle="$out/yaparr_${version}_${arch}.flatpak"
flatpak build-bundle --arch="$flatpak_arch" --runtime-repo=https://dl.flathub.org/repo/flathub.flatpakrepo \
  "$out/flatpak-repo-$arch" "$bundle" "$app_id"
rm -rf "$app" "$meta" "$out/flatpak-build-$arch" "$out/flatpak-repo-$arch"
echo "Created $bundle"
