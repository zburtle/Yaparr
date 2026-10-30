#!/usr/bin/env bash
# Builds a Debian/Ubuntu package of the Avalonia app: artifacts/yaparr_<version>_<arch>.deb
# Usage: installer/build-deb.sh <version> [amd64|arm64]
# Needs the .NET SDK and dpkg-deb (any Debian/Ubuntu machine, including a GitHub Actions ubuntu runner).
set -euo pipefail

version="${1:?usage: build-deb.sh <version> [amd64|arm64]}"
arch="${2:-amd64}"
case "$arch" in
  amd64) rid=linux-x64 ;;
  arm64) rid=linux-arm64 ;;
  *) echo "unknown architecture: $arch" >&2; exit 1 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/artifacts"
stage="$out/deb-$arch"
repo="${GITHUB_REPOSITORY:-}"

rm -rf "$stage"
mkdir -p "$stage/opt/yaparr" "$stage/usr/bin" "$stage/usr/share/applications" "$stage/usr/share/doc/yaparr" "$stage/DEBIAN"

echo "Building Yaparr $version ($rid)"
# Invariant globalization removes the dependency on libicu, whose package name differs between releases.
dotnet publish "$root/samples/Yaparr.Desktop/Yaparr.Desktop.csproj" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" -p:UpdateRepository="$repo" -p:InvariantGlobalization=true -p:DebugType=None \
  -o "$stage/opt/yaparr"

chmod 755 "$stage/opt/yaparr/Yaparr.Desktop"
cp "$root/LICENSE.txt" "$stage/usr/share/doc/yaparr/copyright"
cp "$root/THIRD-PARTY-NOTICES.txt" "$root/THIRD-PARTY-NOTICES-SkiaSharp.txt" "$stage/usr/share/doc/yaparr/"
ln -s /opt/yaparr/Yaparr.Desktop "$stage/usr/bin/yaparr"

cat > "$stage/usr/share/applications/yaparr.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Yaparr
Comment=Serverless peer-to-peer chat
Exec=/opt/yaparr/Yaparr.Desktop
Terminal=false
Categories=Network;Chat;InstantMessaging;
EOF

size_kb=$(du -sk "$stage/opt" | cut -f1)
cat > "$stage/DEBIAN/control" <<EOF
Package: yaparr
Version: $version
Section: net
Priority: optional
Architecture: $arch
Installed-Size: $size_kb
Maintainer: Yaparr <noreply@yaparr.invalid>
Depends: libc6, libfontconfig1, libx11-6, libice6, libsm6, libssl3 | libssl3t64
Recommends: libstdc++6 (>= 13.2)
Description: Serverless peer-to-peer chat
 Chat with friends over the BitTorrent DHT without any server. Messages are
 signed by their senders and rooms need an invite to join.
EOF

deb="$out/yaparr_${version}_${arch}.deb"
dpkg-deb --root-owner-group --build "$stage" "$deb"
rm -rf "$stage"
echo "Created $deb"
