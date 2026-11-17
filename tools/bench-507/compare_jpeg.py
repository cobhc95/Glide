"""Compare JPEG round runs (summary.csv) against a baseline run: per file/mode ratio and geometric means.

    python tools/bench-507/compare_jpeg.py <results-root> <baseline-label> <label> [<label> ...] [--detail] [--md]
"""
import csv, math, os, sys
from collections import defaultdict


def load(root, label):
    rows = {}
    with open(os.path.join(root, label, "summary.csv"), newline="") as f:
        for r in csv.DictReader(f):
            if r["engine"] != "glide":
                continue
            rows[(r["file"], r["mode"])] = r
    return rows


def group(r):
    f = r["file"].replace("\\", "/")
    if r["set"] == "synthetic":
        kind = "synth-prog" if "prog" in r["variant"] else "synth-base"
    elif "/derived/" in f:
        kind = "derived-" + r["variant"]
    else:
        kind = "real-" + f.split("/")[-2]
    return kind


def main():
    a = [x for x in sys.argv[1:] if not x.startswith("--")]
    detail, md = "--detail" in sys.argv, "--md" in sys.argv
    root, base = a[0], a[1]
    b = load(root, base)
    for label in a[2:]:
        o = load(root, label)
        groups = defaultdict(list)
        print(f"\n### {label} vs {base}")
        for key, r in sorted(o.items()):
            if key not in b or not r["median_ms"] or not b[key]["median_ms"]:
                continue
            ratio = float(r["median_ms"]) / float(b[key]["median_ms"])
            g = (group(r), key[1])
            groups[g].append(ratio)
            groups[("ALL", key[1])].append(ratio)
            if detail:
                print(f"{os.path.basename(key[0]):48s} {key[1]:7s} {float(b[key]['median_ms']):8.1f} -> {float(r['median_ms']):8.1f}  x{ratio:5.2f}"
                      f"  peakMB {b[key]['peak_delta_mb_max']}->{r['peak_delta_mb_max']}  busy {r['busy_max_pct']}  {r['route']} {r['error']}")
        for g in sorted(groups):
            v = groups[g]
            gm = math.exp(sum(math.log(x) for x in v) / len(v))
            print(f"  {g[0]:24s} {g[1]:7s} n={len(v):3d} geomean x{gm:5.2f}  min x{min(v):5.2f} max x{max(v):5.2f}")


if __name__ == "__main__":
    main()
