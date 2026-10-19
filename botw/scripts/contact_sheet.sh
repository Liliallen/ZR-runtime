#!/usr/bin/env bash
# Usage: contact_sheet.sh <screenshots-dir> <out.png>  — 4-column grid of 480x270 thumbnails in time order.
set -euo pipefail
n=$(ls "$1"/*.png | wc -l); rows=$(( (n + 3) / 4 ))
ffmpeg -loglevel error -y -pattern_type glob -i "$1/*.png" -vf "scale=480:270,tile=4x${rows}" -frames:v 1 "$2"
