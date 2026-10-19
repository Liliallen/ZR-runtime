#!/usr/bin/env python3
"""Align two capture sequences of the same deterministic sequence (e.g. a cutscene) recorded at
different frame rates. Each frame of run B is matched to its most similar frame in run A; if both
play at the same speed the matches follow a line of slope ~1 (offset = difference in load time).

usage: align_timelines.py <screenshotsA> <screenshotsB> <capture_interval_s>
Needs ffmpeg. Similarity: normalized cross-correlation of 64x36 grayscale thumbnails.
"""
import glob, math, subprocess, sys


def thumb(path):
    raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", path, "-vf", "scale=64:36,format=gray",
                          "-f", "rawvideo", "-"], capture_output=True, check=True).stdout
    m = sum(raw) / len(raw)
    v = [p - m for p in raw]
    n = math.sqrt(sum(x * x for x in v)) or 1.0
    return [x / n for x in v], m


def main(a_dir, b_dir, interval):
    A = [thumb(p) for p in sorted(glob.glob(a_dir + "/*.png"))]
    B = [thumb(p) for p in sorted(glob.glob(b_dir + "/*.png"))]
    pairs = []
    for j, (vb, mb) in enumerate(B):
        if mb < 8:  # skip black frames: they match anything
            continue
        scores = [(sum(x * y for x, y in zip(va, vb)), i) for i, (va, ma) in enumerate(A) if ma >= 8]
        s, i = max(scores)
        pairs.append((j * interval, i * interval, s))
        print(f"B t={j * interval:5.0f}s  ->  A t={i * interval:5.0f}s  (ncc {s:.2f})")
    good = [(b, a) for b, a, s in pairs if s > 0.8]
    if len(good) >= 3:
        n = len(good); sb = sum(b for b, _ in good); sa = sum(a for _, a in good)
        sbb = sum(b * b for b, _ in good); sba = sum(b * a for b, a in good)
        slope = (n * sba - sb * sa) / (n * sbb - sb * sb)
        print(f"fit over {n} confident matches: A_time = {slope:.3f} * B_time + {(sa - slope * sb) / n:.1f}s "
              f"(slope 1.0 = same playback speed)")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], float(sys.argv[3]))
