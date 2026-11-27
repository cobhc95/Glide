"""Final before / after table of the 5.2.0 format speed work: the committed baseline audit against a run-format-audit.ps1 result folder.

Usage: python tools/perf/compare-format-final.py <baseline-audit.md> <after-results> [--md out.md] [--files a.tif,b.heic]

<baseline-audit.md> is docs/audits/format-speed-audit-5.2.0.md (commit fa5dfb2: its tables are the "before" numbers; the raw result folder of that run
was not kept). <after-results> is the -Out folder of run-format-audit.ps1 on the merged build (needs the thumb-warm, thumb-cold, view-warm and
view-cold phases). One row per fixture, four columns, each "before -> after (Nx)":
  thumb cold   production ThumbnailDecode.DecodeToWidth(236 px), first call in a fresh process
  thumb warm   the same, median of the warm runs
  view cold    the viewer's first frame, first open in a fresh process
  view warm    the viewer's first frame, median of the warm runs
"before" for a format the Explorer tab did not thumbnail ("glyph") is the viewer decoder bounded to 480 px (the audit's "As-if thumbnail" table),
shown as "glyph (as-if N)"; its ratio is computed against that number. Markers: "*" after = the new sample is noisy (spread over 40 % of the median and
over 3 ms, or the machine was busy when the phase started); "d" before = the baseline row carried a dagger / noisy flag in the audit.
"""
from __future__ import annotations

import json
import os
import re
import sys


def table_rows(md, heading):
    """Rows (list of cell strings) of the first markdown table after the heading that starts with `heading`."""
    lines = md.splitlines()
    start = next(i for i, l in enumerate(lines) if l.startswith(heading))
    rows = []
    for l in lines[start + 1:]:
        if l.startswith("## ") and rows:
            break
        if l.startswith("|") and not l.startswith("|---"):
            rows.append([c.strip() for c in l.strip().strip("|").split("|")])
    return rows[1:]  # drop the header row


def num(s):
    s = s.strip()
    try:
        return float(s)
    except ValueError:
        return None


def load_baseline(path):
    md = open(path, encoding="utf-8").read()
    out = {}
    for r in table_rows(md, "## Explorer-tab thumbnail"):
        name, asks, cold, warm, flags = r[0], r[2], num(r[4]), num(r[5]), r[-1]
        out[name] = dict(asks=asks == "yes", thumb_cold=cold, thumb_warm=warm, flags_t=flags)
    for r in table_rows(md, "## Viewer, every fixture"):
        name = r[0]
        out.setdefault(name, {}).update(view_cold=num(r[2]), view_warm=num(r[3]), flags_v=r[-1])
    for r in table_rows(md, "## As-if thumbnail"):
        name = r[0]
        out.setdefault(name, {}).update(asif_cold=num(r[1]), asif_warm=num(r[2]))
    return out


def load(root, phase, name):
    p = os.path.join(root, "cells", phase, name + ".json")
    if not os.path.exists(p):
        return None
    with open(p, encoding="utf-8-sig") as f:
        return json.load(f)


def spread_noisy(cell, key, samples):
    if cell is None:
        return False
    s = [x for x in (samples or []) if x is not None]
    m = cell.get(key)
    spread = (max(s) - min(s)) if len(s) >= 3 else 0
    return bool(cell.get("busy")) or (m is not None and spread > 3.0 and spread / max(m, 0.01) > 0.4)


def after_value(root, name, which):
    if which == "thumb_cold":
        c = load(root, "thumb-cold", name)
        return (c.get("coldMs"), spread_noisy(c, "coldMs", c.get("coldMsSamples"))) if c else (None, False)
    if which == "thumb_warm":
        c = load(root, "thumb-warm", name)
        return (c.get("warmMs"), spread_noisy(c, "warmMs", c.get("samples"))) if c else (None, False)
    if which == "view_cold":
        c = load(root, "view-cold", name)
        return (c.get("coldFirstMs"), spread_noisy(c, "coldFirstMs", c.get("coldFirstMsSamples"))) if c else (None, False)
    c = load(root, "view-warm", name)
    if not c:
        return None, False
    s = [c.get("firstMin"), c.get("firstMax")]
    spread = (s[1] - s[0]) if all(x is not None for x in s) else 0
    m = c.get("firstMs")
    return m, bool(c.get("busy")) or (m is not None and spread > 3.0 and spread / max(m, 0.01) > 0.4)


