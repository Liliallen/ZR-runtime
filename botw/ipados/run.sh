#!/bin/sh
# Installs (optional) and launches BOTW-iPad on the connected iPad with lldb attached for the JIT: the app prepares its
# code arena through the JIT26 protocol (jit26_lldb.py) and then lets the debugger go; the game runs detached.
#   botw/ipados/run.sh [app to install] [JSON env, e.g. '{"BOTWPC_FRAMELOG":"Documents/frames.csv"}']
# Kill lldb (pkill -x lldb) before installing another build.
ID=local.botwpc.ipad
D="${BOTWPC_DEVICE:-$(xcrun devicectl list devices 2>/dev/null | awk '/iPad/ && /(available|connected)/ && !/simulated/ {for (i=1;i<=NF;i++) if ($i ~ /^[0-9A-F]{8}-[0-9A-F]{16}$/) print $i; exit}')}"
L="${BOTWPC_LLDB_DIR:-${TMPDIR:-/tmp}/botw-ipad-lldb}"; HERE="$(cd "$(dirname "$0")" && pwd)"; mkdir -p "$L"
pkill -f "tail -f $L/lldb.in"; pkill -x lldb; sleep 1
if [ -n "${1:-}" ]; then xcrun devicectl device install app --device "$D" "$1" >/dev/null || exit 1; fi
if [ -n "${2:-}" ]; then set -- --environment-variables "$2"; else set --; fi
xcrun devicectl device process launch --device "$D" --terminate-existing --start-stopped "$@" "$ID" >/dev/null || exit 1
P=$(xcrun devicectl device info processes --device "$D" 2>/dev/null | grep "BOTWiPad.app" | awk '{print $1}' | head -1)
echo "pid $P (lldb log $L/lldb.log)"
printf 'settings set platform.plugin.darwin.ignored-exceptions EXC_BAD_ACCESS|EXC_BAD_INSTRUCTION|EXC_ARITHMETIC\ncommand script import %s/jit26_lldb.py\ndevice select %s\ndevice process attach -p %s\n' "$HERE" "$D" "$P" > "$L/lldb.in"
(tail -f "$L/lldb.in" | xcrun lldb > "$L/lldb.log" 2>&1 &)
for i in $(seq 1 30); do grep -q "stop reason = signal SIGSTOP" "$L/lldb.log" && break; sleep 1; done
printf 'process handle SIGSEGV SIGBUS SIGILL SIGFPE SIGUSR1 SIGUSR2 -n false -p true -s false\nprocess continue\n' >> "$L/lldb.in"
# serve the app's JIT requests (lldb fed through a pipe runs no stop hooks) until it detaches
(while pgrep -qf "tail -f $L/lldb.in"; do echo jit26_service >> "$L/lldb.in"; grep -q "jit26: detaching" "$L/lldb.log" && break; sleep 1; done; sleep 2; pkill -f "tail -f $L/lldb.in") &
