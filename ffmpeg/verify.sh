#!/usr/bin/env bash
# Checks a built ffmpeg.exe: what is in it, what is not, and that it still does the things Yaparr asks of it.
#   verify.sh path/to/ffmpeg.exe
set -uo pipefail

FF="${1:?usage: verify.sh path/to/ffmpeg.exe}"
HERE="$(cd "$(dirname "$0")" && pwd)"
TMP="$(mktemp -d)"
failed=0
ok()   { echo "ok    $*"; }
fail() { echo "FAIL  $*"; failed=1; }
has()  { grep -q -- "$2" <<<"$1"; }

version="$("$FF" -hide_banner -version 2>&1)"
echo "$version" | head -2

# Licence: no GPL part, nothing nonfree, not even version 3 (which would make it LGPL 3 instead of 2.1).
for bad in --enable-gpl --enable-nonfree --enable-version3 --enable-libx264 --enable-libx265; do
  if has "$version" -- "$bad"; then fail "configured with $bad"; else ok "not configured with $bad"; fi
done
has "$version" -- "--enable-libvpx" && ok "has libvpx" || fail "no libvpx"

encoders="$("$FF" -hide_banner -encoders 2>&1)"
decoders="$("$FF" -hide_banner -decoders 2>&1)"
filters="$("$FF" -hide_banner -filters 2>&1)"
for e in libvpx h264_nvenc h264_amf h264_qsv rawvideo; do has "$encoders" " $e " && ok "encoder $e" || fail "encoder $e is missing"; done
for d in vp8 h264; do has "$decoders" " $d " && ok "decoder $d" || fail "decoder $d is missing"; done
for f in gfxcapture hwdownload format scale; do has "$filters" " $f " && ok "filter $f" || fail "filter $f is missing"; done

# Nothing that should not be there: the GPL encoders, other video codecs, the network.
for e in libx264 libx265 libopenh264 libaom mpeg4 hevc libsvtav1; do has "$encoders" " $e " && fail "encoder $e should not be in this build" || ok "no encoder $e"; done
count="$("$FF" -hide_banner -codecs 2>&1 | grep -c '^ [D.][E.][VAS]')"
echo "codecs in the build: $count"

# The gfxcapture options Yaparr's command line uses (the build must have the October 2026 fixes, dup_frames among them).
help="$("$FF" -hide_banner -h filter=gfxcapture 2>&1)"
for opt in hwnd hmonitor max_framerate capture_cursor resize_mode dup_frames; do has "$help" "$opt" && ok "gfxcapture option $opt" || fail "gfxcapture has no option $opt"; done

# The encoders take the picture formats Yaparr gives them.
for pair in "h264_qsv nv12" "h264_amf yuv420p" "h264_nvenc yuv420p"; do
  set -- $pair
  line="$("$FF" -hide_banner -h encoder="$1" 2>&1 | grep 'Supported pixel formats')"
  has "$line" " $2" && ok "$1 takes $2" || fail "$1 does not take $2 ($line)"
done

# Round trip: VP8 in an IVF stream, then back to raw pictures of the size that went in.
"$FF" -hide_banner -loglevel error -f lavfi -i testsrc2=size=320x240:rate=10 -frames:v 20 \
  -c:v libvpx -deadline realtime -cpu-used 6 -lag-in-frames 0 -auto-alt-ref 0 -error-resilient 1 -b:v 300k -g 10 -f ivf "$TMP/a.ivf" \
  && [ "$(head -c 4 "$TMP/a.ivf")" = "DKIF" ] && ok "VP8 in IVF" || fail "VP8 in IVF"
"$FF" -hide_banner -loglevel error -f ivf -i "$TMP/a.ivf" -f rawvideo -pix_fmt bgra "$TMP/a.raw" \
  && [ "$(stat -c %s "$TMP/a.raw")" = "$((320 * 240 * 4 * 20))" ] && ok "VP8 decodes to 20 pictures of 320x240" || fail "VP8 decode ($(stat -c %s "$TMP/a.raw" 2>/dev/null) bytes)"

# H.264 in software (no card here to encode it): a small stream made with another encoder.
"$FF" -hide_banner -loglevel error -probesize 32 -analyzeduration 0 -f h264 -i "$HERE/tests/tiny.h264" -f rawvideo -pix_fmt bgra "$TMP/b.raw" \
  && [ "$(stat -c %s "$TMP/b.raw")" = "$((64 * 64 * 4 * 5))" ] && ok "H.264 decodes to 5 pictures of 64x64" || fail "H.264 decode ($(stat -c %s "$TMP/b.raw" 2>/dev/null) bytes)"

# Standard input and output, which is how Yaparr feeds and reads it.
cat "$TMP/a.ivf" | "$FF" -hide_banner -loglevel error -f ivf -i pipe:0 -f rawvideo -pix_fmt bgra pipe:1 | wc -c | grep -q "^$((320 * 240 * 4 * 20))$" \
  && ok "pipe in, pipe out" || fail "pipe in, pipe out"

# What it needs from Windows: only system libraries, nothing that would have to be shipped with it.
if command -v objdump >/dev/null; then
  deps="$(objdump -p "$FF" | awk '/DLL Name/ {print tolower($3)}' | sort -u)"
  echo "DLLs it loads: $(echo $deps)"
  odd="$(echo "$deps" | grep -v -E '^(kernel32|user32|gdi32|advapi32|shell32|ole32|oleaut32|ws2_32|bcrypt|crypt32|d3d11|dxgi|d3d9|mfplat|mf|combase|shlwapi|ntdll|msvcrt|api-ms-win-.*|ucrtbase|setupapi|secur32|userenv|winmm|imm32|version|comdlg32|propsys|dwmapi|dwrite|d2d1|windowscodecs|rpcrt4|powrprof|cfgmgr32|uuid|opengl32|cabinet)\.dll$' || true)"
  [ -z "$odd" ] && ok "only system DLLs" || fail "unexpected DLLs: $odd"
fi

size="$(stat -c %s "$FF")"
echo "ffmpeg.exe is $((size / 1024)) KB"
rm -rf "$TMP"
exit $failed
