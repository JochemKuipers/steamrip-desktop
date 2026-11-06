#!/usr/bin/env sh
# ReSharper inspections (same engine and settings as the IDE extension).
#   scripts/inspect.sh [output]            gate: WARNING and above, non-zero exit on any finding
#   SEVERITY=HINT scripts/inspect.sh       report: everything ReSharper knows (INFO < HINT < SUGGESTION < WARNING < ERROR)
#   FAIL=0 scripts/inspect.sh              report only, never fail
#   FORMAT=Sarif|Html|Xml|Text             output format (default Text)
# Severities, naming and style come from .editorconfig, so the IDE, this script (pre-push) and CI agree.
# Takes ~20 s; needs a prior `dotnet build`.
set -e
cd "$(git rev-parse --show-toplevel)"
severity="${SEVERITY:-WARNING}"
format="${FORMAT:-Text}"
fail="${FAIL:-1}"
case "$format" in
  Text) ext=txt ;; Sarif) ext=sarif ;; Html) ext=html ;; Xml) ext=xml ;;
  *) echo "unknown FORMAT $format"; exit 2 ;;
esac
out="${1:-obj/inspectcode.$ext}"
mkdir -p "$(dirname "$out")"
dotnet tool restore >/dev/null
# A fresh cache every run: ReSharper caches .editorconfig evaluation and would otherwise report stale results.
rm -rf obj/jbcache
# stderr carries ReSharper's own analyzer-loading noise; it goes to a log next to the report.
dotnet jb inspectcode SteamRip.slnx --output="$out" --format="$format" --severity="$severity" --swea --no-build \
  --exclude="**/Fixtures/**" --caches-home=obj/jbcache >/dev/null 2>"$out.log"

if [ "$format" != "Text" ]; then
  echo "ReSharper report ($severity+) written to $out"
  exit 0
fi
# Text format: one indented "path:line message" per finding under project headers.
count=$(grep -cE '^\s+\S+:[0-9]+ ' "$out" || true)
if [ "$count" != "0" ]; then
  echo "ReSharper found $count issue(s) at $severity or above:"
  grep -E '^\s+\S+:[0-9]+ ' "$out"
  [ "$fail" = "1" ] && exit 1
  exit 0
fi
echo "ReSharper: no issues at $severity or above."
