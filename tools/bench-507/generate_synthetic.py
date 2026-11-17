"""Deterministic synthetic corpus for the 5.0.7 decode benchmark (images are generated, never downloaded).

Usage:
  python tools/bench-507/generate_synthetic.py <out-dir> --list                 # print the matrix, write nothing
  python tools/bench-507/generate_synthetic.py <out-dir> --confirm [--jobs 2]   # generate (CPU heavy: ask the user first)
      [--tier quick|full] [--only jpeg,png,...] [--force]
      [--cjxl <path to cjxl.exe>] [--vips <path to vips.exe>]   (JXL and tiled TIFF need these; skipped otherwise)

Sizes: 12 MP 4000x3000, 24 MP 6000x4000, 50 MP 8688x5792, 100 MP 12288x8192.
Content profiles (both seeded, identical bytes on every run):
  smooth - tools/perf make_photo(): gradients, waves, shapes, mild noise. Same generator as the 5.0.0 perf table,
           so lossless results stay comparable with earlier releases. Compresses better than real photos.
  detail - smooth + high-frequency texture (multi-scale noise, fine stripes, sensor-like grain). Lands near real
           camera entropy (JPEG q90 ~3-5 bits/pixel). Used for every lossy format, because the coordinator's
           real "converted" files (webp/avif/heic/jxl/jp2) came from smooth sources and are unrealistically small.
Writes <out-dir>/<format>/<name> plus <out-dir>/manifest.csv (file,format,variant,content,width,height,bytes,seed).
"""
from __future__ import annotations

import argparse
import csv
import importlib.util
import io
import os
import subprocess
import sys
import time
from multiprocessing import Pool

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
_perf_spec = importlib.util.spec_from_file_location("glide_perf_data", os.path.join(HERE, "..", "perf", "generate_perf_data.py"))
perf = importlib.util.module_from_spec(_perf_spec)
_perf_spec.loader.exec_module(perf)  # project script: reuses make_photo / exif_with_orientation_and_thumbnail

SIZES = {12: (4000, 3000), 24: (6000, 4000), 50: (8688, 5792), 100: (12288, 8192)}
STRIP = 1024


