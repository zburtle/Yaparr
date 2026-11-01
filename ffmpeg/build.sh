#!/usr/bin/env bash
# Builds the FFmpeg that Yaparr runs to share and show screens: only the parts Yaparr uses, under the LGPL, in one static
# ffmpeg.exe for Windows x64. Run it in an MSYS2 UCRT64 shell (the workflow does); see README.md in this folder.
#
#   FFMPEG_COMMIT   the FFmpeg commit to build (default: the one Yaparr is tested with)
#   OUT             where the package goes (default: ./out)
set -euo pipefail

FFMPEG_COMMIT="${FFMPEG_COMMIT:-17a4a474f4013bfef4393abaca282078e24efc35}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${WORK:-$PWD/work}"
OUT="${OUT:-$PWD/out}"
SHORT="${FFMPEG_COMMIT:0:10}"
NAME="ffmpeg-yaparr-g${SHORT}-win64"

rm -rf "$WORK" "$OUT"
mkdir -p "$WORK" "$OUT/$NAME/bin" "$OUT/$NAME/licenses"
cd "$WORK"

git clone --filter=blob:none https://github.com/FFmpeg/FFmpeg.git src
git -C src checkout --detach "$FFMPEG_COMMIT"
cd src

# What goes in, and why:
#   libvpx_vp8, vp8            VP8 in software: the encoder for computers without a usable graphics card, and the decoder
#   h264_nvenc, h264_amf, h264_qsv   H.264 on NVIDIA, AMD and Intel graphics cards (the card does the work)
#   h264 (decoder, parser)     every viewer decodes H.264 in software
#   ivf, h264 (demuxers/muxers), rawvideo, pipe   the pictures go in and out through standard input and output
#   gfxcapture, hwdownload, format, scale   Windows Graphics Capture, then the download and conversion of each picture
#   lavfi, color, testsrc2, null, wrapped_avframe   only for Yaparr's tests and the checks in verify.sh
#                              (wrapped_avframe is how FFmpeg itself reads a test source)
# Nothing else is switched on: no GPL part (no x264, x265), nothing "nonfree", no network, no other codec, no other program.
./configure \
  --prefix="$WORK/prefix" \
  --arch=x86_64 --target-os=mingw32 \
  --pkg-config-flags=--static \
  --extra-ldflags="-static -static-libgcc -static-libstdc++" \
  --extra-libs="-lstdc++" \
  --disable-everything --disable-autodetect \
  --disable-shared --enable-static \
  --disable-doc --disable-debug --disable-network \
  --disable-ffplay --disable-ffprobe \
  --enable-d3d11va --enable-dxva2 \
  --enable-libvpx --enable-libvpl --enable-amf --enable-ffnvcodec --enable-nvenc \
  --enable-encoder=libvpx_vp8,h264_nvenc,h264_amf,h264_qsv,rawvideo \
  --enable-decoder=vp8,h264,wrapped_avframe \
  --enable-parser=h264,vp8 \
  --enable-demuxer=ivf,h264 \
  --enable-muxer=ivf,h264,rawvideo,null \
  --enable-indev=lavfi \
  --enable-bsf=h264_mp4toannexb \
  --enable-protocol=pipe,file \
  --enable-filter=gfxcapture,hwdownload,format,scale,color,testsrc2 \
  || { tail -n 80 ffbuild/config.log; exit 1; }

make -j"$(nproc)"

VERSION="$(./ffbuild/version.sh .)"
CONFIGURE_LINE="$(grep -m1 '^configuration:' <(./ffmpeg.exe -hide_banner -version) | sed 's/^configuration: //')"
cp ffmpeg.exe "$OUT/$NAME/bin/ffmpeg.exe"
strip "$OUT/$NAME/bin/ffmpeg.exe"
cp COPYING.LGPLv2.1 "$OUT/$NAME/LICENSE.txt"

# The licences of what was built in. MSYS2 keeps each package's licence files under share/licenses; the packages here are the
# ones whose code (or headers) ended up in ffmpeg.exe. Missing ones are reported, not hidden.
LICENSE_ROOT="${MINGW_PREFIX:-/ucrt64}/share/licenses"
for pkg in libvpx libvpl amf-headers ffnvcodec-headers gcc-libs; do
  dir="$LICENSE_ROOT/$pkg"
  if [ -d "$dir" ]; then mkdir -p "$OUT/$NAME/licenses/$pkg" && cp -r "$dir"/. "$OUT/$NAME/licenses/$pkg"/
  else echo "NOTE: no licence folder for $pkg under $LICENSE_ROOT" | tee -a "$OUT/$NAME/licenses/MISSING.txt"; fi
done

# The packages and versions used, as they were on the build machine.
{
  echo "FFmpeg $VERSION"
  echo "commit $FFMPEG_COMMIT"
  echo "built from https://github.com/zburtle/Yaparr${GITHUB_SHA:+ at commit $GITHUB_SHA}${GITHUB_RUN_ID:+ (workflow run $GITHUB_RUN_ID)}"
  echo
  echo "configure: $CONFIGURE_LINE"
  echo
  echo "Libraries and headers used:"
  for pkg in libvpx libvpl amf-headers ffnvcodec-headers gcc-libs; do pacman -Q "${MINGW_PACKAGE_PREFIX:-mingw-w64-ucrt-x86_64}-$pkg" 2>/dev/null || true; done
  echo
  echo "Compiler: $(gcc --version | head -1)"
} > "$OUT/$NAME/BUILD-INFO.txt"

cat > "$OUT/$NAME/SOURCE.txt" <<EOF
This is a build of FFmpeg, made for Yaparr (https://github.com/zburtle/Yaparr), with only the parts Yaparr uses.

FFmpeg is licensed under the GNU Lesser General Public License, version 2.1 or later (LICENSE.txt). It is not built with any
GPL or "nonfree" part. Yaparr runs it as a separate program and does not link to it, so it can be replaced: set YAPARR_FFMPEG
to another ffmpeg.exe, or put one in an "ffmpeg" folder next to Yaparr.

The source code of this build, the exact commit of FFmpeg that was built, is the file ffmpeg-g${SHORT}-source.tar.xz in the
same release, and https://github.com/FFmpeg/FFmpeg/commit/${FFMPEG_COMMIT}.
The way it was configured and built is in BUILD-INFO.txt here and in the ffmpeg folder of the Yaparr source
(https://github.com/zburtle/Yaparr/tree/master/ffmpeg).

Built in: libvpx (BSD licence, with Google's patent grant). The AMF, NVIDIA codec and oneVPL headers are used to talk to the
graphics driver, which is not part of this package. Their licences are in the licenses folder.

FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project. Yaparr is not endorsed by the FFmpeg project.
EOF

# The source of exactly what was built, for the LGPL.
git archive --format=tar --prefix="ffmpeg-g${SHORT}/" "$FFMPEG_COMMIT" | xz -9 > "$OUT/ffmpeg-g${SHORT}-source.tar.xz"

( cd "$OUT" && zip -9 -r "$NAME.zip" "$NAME" )
echo "NAME=$NAME" > "$OUT/name.txt"
echo "Built $OUT/$NAME.zip"
