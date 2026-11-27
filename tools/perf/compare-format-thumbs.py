#!/usr/bin/env python3
"""Before / after table for the 5.2.0 thumbnail work, from two run-format-audit.ps1 result folders.

usage: compare-format-thumbs.py <results-before> <results-after> [--all]

Per fixture: thumbnail cold / warm (Explorer tab path, 236 px, ThumbnailDecode.DecodeToWidth, disk cache off) and viewer
first-view cold / warm (+ full decode warm), before and after. "glyph" = no thumbnail produced (the tile shows the file-type icon).
Noise flag: the samples of a warm cell spread by more than 40 % of the median and more than 3 ms ("~"), or the cell started with
the machine above the busy threshold ("!"). With --all every fixture is listed; otherwise one representative per format family.
"""
import json
import os
import sys

REPRESENTATIVES = [
    "jpeg-base-24mp.jpg", "jpeg-prog-24mp.jpg", "jpeg-camera-mpf-24mp.jpg", "mpo-12mp.mpo", "png-24mp.png", "apng-720p.png",
    "gif-4k.gif", "bmp-24mp.bmp", "ico-256.ico", "cur-256.cur", "webp-24mp.webp", "tiff-lzw-24mp.tif", "jxr-12mp.jxr",
    "heic-12mp.heic", "avif-12mp.avif", "jxl-12mp.jxl", "jp2-4k.jp2", "j2k-4k.j2k",
    "raw-dng-1600-preview-24mp.dng", "raw-cr3-1620-preview-24mp.cr3", "raw-nef-fullsize-preview-24mp.nef", "raw-arw-1620-preview-24mp.arw",
    "raw-raf-fullsize-preview-24mp.raf", "psd-rle-4k.psd", "psd-zip-pred-4k.psd", "exr-zip-half-4k.exr", "hdr-4k.hdr", "pfm-4k.pfm",
    "svg-5000.svg", "svgz-5000.svgz", "svg-simple.svg", "svg-embedded-png.svg", "tga-4k.tga", "ppm-4k.ppm", "qoi-4k.qoi", "pcx-4k.pcx",
    "sgi-4k.sgi", "dds-dxt1-4k.dds", "ktx1-rgba8-4k.ktx", "fits-int16-2k.fits", "ora-4k.ora",
]


# The suffixes the Explorer tab asked a thumbnail for before 5.2.0 (MainWindow.CanUseBrowserThumbnailDecoder). For any other file the
# "before" decoder numbers are what ThumbnailDecode would have cost through Skia if the tab had asked; the tab itself showed the glyph.
LISTED = {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".jxr", ".wdp", ".hdp"}


def load(folder, phase, name):
    path = os.path.join(folder, "cells", phase, name + ".json")
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8-sig") as handle:
        return json.load(handle)


def noisy(samples):
    s = sorted(x for x in (samples or []) if x is not None)
    if len(s) < 3:
        return False
    med = s[len(s) // 2]
    return (s[-1] - s[0]) > 0.4 * max(med, 0.01) and (s[-1] - s[0]) > 3.0


def fmt(value, flag=""):
    if value is None:
        return "-"
    text = f"{value:.2f}" if value < 10 else f"{value:.1f}" if value < 100 else f"{value:.0f}"
    return text + flag


def thumb_cell(folder, name):
    warm = load(folder, "thumb-warm", name)
    cold = load(folder, "thumb-cold", name)
    w = c = None
    wflag = cflag = ""
    produced = False
    if warm:
        size = str(warm.get("size", ""))
        produced = warm.get("warmMs") is not None and size not in ("none",) and not size.startswith("error")
        if produced:
            w = warm["warmMs"]
            wflag = ("~" if noisy(warm.get("samples")) else "") + ("!" if warm.get("busy") else "")
    if cold and produced and cold.get("coldMs") is not None:
        c = cold["coldMs"]
        cflag = "!" if cold.get("busy") else ""
    return produced, c, cflag, w, wflag


def view_cell(folder, name):
    cold = load(folder, "view-cold", name)
    warm = load(folder, "view-warm", name)
    pre = load(folder, "view-cold-preload", name)
    c = cold.get("coldFirstMs") if cold else None
    cflag = "!" if cold and cold.get("busy") else ""
    w = warm.get("firstMs") if warm else None
    wflag = ""
    if warm and warm.get("firstMin") is not None and warm.get("firstMax") is not None and w:
        spread = warm["firstMax"] - warm["firstMin"]
        if spread > 0.4 * w and spread > 3.0:
            wflag += "~"
    if warm and warm.get("busy"):
        wflag += "!"
    full = warm.get("fullMs") if warm else None
    p = pre.get("coldFirstMs") if pre else None
    return c, cflag, w, wflag, full, p


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    before, after = sys.argv[1], sys.argv[2]
    names = sorted(os.listdir(os.path.join(after, "cells", "thumb-warm")))
    names = [n[:-5] for n in names if n.endswith(".json")]
    if "--all" not in sys.argv:
        names = [n for n in REPRESENTATIVES if n in names]
    print("| Fixture | Thumb cold ms before | after | after + preload | Thumb warm ms before | after | View first cold ms before | after | after + preload | View first warm ms before | after | View full warm ms before | after |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for name in names:
        bp, bc, bcf, bw, bwf = thumb_cell(before, name)
        shown_before = os.path.splitext(name)[1].lower() in LISTED
        ap, ac, acf, aw, awf = thumb_cell(after, name)
        bvc, bvcf, bvw, bvwf, bfull, _ = view_cell(before, name)
        avc, avcf, avw, avwf, afull, apre = view_cell(after, name)
        glyph = "glyph"
        tp = load(after, "thumb-cold-preload", name)
        tpre = fmt(tp["coldMs"]) if tp and tp.get("coldMs") is not None else ""
        print("| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
            name,
            fmt(bc, bcf) if bp and shown_before else (glyph + (f" ({fmt(bc, bcf)} decoder only)" if bp else "")),
            fmt(ac, acf) if ap else glyph, tpre,
            fmt(bw, bwf) if bp and shown_before else (glyph + (f" ({fmt(bw, bwf)} decoder only)" if bp else "")),
            fmt(aw, awf) if ap else glyph,
            fmt(bvc, bvcf), fmt(avc, avcf), fmt(apre) if apre is not None else "",
            fmt(bvw, bvwf), fmt(avw, avwf), fmt(bfull), fmt(afull)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
