"""Before / after table of two run-format-audit.ps1 result folders (5.2.0 format speed work).

Usage: python tools/perf/compare-format-audit.py <before-results> <after-results> [--files a.tif,b.heic] [--md out.md]

One row per file with the four columns the speed work is judged on, each as "before -> after (x faster)":
  thumbnail cold   first tile in a fresh process        (thumb-cold; HEIC / AVIF / JXL / JP2: the codec plug-in's 236 px preview, prov-cold)
  thumbnail warm   median of the warm runs              (thumb-warm; prov-warm)
  view first cold  viewer first frame, fresh process    (view-cold coldFirstMs)
  view first warm  viewer first frame, median           (view-warm firstMs)
plus the settled and full-decode medians of the viewer. A cell is marked "*" when either side is noisy (samples spread by more than 40 % of the
median and more than 3 ms, or the machine was above the busy threshold when the phase started).
"""
from __future__ import annotations

import json
import os
import sys

PLUGIN = {".heic", ".heif", ".heics", ".heifs", ".hif", ".avif", ".avifs", ".jxl", ".jp2", ".j2k", ".j2c", ".jpc", ".jpx", ".jpf", ".jpm", ".mj2"}


def load(root, phase, name):
    p = os.path.join(root, "cells", phase, name + ".json")
    if not os.path.exists(p):
        return None
    with open(p, encoding="utf-8-sig") as f:
        return json.load(f)


def noisy(cell, key, samples_key=None):
    if cell is None:
        return False
    s = [x for x in (cell.get(samples_key or "samples") or []) if x is not None]
    m = cell.get(key)
    spread = (max(s) - min(s)) if len(s) >= 3 else 0
    return bool(cell.get("busy")) or (m is not None and spread > 3.0 and spread / max(m, 0.01) > 0.4)


def cell_value(root, name, which):
    ext = os.path.splitext(name)[1].lower()
    plugin = ext in PLUGIN
    if which == "thumb_cold":
        c = load(root, "prov-cold" if plugin else "thumb-cold", name)
        return (c.get("coldMs"), noisy(c, "coldMs", "coldMsSamples")) if c else (None, False)
    if which == "thumb_warm":
        c = load(root, "prov-warm" if plugin else "thumb-warm", name)
        return (c.get("warmMs"), noisy(c, "warmMs")) if c else (None, False)
    if which == "view_cold":
        c = load(root, "view-cold", name)
        return (c.get("coldFirstMs"), noisy(c, "coldFirstMs", "coldFirstMsSamples")) if c else (None, False)
    if which == "view_warm":
        c = load(root, "view-warm", name)
        if not c:
            return None, False
        s = [c.get("firstMin"), c.get("firstMax")]
        spread = (s[1] - s[0]) if all(x is not None for x in s) else 0
        m = c.get("firstMs")
        return m, bool(c.get("busy")) or (m is not None and spread > 3.0 and spread / max(m, 0.01) > 0.4)
    if which == "settled":
        c = load(root, "view-warm", name)
        return (c.get("settledMs"), False) if c else (None, False)
    if which == "full":
        c = load(root, "view-warm", name)
        return (c.get("fullMs"), False) if c else (None, False)
    return None, False


def fmt(v):
    if v is None:
        return "n/a"
    return f"{v:.2f}" if v < 10 else f"{v:.1f}" if v < 100 else f"{v:.0f}"


def pair(before, after):
    (b, nb), (a, na) = before, after
    if b is None or a is None:
        return f"{fmt(b)} -> {fmt(a)}"
    ratio = b / a if a > 0 else float("inf")
    mark = " *" if (nb or na) else ""
    if 0.9 <= ratio <= 1.1:
        return f"{fmt(b)} -> {fmt(a)} (same){mark}"
    return f"{fmt(b)} -> {fmt(a)} ({ratio:.1f}x){mark}" if ratio > 1 else f"{fmt(b)} -> {fmt(a)} ({1 / ratio:.1f}x slower){mark}"


def main():
    before, after = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
    only = None
    md_path = None
    if "--files" in sys.argv:
        only = sys.argv[sys.argv.index("--files") + 1].split(",")
    if "--md" in sys.argv:
        md_path = sys.argv[sys.argv.index("--md") + 1]
    names = sorted(os.listdir(os.path.join(after, "cells", "view-warm"))) if os.path.isdir(os.path.join(after, "cells", "view-warm")) else []
    names = [n[:-5] for n in names if n.endswith(".json")]
    if only:
        names = [n for n in names if n in only]
    lines = ["| File | Thumbnail cold (ms) | Thumbnail warm (ms) | View first cold (ms) | View first warm (ms) | View settled (ms) | View full (ms) |",
             "|---|---|---|---|---|---|---|"]
    for n in names:
        row = [n]
        for which in ("thumb_cold", "thumb_warm", "view_cold", "view_warm", "settled", "full"):
            row.append(pair(cell_value(before, n, which), cell_value(after, n, which)))
        lines.append("| " + " | ".join(row) + " |")
    text = "\n".join(lines)
    print(text)
    if md_path:
        with open(md_path, "w", encoding="utf-8") as f:
            f.write(text + "\n")


if __name__ == "__main__":
    main()
