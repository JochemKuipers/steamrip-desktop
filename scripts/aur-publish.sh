#!/usr/bin/env bash
# Publish packaging/aur/PKGBUILD for a released version to the AUR (steamrip-desktop-bin).
#   scripts/aur-publish.sh 2026.11.7            fill the template, regenerate .SRCINFO, commit + push to the AUR
#   DRY_RUN=1 scripts/aur-publish.sh 2026.11.7  everything except the push
# Needs the release to exist on GitHub (the .deb checksum is read from its SHA256SUMS.txt) and SSH access to
# aur@aur.archlinux.org: either your own agent key (local use) or AUR_SSH_PRIVATE_KEY in the environment (CI).
# .SRCINFO comes from `makepkg --printsrcinfo`, run directly on Arch or inside an archlinux container elsewhere.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

version="${1:?usage: $0 <version>}"; version="${version#v}" # accept v2026.11.7 too
pkg=steamrip-desktop-bin
repo=JochemKuipers/steamrip-desktop

echo "== checksum of the .deb from the v$version release"
# gh works for a private repo too (GH_TOKEN in CI, your login locally); curl covers a public one without gh.
if command -v gh >/dev/null; then
  sha="$(gh release download "v$version" -R "$repo" -p SHA256SUMS.txt -O - | awk '/_amd64\.deb$/ {print $1}')"
else
  sha="$(curl -fsSL --retry 3 "https://github.com/$repo/releases/download/v$version/SHA256SUMS.txt" | awk '/_amd64\.deb$/ {print $1}')"
fi
[ -n "$sha" ] || { echo "no .deb listed in SHA256SUMS.txt for v$version; is the release published?" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
sed -e "s/@VERSION@/$version/" -e "s/@SHA256@/$sha/" packaging/aur/PKGBUILD >"$work/PKGBUILD"

echo "== .SRCINFO"
if command -v makepkg >/dev/null; then
  (cd "$work" && makepkg --printsrcinfo >.SRCINFO)
else
  docker run --rm -v "$work:/pkg" -w /pkg archlinux:base-devel \
    sh -c 'useradd -m b && chown -R b /pkg && su b -c "makepkg --printsrcinfo" > .SRCINFO && chown "$(stat -c %u /pkg/PKGBUILD)" .SRCINFO' >/dev/null
fi
grep -q "pkgver = $version" "$work/.SRCINFO" || { echo ".SRCINFO does not carry $version" >&2; exit 1; }

if [ -n "${AUR_SSH_PRIVATE_KEY:-}" ]; then
  echo "== using AUR_SSH_PRIVATE_KEY"
  mkdir -p ~/.ssh
  printf '%s\n' "$AUR_SSH_PRIVATE_KEY" >~/.ssh/aur
  chmod 600 ~/.ssh/aur
  ssh_cmd='ssh -i ~/.ssh/aur -o IdentitiesOnly=yes -o BatchMode=yes'
else
  echo "== using the ssh agent"
  ssh_cmd='ssh'
fi
ssh-keyscan -H aur.archlinux.org >>~/.ssh/known_hosts 2>/dev/null || true
export GIT_SSH_COMMAND="$ssh_cmd"

echo "== AUR preflight"
# the AUR forced command only knows its own verbs; `help` is the cheapest
$ssh_cmd aur@aur.archlinux.org help >/dev/null 2>&1 || { echo "AUR SSH preflight failed: key not accepted by aur.archlinux.org" >&2; exit 1; }

echo "== clone ssh://aur@aur.archlinux.org/$pkg.git"
if ! git clone -q "ssh://aur@aur.archlinux.org/$pkg.git" "$work/aur" 2>/dev/null; then
  echo "   not there yet → setup-repo $pkg"
  $ssh_cmd aur@aur.archlinux.org setup-repo "$pkg"
  git clone -q "ssh://aur@aur.archlinux.org/$pkg.git" "$work/aur"
fi

cp "$work/PKGBUILD" "$work/.SRCINFO" "$work/aur/"
cd "$work/aur"
git config user.name 'Jochem Kuipers'
git config user.email 'jochem@kuipers.cc'
git add PKGBUILD .SRCINFO
if git diff --cached --quiet; then
  echo "AUR already at $version"
  exit 0
fi
git commit -q -m "Update to $version"
if [ "${DRY_RUN:-0}" = 1 ]; then
  echo "== dry run; would push:"
  git --no-pager show --stat HEAD | head -20
  exit 0
fi
git push -q origin HEAD:master
echo "== pushed https://aur.archlinux.org/packages/$pkg ($version)"