def make_detail(width: int, height: int, seed: int, alpha: bool = False) -> Image.Image:
    base = np.asarray(perf.make_photo(width, height, seed, alpha=alpha)).copy()
    rng = np.random.default_rng(seed + 77)
    fine = rng.normal(0.0, 14.0, size=(256, 256, 3)).astype(np.float32)
    coarse = rng.normal(0.0, 22.0, size=(64, 64, 3)).astype(np.float32)
    xs = np.arange(width, dtype=np.float32)
    for y0 in range(0, height, STRIP):
        y1 = min(height, y0 + STRIP)
        ys = np.arange(y0, y1, dtype=np.float32)[:, None]
        n_fine = np.tile(fine, ((y1 - y0 + 255) // 256, (width + 255) // 256, 1))[: y1 - y0, :width]
        # coarse noise upsampled x8 (blocky blobs -> mid frequencies)
        c = np.repeat(np.repeat(coarse, 8, axis=0), 8, axis=1)
        n_coarse = np.tile(c, ((y1 - y0 + 511) // 512, (width + 511) // 512, 1))[: y1 - y0, :width]
        stripes = 18.0 * np.sin(xs[None, :] * 0.9 + ys * 0.37)[..., None] * (np.sin(ys / 97.0) > 0.3)[..., None]
        strip = base[y0:y1, :, :3].astype(np.float32) + n_fine + 0.5 * n_coarse + stripes
        base[y0:y1, :, :3] = np.clip(strip, 0, 255).astype(np.uint8)
    return Image.fromarray(base, "RGBA" if alpha else "RGB")


def S(fmt, variant, mps, content, ext, tier="full", **kw):
    return [dict(format=fmt, variant=variant, mp=mp, content=content, ext=ext, tier=("quick" if mp == 24 else tier), **kw) for mp in mps]


def matrix() -> list[dict]:
    m: list[dict] = []
    m += S("jpeg", "base420-q90", [12, 24, 50, 100], "detail", "jpg", quality=90, subsampling=2)
    m += S("jpeg", "prog420-q90", [12, 24, 50, 100], "detail", "jpg", quality=90, subsampling=2, progressive=True)
    m += S("jpeg", "base444-q95", [24, 100], "detail", "jpg", quality=95, subsampling=0)
    m += S("jpeg", "base420-rst1", [24, 100], "detail", "jpg", quality=90, subsampling=2, restart_rows=1)
    m += S("jpeg", "base420-exif6", [24], "detail", "jpg", quality=90, subsampling=2, orientation=6)
    m += S("jpeg", "base420-q90-smooth", [24], "smooth", "jpg", quality=90, subsampling=2)
    m += S("png", "rgb8-z6", [12, 24, 50], "smooth", "png", level=6)
    m += S("png", "rgb8-z1", [100], "smooth", "png", level=1)
    m += S("png", "rgba8-z6", [24], "smooth", "png", level=6, alpha=True)
    m += S("png", "rgb8-z6-detail", [24], "detail", "png", level=6)
    m += S("webp", "lossy-q85", [12, 24, 50, 100], "detail", "webp", quality=85)
    m += S("webp", "lossless", [12, 24], "smooth", "webp", lossless=True)
    m += S("webp", "lossy-alpha-q85", [24], "detail", "webp", quality=85, alpha=True)
    m += S("tiff", "lzw", [12, 24, 50, 100], "smooth", "tif", compression="tiff_lzw")
    m += S("tiff", "deflate", [24, 100], "smooth", "tif", compression="tiff_adobe_deflate")
    m += S("tiff", "none", [24, 100], "smooth", "tif", compression="raw")
    m += S("tiff", "jpeg", [24], "detail", "tif", compression="jpeg")
    m += S("tiff", "tiled256-lzw", [24, 100], "smooth", "tif", tiled=True)
    m += S("bmp", "rgb24", [24, 100], "smooth", "bmp")
    m += S("gif", "static-256", [12], "smooth", "gif")
    m += S("qoi", "rgb", [24], "smooth", "qoi")
    m += S("avif", "q70-420-8bit", [12, 24, 50], "detail", "avif", quality=70)
    m += S("heic", "q80-420-8bit", [12, 24, 50], "detail", "heic", quality=80)
    m += S("jxl", "d1-e3", [12, 24, 50, 100], "detail", "jxl", distance=1.0, effort=3)
    m += S("jxl", "lossless-e1", [24], "smooth", "jxl", distance=0.0, effort=1)
    m += S("jp2", "lossy-r20", [12, 24, 50], "detail", "jp2", rate=20)
    for e in m:
        e["width"], e["height"] = SIZES[e["mp"]]
        e["name"] = f"{e['format']}-{e['variant']}-{e['mp']}mp.{e['ext']}"
        e["seed"] = sum(map(ord, e["name"]))
    return m


def write(args) -> dict:
    spec, out_dir, force, cjxl, vips = args
    rel = os.path.join(spec["format"], spec["name"])
    path = os.path.join(out_dir, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if os.path.exists(path) and not force:
        return {**spec, "file": rel, "bytes": os.path.getsize(path), "status": "reused"}
    started = time.time()
    w, h, seed, alpha = spec["width"], spec["height"], spec["seed"], spec.get("alpha", False)
    img = make_detail(w, h, seed, alpha) if spec["content"] == "detail" else perf.make_photo(w, h, seed, alpha=alpha)
    tmp = path + ".tmp"
    fmt = spec["format"]
    try:
        if fmt == "jpeg":
            kw = dict(quality=spec["quality"], subsampling=spec["subsampling"], optimize=False, progressive=spec.get("progressive", False))
            if spec.get("restart_rows"):
                kw["restart_marker_rows"] = spec["restart_rows"]
            if spec.get("orientation"):
                thumb = img.copy(); thumb.thumbnail((160, 120))
                kw["exif"] = perf.exif_with_orientation_and_thumbnail(spec["orientation"], thumb)
            img.save(tmp, "JPEG", **kw)
        elif fmt == "png":
            img.save(tmp, "PNG", compress_level=spec["level"])
        elif fmt == "webp":
            if spec.get("lossless"):
                img.save(tmp, "WEBP", lossless=True, quality=50, method=0)
            else:
                img.save(tmp, "WEBP", quality=spec["quality"], method=4)
        elif fmt == "tiff":
            if spec.get("tiled"):
                if not vips:
                    return {**spec, "file": rel, "bytes": 0, "status": "skipped (needs --vips)"}
                src = path + ".src.tif"
                img.save(src, "TIFF", compression="raw")
                subprocess.run([vips, "tiffsave", src, tmp + ".tif", "--tile", "--tile-width", "256", "--tile-height", "256", "--compression", "lzw"], check=True)
                os.replace(tmp + ".tif", tmp); os.remove(src)
            elif spec["compression"] == "jpeg":
                img.save(tmp, "TIFF", compression="jpeg", quality=90)
            else:
                img.save(tmp, "TIFF", compression=spec["compression"])
        elif fmt == "bmp":
            img.save(tmp, "BMP")
        elif fmt == "gif":
            img.quantize(colors=256, method=Image.Quantize.FASTOCTREE).save(tmp, "GIF")
        elif fmt == "qoi":
            img.save(tmp, "QOI")
        elif fmt == "avif":
            img.save(tmp, "AVIF", quality=spec["quality"], speed=8, subsampling="4:2:0")
        elif fmt == "heic":
            import pillow_heif
            pillow_heif.from_pillow(img).save(tmp, format="HEIF", quality=spec["quality"])
        elif fmt == "jxl":
            if not cjxl:
                return {**spec, "file": rel, "bytes": 0, "status": "skipped (needs --cjxl)"}
            src = path + ".src.ppm"
            img.save(src, "PPM")
            subprocess.run([cjxl, src, tmp + ".jxl", "-d", str(spec["distance"]), "-e", str(spec["effort"]), "--quiet"], check=True)
            os.replace(tmp + ".jxl", tmp); os.remove(src)
        elif fmt == "jp2":
            img.save(tmp, "JPEG2000", irreversible=True, quality_mode="rates", quality_layers=[spec["rate"]], num_resolutions=6)
        else:
            raise ValueError(fmt)
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)
    return {**spec, "file": rel, "bytes": os.path.getsize(path), "status": f"{time.time() - started:.1f}s"}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--confirm", action="store_true")
    ap.add_argument("--tier", choices=["quick", "full"], default="full")
    ap.add_argument("--only", default="")
    ap.add_argument("--jobs", type=int, default=2)
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--cjxl", default="")
    ap.add_argument("--vips", default="")
    a = ap.parse_args()
    specs = matrix()
    if a.tier == "quick":
        specs = [s for s in specs if s["tier"] == "quick"]
    if a.only:
        wanted = {x.strip() for x in a.only.split(",")}
        specs = [s for s in specs if s["format"] in wanted]
    if a.list or not a.confirm:
        for s in specs:
            print(f"{s['format']:5} {s['variant']:22} {s['mp']:3} MP  {s['content']:6}  {s['name']}")
        total_mp = sum(s["mp"] for s in specs)
        print(f"{len(specs)} files, {total_mp} MP total")
        if not a.confirm and not a.list:
            print("Nothing written: pass --confirm to generate (CPU heavy; the user must confirm first).", file=sys.stderr)
            return 3
        return 0
    os.makedirs(a.out, exist_ok=True)
    jobs = [(s, a.out, a.force, a.cjxl or None, a.vips or None) for s in specs]
    rows = []
    # Largest first so the long encodes overlap; few workers because every 100 MP image holds ~1 GB.
    jobs.sort(key=lambda j: -j[0]["mp"])
    with Pool(max(1, a.jobs)) as pool:
        for r in pool.imap_unordered(write, jobs):
            print(f"{r['file']:50} {r['bytes']:>12,} {r['status']}", flush=True)
            rows.append(r)
    # Merge with an existing manifest so separate invocations (e.g. --tier quick, then --only tiff) accumulate.
    header = ["file", "format", "variant", "content", "width", "height", "bytes", "seed"]
    merged = {}
    manifest = os.path.join(a.out, "manifest.csv")
    if os.path.exists(manifest):
        with open(manifest, newline="", encoding="utf-8") as f:
            for old in csv.DictReader(f):
                if os.path.exists(os.path.join(a.out, old["file"])):
                    merged[old["file"]] = [old[h] for h in header]
    for r in rows:
        if r["bytes"]:
            merged[r["file"]] = [r["file"], r["format"], r["variant"], r["content"], r["width"], r["height"], r["bytes"], r["seed"]]
    with open(manifest, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(header)
        for key in sorted(merged):
            w.writerow(merged[key])
    return 0


if __name__ == "__main__":
    sys.exit(main())
