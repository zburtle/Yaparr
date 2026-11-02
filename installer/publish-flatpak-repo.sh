#!/usr/bin/env bash
# Adds Flatpak builds to the hosted repository, laid out as the web site that serves it.
# Usage: installer/publish-flatpak-repo.sh <site-dir> <repo-dir>...
#   <site-dir>  the site so far (empty the first time); it gets repo/, the .flatpakrepo and .flatpakref files and a start page
#   <repo-dir>  an artifacts/flatpak-repo-<arch> directory that installer/build-flatpak.sh made with signing on
# Needs FLATPAK_GPG_KEY and FLATPAK_GPG_HOME (the key that signed the builds), flatpak and ostree.
# FLATPAK_SITE_URL is the address the site is served at (default https://flatpak.yaparr.com).
set -euo pipefail

site="${1:?usage: publish-flatpak-repo.sh <site-dir> <repo-dir>...}"
shift
[ "$#" -gt 0 ] || { echo "no repository to add" >&2; exit 1; }
: "${FLATPAK_GPG_KEY:?}" "${FLATPAK_GPG_HOME:?}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
site_url="${FLATPAK_SITE_URL:-https://flatpak.yaparr.com}"
app_id=com.yaparr.Yaparr
branch=stable

# Git and CI artifacts keep no empty directories, and ostree needs them: they are made again for every repository we open.
ensure_dirs() {
  mkdir -p "$1/objects" "$1/refs/heads" "$1/refs/mirrors" "$1/refs/remotes" "$1/tmp" "$1/extensions" "$1/state" "$1/deltas"
}

mkdir -p "$site"
[ -f "$site/repo/config" ] || ostree --repo="$site/repo" init --mode=archive
ensure_dirs "$site/repo"

for source in "$@"; do
  ensure_dirs "$source"
  # The refs this build made (app/<id>/<arch>/<branch>); the appstream refs are made again below.
  refs="$(ostree --repo="$source" refs | grep "^app/$app_id/" || true)"
  [ -n "$refs" ] || { echo "No $app_id build in $source" >&2; exit 1; }
  for ref in $refs; do
    echo "Adding $ref"
    ostree --repo="$site/repo" pull-local "$source" "$ref"
  done
done

# The summary lists what the repository holds and is signed, so a client can check it. Only the last few versions are kept,
# since every one is a download for somebody who skipped the ones between.
flatpak build-update-repo --gpg-sign="$FLATPAK_GPG_KEY" --gpg-homedir="$FLATPAK_GPG_HOME" \
  --generate-static-deltas --prune --prune-depth=3 --title="Yaparr" "$site/repo"

key="$(base64 -w0 "$root/installer/flatpak/yaparr-flatpak.gpg")"
cat > "$site/yaparr.flatpakrepo" <<EOF
[Flatpak Repo]
Title=Yaparr
Url=$site_url/repo/
Homepage=https://github.com/zburtle/Yaparr
Comment=Serverless peer-to-peer chat
DefaultBranch=$branch
GPGKey=$key
EOF

cat > "$site/$app_id.flatpakref" <<EOF
[Flatpak Ref]
Title=Yaparr
Name=$app_id
Branch=$branch
Url=$site_url/repo/
SuggestRemoteName=yaparr
IsRuntime=false
RuntimeRepo=https://dl.flathub.org/repo/flathub.flatpakrepo
Homepage=https://github.com/zburtle/Yaparr
GPGKey=$key
EOF

cat > "$site/index.html" <<EOF
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Yaparr for Linux</title></head>
<body style="font-family: sans-serif; max-width: 40em; margin: 2em auto; padding: 0 1em">
<h1>Yaparr for Linux</h1>
<p>Serverless peer-to-peer chat. This is the Flatpak repository of <a href="https://github.com/zburtle/Yaparr">Yaparr</a>.</p>
<p>Install it (the 64-bit Intel/AMD and ARM versions are both here):</p>
<pre>flatpak install $site_url/$app_id.flatpakref</pre>
<p>It then updates with the rest of your software, or with <code>flatpak update</code>. To add the repository by itself:</p>
<pre>flatpak remote-add --if-not-exists yaparr $site_url/yaparr.flatpakrepo</pre>
</body>
</html>
EOF

echo "${site_url#https://}" > "$site/CNAME"
touch "$site/.nojekyll"
echo "The site is in $site"
