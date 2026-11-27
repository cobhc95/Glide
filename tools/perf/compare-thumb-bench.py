"""Merges two run-thumb-bench.ps1 result folders (before / after) into markdown tables.

Usage:  python tools/perf/compare-thumb-bench.py <results-dir> [before-label] [after-label]

Reads <results-dir>/<label>/thumb-<label>.json, thumb-cold-<label>.json, decode-<label>.json, load-<label>.json and prints
the tables used by docs/audits/thumbnail-perf-5.1.7.md.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path


def load(path: Path):
    if not path.exists():
        return None
    return json.loads(path.read_text(encoding="utf-8-sig"))


def fmt(v, digits=1):
    if v is None:
        return "n/a"
    if isinstance(v, float) and v != v:
        return "n/a"
    return f"{v:.{digits}f}" if v >= 10 else f"{v:.2f}"


def speedup(before, after):
    if before is None or after is None or before != before or after != after or after <= 0:
        return "-"
    return f"{before / after:.1f}x"


def main() -> int:
    root = Path(sys.argv[1])
    b_label = sys.argv[2] if len(sys.argv) > 2 else "before"
    a_label = sys.argv[3] if len(sys.argv) > 3 else "after"
    data = {}
    for label in (b_label, a_label):
        folder = root / label
        thumb = load(folder / f"thumb-{label}.json") or {"items": []}
        cold = load(folder / f"thumb-cold-{label}.json") or []
        decode = load(folder / f"decode-{label}.json") or {"items": []}
        loadlog = load(folder / f"load-{label}.json") or []
        data[label] = {
            "warm": {(i["file"], i["width"]): i for i in thumb["items"]},
            "cold": {(c["file"], c["width"]): c for c in cold},
            "paint": {i["file"]: i for i in decode["items"]},
            "load": loadlog,
        }
    b, a = data[b_label], data[a_label]
    files = sorted({k[0] for k in b["warm"]} | {k[0] for k in a["warm"]})

    print("### Explorer thumbnails (236 px wide tile, medians)\n")
    print("| File | Before warm ms | After warm ms | Warm speedup | Before cold ms | After cold ms | Cold speedup | After cache hit ms | Hit speedup vs before warm | Flags |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|")
    for f in files:
        k = (f, 236)
        bw, aw = b["warm"].get(k), a["warm"].get(k)
        bc, ac = b["cold"].get(k), a["cold"].get(k)
        bwm = bw["warmMs"] if bw and bw.get("warmMs") is not None else None
        awm = aw["warmMs"] if aw and aw.get("warmMs") is not None else None
        bcm = bc["coldMs"] if bc and bc.get("coldMs") is not None and bc["coldMs"] == bc["coldMs"] else None
        acm = ac["coldMs"] if ac and ac.get("coldMs") is not None and ac["coldMs"] == ac["coldMs"] else None
        hit = aw["hitMs"] if aw and aw.get("hitMs") is not None else None
        flags = []
        if bw and bw.get("noisy"): flags.append("before noisy")
        if aw and aw.get("noisy"): flags.append("after noisy")
        if bwm is None and awm is not None: flags.append("no thumbnail before")
        if awm is None: flags.append("no thumbnail after")
        base = bwm if bwm is not None else None
        print(f"| {f} | {fmt(bwm)} | {fmt(awm)} | {speedup(bwm, awm) if bwm else 'new'} | {fmt(bcm)} | {fmt(acm)} | {speedup(bcm, acm) if bcm else 'new'} | {fmt(hit)} | {speedup(base, hit) if base else 'new'} | {', '.join(flags)} |")

    print("\n### Viewer first paint (full-image open, 2560x1369 viewport, medians)\n")
    print("| File | Before cold first frame ms | After cold | Speedup | Before warm first frame ms | After warm | Speedup | Before settled ms | After settled | Before full decode ms | After full decode | Route before / after |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|")
    for f in sorted({*b["paint"], *a["paint"]}):
        bp, ap = b["paint"].get(f, {}), a["paint"].get(f, {})
        def g(d, k): return d.get(k) if d else None
        print(f"| {f} | {fmt(g(bp,'coldFirstMs'))} | {fmt(g(ap,'coldFirstMs'))} | {speedup(g(bp,'coldFirstMs'), g(ap,'coldFirstMs'))} | {fmt(g(bp,'firstMs'))} | {fmt(g(ap,'firstMs'))} | {speedup(g(bp,'firstMs'), g(ap,'firstMs'))} | {fmt(g(bp,'settledMs'))} | {fmt(g(ap,'settledMs'))} | {fmt(g(bp,'fullMs'))} | {fmt(g(ap,'fullMs'))} | {g(bp,'route') or '-'} / {g(ap,'route') or '-'} |")

    print("\n### Machine load during the runs\n")
    for label, d in ((b_label, b), (a_label, a)):
        samples = d["load"]
        busy = [s for s in samples if s.get("busy")]
        peak = max((s["cpu"] for s in samples), default=0)
        print(f"- {label}: {len(samples)} load samples, peak CPU {peak}%, busy samples (>= threshold): " + (", ".join(f"{s['phase']}={s['cpu']}%" for s in busy) if busy else "none"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
