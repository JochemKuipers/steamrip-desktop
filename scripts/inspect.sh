#!/usr/bin/env sh
# ReSharper inspections (same engine and settings as the IDE extension) at WARNING and above.
# Non-zero exit on any finding. Severities, naming and style come from .editorconfig, so the IDE,
# this script (pre-push) and CI agree. Takes ~20 s; needs a prior `dotnet build`.
set -e
cd "$(git rev-parse --show-toplevel)"
out="${1:-obj/inspectcode.txt}"
mkdir -p "$(dirname "$out")"
dotnet tool restore >/dev/null
# A fresh cache every run: ReSharper caches .editorconfig evaluation and would otherwise report stale results.
rm -rf obj/jbcache
# stderr carries ReSharper's own analyzer-loading noise; it goes to a log next to the report.
dotnet jb inspectcode SteamRip.slnx --output="$out" --format=Text --severity=WARNING --swea --no-build \
  --exclude="**/Fixtures/**" --caches-home=obj/jbcache >/dev/null 2>"$out.log"
# Text format: one indented "path:line message" per finding under project headers.
count=$(grep -cE '^\s+\S+:[0-9]+ ' "$out" || true)
if [ "$count" != "0" ]; then
  echo "ReSharper found $count issue(s):"
  grep -E '^\s+\S+:[0-9]+ ' "$out"
  exit 1
fi
echo "ReSharper: no issues at WARNING or above."
