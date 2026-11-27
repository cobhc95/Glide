"""Aggregates the cells written by run-format-audit.ps1 into one table (JSON + markdown) and lists the noisy cells.

Usage: python tools/perf/aggregate-format-audit.py <results-dir> <data-dir>
Writes <results-dir>/audit.json, audit-tables.md and noisy.txt (file names to re-run with `run-format-audit.ps1 -Only`).
A timing is NOISY when its samples spread by more than 40 % of the median and more than 3 ms, or when the phase started above the busy threshold.
"""
from __future__ import annotations

import json
import os
import sys

res = os.path.abspath(sys.argv[1])
data = os.path.abspath(sys.argv[2])
cells = os.path.join(res, "cells")


def load(phase, name):
    p = os.path.join(cells, phase, name + ".json")
    if not os.path.exists(p):
        return None
    with open(p, encoding="utf-8-sig") as f:
        return json.load(f)


def med(v):
    v = sorted(x for x in v if x is not None)
    if not v:
        return None
    m = len(v) // 2
    return v[m] if len(v) % 2 else (v[m - 1] + v[m]) / 2


def noisy(samples, median):
    s = [x for x in (samples or []) if x is not None]
    if len(s) < 3 or median is None:
        return False
    spread = max(s) - min(s)
    return spread > 3.0 and spread / max(median, 0.01) > 0.4


def ms(v):
    if v is None:
        return "n/a"
    return f"{v:.2f}" if v < 10 else f"{v:.0f}" if v >= 100 else f"{v:.1f}"


names = sorted(f for f in os.listdir(os.path.join(data, "single")) if not f.endswith(".tmp"))
rows = []
for n in names:
    path = os.path.join(data, "single", n)
    r = {"file": n, "mb": os.path.getsize(path) / 1e6}
    route = load("route", n) or {}
    r.update(listed=route.get("listed"), thumbRoute=route.get("route"), tier=route.get("tier"), provider=route.get("provider"), ext=route.get("ext"))
    tw = load("thumb-warm", n) or {}
    tc = load("thumb-cold", n) or {}
    r.update(thumbWarm=tw.get("warmMs"), thumbWarmSamples=tw.get("samples"), thumbHit=tw.get("hitMs"), thumbSize=tw.get("size"),
             thumbCold=tc.get("coldMs"), thumbColdSamples=tc.get("coldMsSamples"))
    vw = load("view-warm", n) or {}
    vc = load("view-cold", n) or {}
    r.update(viewWarm=vw.get("firstMs"), viewSettled=vw.get("settledMs"), viewFull=vw.get("fullMs"), viewRoute=vw.get("route"), viewSize=vw.get("firstSize"), fullSize=vw.get("fullSize"),
             viewErr=vw.get("error"), viewPeakMb=vw.get("peakDeltaMb"), viewFirstMin=vw.get("firstMin"), viewFirstMax=vw.get("firstMax"), viewFullMin=vw.get("fullMin"), viewFullMax=vw.get("fullMax"),
             viewCold=vc.get("coldFirstMs"), viewColdSamples=vc.get("coldFirstMsSamples"), viewColdFull=vc.get("coldFullMs"), viewColdErr=vc.get("meta_error"))
    sw = load("vsmall-warm", n) or {}
    sc = load("vsmall-cold", n) or {}
    r.update(vsWarm=sw.get("warmMs"), vsWarmSamples=sw.get("samples"), vsCold=sc.get("coldMs"), vsColdSamples=sc.get("coldMsSamples"), vsSize=sw.get("size"), vsRoute=sw.get("route"), vsErr=sw.get("error"))
    for tag in ("inbox", "glide"):
        w = load(tag + "-warm", n) or {}
        c = load(tag + "-cold", n) or {}
        r.update({tag + "Warm": w.get("warmMs"), tag + "WarmSamples": w.get("samples"), tag + "Size": w.get("size"), tag + "Hr": w.get("hr"),
                  tag + "Cold": c.get("getMs"), tag + "ColdSamples": c.get("getMsSamples")})
    fa = load("factory", n) or {}
    r.update(factoryWarm=fa.get("warmMs"), factoryFirst=fa.get("firstMs"), factoryHandler=fa.get("handler"))
    busy = [ph for ph in ("thumb-warm", "thumb-cold", "view-warm", "view-cold", "vsmall-warm", "vsmall-cold", "inbox-warm", "inbox-cold", "glide-warm", "glide-cold")
            if (load(ph, n) or {}).get("busy")]
    r["busyPhases"] = busy
    flags = []
    for key, samples, m in [("thumbWarm", r["thumbWarmSamples"], r["thumbWarm"]), ("thumbCold", r["thumbColdSamples"], r["thumbCold"]),
                            ("viewCold", r["viewColdSamples"], r["viewCold"]), ("vsWarm", r["vsWarmSamples"], r["vsWarm"]),
                            ("vsCold", r["vsColdSamples"], r["vsCold"]), ("inboxWarm", r["inboxWarmSamples"], r["inboxWarm"]),
                            ("glideWarm", r["glideWarmSamples"], r["glideWarm"]), ("glideCold", r["glideColdSamples"], r["glideCold"])]:
        if noisy(samples, m):
            flags.append(key)
    if r["viewFirstMin"] is not None and r["viewWarm"] is not None:
        if (r["viewFirstMax"] - r["viewFirstMin"]) > 3 and (r["viewFirstMax"] - r["viewFirstMin"]) / max(r["viewWarm"], 0.01) > 0.4: flags.append("viewWarm")
        if r["viewFull"] and (r["viewFullMax"] - r["viewFullMin"]) > 3 and (r["viewFullMax"] - r["viewFullMin"]) / max(r["viewFull"], 0.01) > 0.4: flags.append("viewFull")
    r["noisy"] = flags
    rows.append(r)

with open(os.path.join(res, "audit.json"), "w", encoding="utf-8") as f:
    json.dump(rows, f, indent=1)
with open(os.path.join(res, "noisy.txt"), "w", encoding="utf-8") as f:
    f.write(",".join(r["file"] for r in rows if r["noisy"] or r["busyPhases"]))
print(f"{len(rows)} rows; noisy cells in {sum(1 for r in rows if r['noisy'])} files; busy-phase cells in {sum(1 for r in rows if r['busyPhases'])} files")
for r in rows:
    if r["noisy"]:
        print("  NOISY", r["file"], r["noisy"])
