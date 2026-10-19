#!/usr/bin/env bash
# Usage: botw/scripts/setup-macos.sh <BOTW-PC.app> <source data dir> <game dir>
#   source data dir: an existing BOTW-PC/Ryujinx data dir (e.g. the Windows devdata); only read, never modified.
#   game dir:        folder with the base .nsp, update/ and dlc/ (the Windows layout).
# Creates ~/Library/Application Support/BOTW-PC (or $BOTWPC_DATA) with copies of keys, firmware, profiles, saves and
# settings, registers the game, update and DLC, and selects Runtime V2. Existing destination files are kept.
set -euo pipefail
APP=${1:?BOTW-PC.app}; SRC=${2:?source data dir}; GAME=${3:?game dir}
BIN="$APP/Contents/MacOS/BOTW-PC"
DATA=${BOTWPC_DATA:-$HOME/Library/Application Support/BOTW-PC}

BASE=$(ls "$GAME"/*01007EF00011E000*.nsp | head -1)
UPDATE=$(ls "$GAME"/update/*.nsp | head -1)
DLC=(); for f in "$GAME"/dlc/*.nsp; do DLC+=(--dlc "$f"); done

mkdir -p "$DATA/botw"
# Settings: copy the source's emulator and BOTW-PC settings once, then rewrite their Windows paths.
[ -f "$DATA/Config.json" ] || cp "$SRC/Config.json" "$DATA/Config.json"
[ -f "$DATA/botw/settings.json" ] || { [ -f "$SRC/botw/settings.json" ] && cp "$SRC/botw/settings.json" "$DATA/botw/settings.json"; } || true
python3 - "$DATA" "$GAME" <<'PY'
import json, os, sys
data, game = sys.argv[1], os.path.abspath(sys.argv[2])
p = os.path.join(data, "Config.json"); c = json.load(open(p, encoding="utf-8"))
c["game_dirs"] = [game]; c["autoload_dirs"] = [os.path.join(game, "update"), os.path.join(game, "dlc")]
c["preferred_gpu"] = ""  # the Windows NVIDIA id never matches on a Mac
# Apple Hypervisor: BotW crashes with corrupted guest pointers within minutes (MACOS_PORT.md); use the JIT.
c["use_hypervisor"] = False
json.dump(c, open(p, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
p = os.path.join(data, "botw", "settings.json")
s = json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}
s.setdefault("graphics", {})["dlss"] = "Off"  # NVIDIA-only
s["graphics"].setdefault("gpu_frontend", "V2")
r = s.setdefault("runtime", {})
for k, v in {"cpu_core": "V2", "cpu_v2_abi": "Flags", "memory_tracking": "Legacy", "hle_scheduler": "V2"}.items():
    r[k] = v if r.get(k, "Legacy") == "Legacy" else r[k]
s["graphics"]["gpu_frontend"] = "V2" if s["graphics"]["gpu_frontend"] == "Legacy" else s["graphics"]["gpu_frontend"]
json.dump(s, open(p, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
PY
# Caches that do not depend on the host: guest shaders (no Vulkan-vendor host cache) and the PPTC profile (not the
# x86-64 code cache). Saves the shader stutter of a first playthrough; never overwrites.
T=games/01007ef00011e000
for f in "$SRC/$T"/cache/shader/{guest,shared}.{toc,data} "$SRC/$T"/cache/cpu/*/*.info "$SRC/$T/mods.json"; do
  [ -f "$f" ] || continue; d="$DATA/${f#"$SRC/"}"; mkdir -p "$(dirname "$d")"; cp -n "$f" "$d" || true
done
"$BIN" --botw-setup -r "$DATA" --import "$SRC" --import-saves --game "$BASE" --update "$UPDATE" "${DLC[@]}"