def fmt(v):
    if v is None:
        return "n/a"
    return f"{v:.2f}" if v < 10 else f"{v:.1f}" if v < 100 else f"{v:.0f}"


def ratio_text(b, a):
    if b is None or a is None or a <= 0:
        return ""
    r = b / a
    if 0.9 <= r <= 1.1:
        return "same"
    return f"{r:.1f}x" if r > 1 else f"{1 / r:.1f}x slower"


def main():
    base_md = sys.argv[1]
    after = os.path.abspath(sys.argv[2])
    md_path = sys.argv[sys.argv.index("--md") + 1] if "--md" in sys.argv else None
    only = sys.argv[sys.argv.index("--files") + 1].split(",") if "--files" in sys.argv else None
    base = load_baseline(base_md)
    names = sorted(n[:-5] for n in os.listdir(os.path.join(after, "cells", "view-warm")) if n.endswith(".json"))
    if only:
        names = [n for n in names if n in only]
    head = ["| Fixture | Thumbnail cold (ms) | Thumbnail warm (ms) | View first cold (ms) | View first warm (ms) |", "|---|---|---|---|---|"]
    rows, ratios, noisy_cells = [], {"thumb_cold": [], "thumb_warm": [], "view_cold": [], "view_warm": []}, []
    for n in names:
        b = base.get(n, {})
        cells = [n]
        for which in ("thumb_cold", "thumb_warm", "view_cold", "view_warm"):
            a, noisy = after_value(after, n, which)
            bv = b.get(which)
            glyph = which.startswith("thumb") and not b.get("asks", True)
            if glyph:
                asif = b.get("asif_cold" if which == "thumb_cold" else "asif_warm")
                before_txt = f"glyph (as-if {fmt(asif)})"
                bv_cmp = asif
            else:
                flags = b.get("flags_t" if which.startswith("thumb") else "flags_v", "")
                dag = "d" if (("thumb-" + which.split("_")[1]) in flags or ("view-" + which.split("_")[1]) in flags or "noisy" in flags and which == "view_warm") else ""
                before_txt = fmt(bv) + dag
                bv_cmp = bv
            r = ratio_text(bv_cmp, a)
            mark = " *" if noisy else ""
            if noisy:
                noisy_cells.append(f"{n}:{which}")
            cells.append(f"{before_txt} -> {fmt(a)}{(' (' + r + ')') if r else ''}{mark}")
            if bv_cmp and a and a > 0:
                ratios[which].append(bv_cmp / a)
        rows.append("| " + " | ".join(cells) + " |")
    text = "\n".join(head + rows)
    print(text)
    summary = []
    for which, rs in ratios.items():
        if rs:
            faster = sum(1 for r in rs if r > 1.1)
            slower = sum(1 for r in rs if r < 1 / 1.1)
            rs_sorted = sorted(rs)
            summary.append(f"{which}: {len(rs)} compared, {faster} faster (> 1.1x), {len(rs) - faster - slower} same, {slower} slower; median ratio {rs_sorted[len(rs_sorted) // 2]:.2f}x")
    print("\n".join(summary), file=sys.stderr)
    print("noisy cells:", len(noisy_cells), file=sys.stderr)
    if md_path:
        with open(md_path, "w", encoding="utf-8") as f:
            f.write(text + "\n\n" + "\n".join(summary) + "\n")
        with open(os.path.splitext(md_path)[0] + "-noisy.txt", "w", encoding="utf-8") as f:
            f.write("\n".join(noisy_cells))


if __name__ == "__main__":
    main()
