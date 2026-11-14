"""Generates the synthetic Glide performance corpus (images are created, never downloaded).

Usage:  python tools/perf/generate_perf_data.py <output-dir> [--quick] [--force]

The corpus is deterministic (fixed seeds) so before/after runs measure identical bytes. Existing
files are reused unless --force is given; a manifest.json describes every generated file.
--quick skips the slowest entries (400 MP JPEG, 100 MP PNG/TIFF, 5000-file folder).
"""
from __future__ import annotations

import io
import json
import os
import struct
import sys
import time
from multiprocessing import Pool

import numpy as np
from PIL import Image, ImageDraw

STRIP_ROWS = 1024


def _noise_tile(seed: int) -> np.ndarray:
    rng = np.random.default_rng(seed)
    return rng.normal(0.0, 5.0, size=(512, 512, 3)).astype(np.int16)


def make_photo(width: int, height: int, seed: int = 1, alpha: bool = False) -> Image.Image:
    """Photo-like synthetic content: smooth gradients, low-frequency waves, shapes and mild noise.

    Pure noise compresses unrealistically badly and flat colour unrealistically well; this mix lands
    near real camera JPEG sizes (~2-4 bits/pixel at q90). Built in strips so 400 MP stays bounded.
    """
    channels = 4 if alpha else 3
    out = np.empty((height, width, channels), dtype=np.uint8)
    tile = _noise_tile(seed)
    xs = np.arange(width, dtype=np.float32)
    phase = (seed % 17) * 0.37
    for y0 in range(0, height, STRIP_ROWS):
        y1 = min(height, y0 + STRIP_ROWS)
        ys = np.arange(y0, y1, dtype=np.float32)[:, None]
        r = 30 + 190 * (xs[None, :] / max(1, width - 1)) + 20 * np.sin(ys / 211.0 + phase)
        g = 40 + 170 * (ys / max(1, height - 1)) + 25 * np.cos(xs[None, :] / 157.0 + phase)
        b = 128 + 70 * np.sin(xs[None, :] / 97.0 + ys / 53.0 + phase)
        strip = np.stack([r, g, b], axis=-1)
        reps_y = (y1 - y0 + 511) // 512
        reps_x = (width + 511) // 512
        noise = np.tile(tile, (reps_y, reps_x, 1))[: y1 - y0, :width, :]
        strip = np.clip(strip + noise, 0, 255).astype(np.uint8)
        out[y0:y1, :, :3] = strip
        if alpha:
            a = 255 * (0.5 + 0.5 * np.sin(xs[None, :] / 301.0 + ys / 199.0))
            out[y0:y1, :, 3] = np.clip(a, 0, 255).astype(np.uint8)
    image = Image.fromarray(out, "RGBA" if alpha else "RGB")
    draw = ImageDraw.Draw(image)
    rng = np.random.default_rng(seed + 1000)
    for _ in range(24):
        cx, cy = int(rng.integers(0, width)), int(rng.integers(0, height))
        rad = int(max(4, min(width, height) * float(rng.uniform(0.02, 0.12))))
        colour = tuple(int(c) for c in rng.integers(0, 255, size=channels))
        if rng.random() < 0.5:
            draw.ellipse([cx - rad, cy - rad, cx + rad, cy + rad], fill=colour)
        else:
            draw.rectangle([cx - rad, cy - rad // 2, cx + rad, cy + rad // 2], fill=colour)
    return image


def exif_with_orientation_and_thumbnail(orientation: int, thumb: Image.Image) -> bytes:
    """Raw APP1 Exif payload: IFD0 Orientation + IFD1 embedded JPEG thumbnail (little endian)."""
    buf = io.BytesIO()
    thumb.save(buf, "JPEG", quality=80)
    thumb_bytes = buf.getvalue()
    # TIFF header (8) | IFD0: count(2) + 2 entries(24) + next(4) | IFD1: count(2) + 2 entries(24) + next(4) | thumb
    ifd0_offset = 8
    ifd0_size = 2 + 2 * 12 + 4
    ifd1_offset = ifd0_offset + ifd0_size
    ifd1_size = 2 + 2 * 12 + 4
    thumb_offset = ifd1_offset + ifd1_size
    out = bytearray(b"II*\x00" + struct.pack("<I", ifd0_offset))
    out += struct.pack("<H", 2)
    out += struct.pack("<HHIHH", 0x0112, 3, 1, orientation, 0)       # Orientation SHORT
    out += struct.pack("<HHIHH", 0x0128, 3, 1, 2, 0)                 # ResolutionUnit SHORT (inches)
    out += struct.pack("<I", ifd1_offset)
    out += struct.pack("<H", 2)
    out += struct.pack("<HHII", 0x0201, 4, 1, thumb_offset)          # JPEGInterchangeFormat
    out += struct.pack("<HHII", 0x0202, 4, 1, len(thumb_bytes))      # JPEGInterchangeFormatLength
    out += struct.pack("<I", 0)
    out += thumb_bytes
    return b"Exif\x00\x00" + bytes(out)


SIZES = {
    "small": (800, 600),
    "4k": (3840, 2160),
    "12mp": (4000, 3000),
    "24mp": (6000, 4000),
    "50mp": (8688, 5792),
    "100mp": (12288, 8192),
    "20000sq": (20000, 20000),
}


def single_specs(quick: bool) -> list[dict]:
    specs: list[dict] = []
    def add(name, fmt, size, **kw):
        specs.append({"name": name, "format": fmt, "size": size, **kw})
    for size in ["small", "4k", "12mp", "24mp", "50mp", "100mp"]:
        add(f"jpeg-base-{size}.jpg", "jpeg", size)
    for size in ["4k", "24mp", "50mp", "100mp"]:
        add(f"jpeg-prog-{size}.jpg", "jpeg", size, progressive=True)
    for size in ["12mp", "24mp"]:
        add(f"jpeg-exif6-thumb-{size}.jpg", "jpeg", size, orientation=6)
    if not quick:
        add("jpeg-base-20000sq.jpg", "jpeg", "20000sq")
    for size in ["small", "4k", "24mp"]:
        add(f"png-{size}.png", "png", size)
    add("png-alpha-24mp.png", "png", "24mp", alpha=True)
    if not quick:
        add("png-100mp.png", "png", "100mp")
    for size in ["small", "4k", "24mp", "50mp"]:
        add(f"webp-{size}.webp", "webp", size)
    add("webp-alpha-4k.webp", "webp", "4k", alpha=True)
    for size in ["small", "4k", "24mp", "100mp"]:
        add(f"bmp-{size}.bmp", "bmp", size)
    for size in ["4k", "24mp"]:
        add(f"tiff-lzw-{size}.tif", "tiff", size, compression="tiff_lzw")
    add("tiff-raw-50mp.tif", "tiff", "50mp", compression="raw")
    if not quick:
        add("tiff-lzw-100mp.tif", "tiff", "100mp", compression="tiff_lzw")
    add("gif-static-4k.gif", "gif", "4k")
    add("gif-anim-1080p-60f.gif", "gif-anim", (1920, 1080), frames=60)
    add("gif-anim-small-200f.gif", "gif-anim", (800, 600), frames=200)
    return specs


def write_single(args) -> dict:
    spec, out_dir, force = args
    path = os.path.join(out_dir, "single", spec["name"])
    if os.path.exists(path) and not force:
        return {**spec, "path": path, "bytes": os.path.getsize(path), "reused": True}
    size = spec["size"]
    width, height = SIZES[size] if isinstance(size, str) else size
    seed = sum(map(ord, spec["name"]))
    started = time.time()
    fmt = spec["format"]
    tmp = path + ".tmp"
    if fmt == "gif-anim":
        base = make_photo(width, height, seed).quantize(colors=128, method=Image.Quantize.FASTOCTREE)
        frames = []
        for i in range(spec["frames"]):
            frame = base.copy()
            ImageDraw.Draw(frame).ellipse([(i * 13) % width, (i * 7) % height, (i * 13) % width + 120, (i * 7) % height + 120], fill=(i * 3) % 128)
            frames.append(frame)
        frames[0].save(tmp, "GIF", save_all=True, append_images=frames[1:], duration=40, loop=0, optimize=False)
    else:
        image = make_photo(width, height, seed, alpha=spec.get("alpha", False))
        if fmt == "jpeg":
            kw = {"quality": 90, "subsampling": 2, "optimize": False, "progressive": spec.get("progressive", False)}
            if spec.get("orientation"):
                thumb = image.copy(); thumb.thumbnail((160, 120))
                kw["exif"] = exif_with_orientation_and_thumbnail(spec["orientation"], thumb)
            image.save(tmp, "JPEG", **kw)
        elif fmt == "png":
            image.save(tmp, "PNG", compress_level=6 if width * height < 30_000_000 else 1)
        elif fmt == "webp":
            image.save(tmp, "WEBP", quality=85, method=4 if width * height < 30_000_000 else 0)
        elif fmt == "bmp":
            image.save(tmp, "BMP")
        elif fmt == "tiff":
            image.save(tmp, "TIFF", compression=spec.get("compression", "raw"))
        elif fmt == "gif":
            image.quantize(colors=256, method=Image.Quantize.FASTOCTREE).save(tmp, "GIF")
        else:
            raise ValueError(fmt)
    os.replace(tmp, path)
    return {**spec, "path": path, "bytes": os.path.getsize(path), "seconds": round(time.time() - started, 2)}


def write_folder_item(args) -> str:
    folder, index, width, height, quality, force = args
    path = os.path.join(folder, f"img_{index:05d}.jpg")
    if os.path.exists(path) and not force:
        return path
    image = make_photo(width, height, seed=index * 7 + width)
    image.save(path + ".tmp", "JPEG", quality=quality, subsampling=2)
    os.replace(path + ".tmp", path)
    return path


FOLDERS = [
    ("folder-50-24mp", 50, 6000, 4000, 90),
    ("folder-500-4k", 500, 3840, 2160, 88),
    ("folder-5000-small", 5000, 1024, 768, 85),
]


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    out_dir = os.path.abspath(sys.argv[1])
    quick = "--quick" in sys.argv
    force = "--force" in sys.argv
    os.makedirs(os.path.join(out_dir, "single"), exist_ok=True)
    workers = max(1, min(12, (os.cpu_count() or 4) - 2))
    started = time.time()
    manifest = {"generated_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "single": [], "folders": []}
    specs = single_specs(quick)
    # Largest first so the long encodes overlap with everything else.
    specs.sort(key=lambda s: -(SIZES[s["size"]][0] * SIZES[s["size"]][1] if isinstance(s["size"], str) else s["size"][0] * s["size"][1]))
    with Pool(workers) as pool:
        for entry in pool.imap_unordered(write_single, [(s, out_dir, force) for s in specs]):
            print(f"  {entry['name']}: {entry['bytes'] / 1e6:.1f} MB", flush=True)
            manifest["single"].append(entry)
        for name, count, width, height, quality in FOLDERS:
            if quick and count > 500:
                continue
            folder = os.path.join(out_dir, "folders", name)
            os.makedirs(folder, exist_ok=True)
            paths = list(pool.imap_unordered(write_folder_item, [(folder, i, width, height, quality, force) for i in range(count)], chunksize=8))
            total = sum(os.path.getsize(p) for p in paths)
            print(f"  {name}: {len(paths)} files, {total / 1e6:.0f} MB", flush=True)
            manifest["folders"].append({"name": name, "path": folder, "count": len(paths), "width": width, "height": height, "bytes": total})
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2)
    print(f"Corpus ready in {time.time() - started:.0f}s: {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
