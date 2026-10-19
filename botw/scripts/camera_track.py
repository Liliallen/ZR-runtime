#!/usr/bin/env python3
"""Frame-rate regression check from BOTWPC_CAMERA_LOG files: camera position and heading over the scripted actions.

usage: camera_track.py <runA dir> <runB dir> [...]   (each dir: camera.txt + stdout.log from bench_win.ps1)

camera.txt lines are "time_s m00 .. m33" (scene world -> clip matrix rows x, y, z, w). The camera centre C is the point
the x, y and w rows map to zero (w row = depth), solved per frame; the heading is the w row's horizontal direction.
stdout.log gives the input script's time 0 ("input script clock started at"), so runs at different frame rates are
compared at the same script times. With correct game timing, every segment moves the camera by the same distance and
turns it by the same angle at 30 and 120 FPS (the game simulates the same game time)."""
import math
import re
import sys

# (name, start, end) in input-script seconds; must match botw/bench/regress.input.
SEGMENTS = [
    ("rest before jump", 60.0, 65.0),
    ("jump (peak height)", 65.0, 67.5),
    ("walk 2 s", 67.5, 71.0),
    ("attacks", 71.0, 75.0),
    ("throw", 75.0, 78.5),
    ("camera turn 1 s", 78.5, 81.0),
    ("sprint 1.5 s + jump", 81.0, 86.0),
]


def solve3(a, b):
    det = (a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1]) - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0])
           + a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]))
    if abs(det) < 1e-12:
        return None
    out = []
    for c in range(3):
        m = [row[:] for row in a]
        for r in range(3):
            m[r][c] = b[r]
        out.append((m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1]) - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
                    + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0])) / det)
    return out


def load(run):
    origin = None
    with open(f"{run}/stdout.log", encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.search(r"input script clock started at ([0-9.]+) s", line)
            if m:
                origin = float(m.group(1))
                break
    frames = []
    with open(f"{run}/camera.txt") as f:
        for line in f:
            v = [float(x) for x in line.split()]
            t, m = v[0], v[1:17]
            rx, ry, rw = m[0:4], m[4:8], m[12:16]
            c = solve3([rx[:3], ry[:3], rw[:3]], [-rx[3], -ry[3], -rw[3]])
            if c is None:
                continue
            heading = math.degrees(math.atan2(rw[0], rw[2]))
            frames.append((t - origin, c, heading))
    return frames


def segment(frames, start, end):
    seg = [f for f in frames if start <= f[0] < end]
    if not seg:
        return None
    p0, p1 = seg[0][1], seg[-1][1]
    horiz = math.hypot(p1[0] - p0[0], p1[2] - p0[2])
    rise = max(f[1][1] for f in seg) - p0[1]
    turn = (seg[-1][2] - seg[0][2] + 180) % 360 - 180
    return horiz, rise, turn, len(seg) / (end - start)


def main(argv):
    runs = argv[1:]
    data = {r: load(r) for r in runs}
    print(f"{'segment':22s}" + "".join(f" | {r.split('/')[-1].split(chr(92))[-1][:26]:>26s}" for r in runs))
    print(f"{'':22s}" + " | horiz m   rise m  turn deg  fps" * len(runs))
    for name, start, end in SEGMENTS:
        row = f"{name:22s}"
        for r in runs:
            s = segment(data[r], start, end)
            row += " |      n/a" if s is None else f" | {s[0]:7.2f} {s[1]:7.2f} {s[2]:8.1f} {s[3]:5.0f}"
        print(row)

    # Net displacement between two moments of rest (camera settled behind Link): where Link ended up.
    row = f"{'net 62 s -> 92 s':22s}"
    for r in runs:
        a = [f for f in data[r] if 61.5 <= f[0] < 62.5]
        b = [f for f in data[r] if 91.5 <= f[0] < 92.5]
        if not a or not b:
            row += " |      n/a"
            continue
        dx, dy, dz = (b[-1][1][i] - a[-1][1][i] for i in range(3))
        turn = (b[-1][2] - a[-1][2] + 180) % 360 - 180
        row += f" | {math.hypot(dx, dz):7.2f} {dy:7.2f} {turn:8.1f}      "
    print(row)


if __name__ == "__main__":
    main(sys.argv)
