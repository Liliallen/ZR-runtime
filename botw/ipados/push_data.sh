#!/usr/bin/env bash
# Copies a BOTW-PC data folder and the game files into BOTW-iPad's Documents on the connected iPad (the app must be
# installed). The source folder is only read; the iPad gets its own copy (saves included).
#   botw/ipados/push_data.sh <BOTW-PC data folder> <game file>... (base game .nsp/.xci, update, DLC)
# Skips files that are already there and unchanged (devicectl), so it can be re-run after an interruption.
set -euo pipefail
ID=local.botwpc.ipad
D="${BOTWPC_DEVICE:-$(xcrun devicectl list devices 2>/dev/null | awk '/iPad/ && /(available|connected)/ && !/simulated/ {for (i=1;i<=NF;i++) if ($i ~ /^[0-9A-F]{8}-[0-9A-F]{16}$/) print $i; exit}')}"
DATA=$1; shift
STAGE=$(mktemp -d); trap 'rm -rf "$STAGE"' EXIT
# The data folder without logs and the host's GPU caches; the game's shader cache (guest shaders) is kept.
rsync -a --exclude Logs --exclude 'screenshots' "$DATA/" "$STAGE/BOTW-PC/"
xcrun devicectl device copy to --device "$D" --domain-type appDataContainer --domain-identifier "$ID" \
  --source "$STAGE/BOTW-PC" --destination Documents/BOTW-PC
for f in "$@"; do
  echo "copying $(basename "$f") ($(($(stat -f %z "$f") >> 20)) MB)"
  xcrun devicectl device copy to --device "$D" --domain-type appDataContainer --domain-identifier "$ID" \
    --source "$f" --destination "Documents/Games/$(basename "$f")"
done
