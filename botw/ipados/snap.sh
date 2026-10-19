#!/bin/sh
# Pulls the iPad's current screen, the app's statistics and logs into $1 (default dist-ipados/device).
D="${BOTWPC_DEVICE:-$(xcrun devicectl list devices 2>/dev/null | awk '/iPad/ && /(available|connected)/ && !/simulated/ {for (i=1;i<=NF;i++) if ($i ~ /^[0-9A-F]{8}-[0-9A-F]{16}$/) print $i; exit}')}"
R="${1:-$(cd "$(dirname "$0")/../../.." && pwd)/dist-ipados/device}"; mkdir -p "$R"; T=$(date +%H%M%S)
xcrun devicectl device capture screenshot --device "$D" --destination "$R/screen-$T.png" >/dev/null 2>&1 && echo "screen $R/screen-$T.png"
c() { xcrun devicectl device copy from --device "$D" --domain-type appDataContainer --domain-identifier local.botwpc.ipad --source "$1" --destination "$2" >/dev/null 2>&1; }
c Documents/botw_stats.csv "$R/botw_stats.csv" && tail -3 "$R/botw_stats.csv"
c Documents/BOTW-PC/Logs "$R/Logs" && ls -t "$R/Logs" | head -1
xcrun devicectl device info processes --device "$D" 2>/dev/null | grep -q BOTWiPad && echo "process: running" || echo "process: not running"
