"""Generates the deterministic thumbnail / first-paint benchmark fixtures (synthetic images, never downloaded).

Usage:  python tools/perf/generate_thumb_fixtures.py <output-dir> [--force]

Writes <output-dir>/single/*, the layout `Glide.PerfBench thumb` and `Glide.PerfBench decode` read. One format per
file (name prefix = format key used in the benchmark report): JPEG (baseline, progressive, camera JPEG with a CIPA
MPF large preview and EXIF thumbnail), PNG (opaque, alpha), WebP, HEIC, AVIF, TIFF (LZW, uncompressed),
GIF, BMP, ICO. Camera RAW is not generated: no RAW writer exists in this repo and a real RAW cannot be synthesised.
"""
from __future__ import annotations

import importlib.util
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from PIL import Image  # noqa: E402
import generate_perf_data as perf  # noqa: E402


def _load_mpf():
    spec = importlib.util.spec_from_file_location("mpf", os.path.join(HERE, "..", "bench-507", "make_jpeg_fixtures.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    out = os.path.abspath(sys.argv[1])
    force = "--force" in sys.argv
    single = os.path.join(out, "single")
    os.makedirs(single, exist_ok=True)
    mpf = _load_mpf()
    try:
        import pillow_heif
        pillow_heif.register_heif_opener()
        have_heic = True
    except Exception:
        have_heic = False

    def target(name):
        path = os.path.join(single, name)
        return None if os.path.exists(path) and not force else path

    photos: dict[tuple[str, int], Image.Image] = {}

    def photo(size_key, seed, alpha=False):
        key = (size_key, seed, alpha)
        if key not in photos:
            w, h = perf.SIZES[size_key]
            photos.clear()
            photos[key] = perf.make_photo(w, h, seed, alpha=alpha)
        return photos[key]

    for size_key in ["12mp", "24mp"]:
        image = photo(size_key, 11)
        if p := target(f"jpeg-base-{size_key}.jpg"):
            image.save(p, "JPEG", quality=90, subsampling=2)
        if p := target(f"jpeg-prog-{size_key}.jpg"):
            image.save(p, "JPEG", quality=90, subsampling=2, progressive=True)
        if size_key == "24mp":
            if p := target("jpeg-camera-mpf-24mp.jpg"):
                # Camera file: EXIF orientation 6 + IFD1 thumbnail + MPF 1620x1080 large preview after the main image.
                thumb = image.copy(); thumb.thumbnail((160, 120))
                exif = perf.exif_with_orientation_and_thumbnail(6, thumb)
                buf = io.BytesIO()
                image.save(buf, "JPEG", quality=92, subsampling=2, exif=exif)
                prev = io.BytesIO()
                image.resize((1620, 1080)).save(prev, "JPEG", quality=85)
                with open(p, "wb") as f:
                    f.write(mpf.with_mpf(buf.getvalue(), prev.getvalue()))
    for size_key in ["4k", "24mp"]:
        image = photo(size_key, 21)
        if p := target(f"png-{size_key}.png"):
            image.save(p, "PNG", compress_level=6)
        if p := target(f"webp-{size_key}.webp"):
            image.save(p, "WEBP", quality=85, method=4)
        if p := target(f"tiff-lzw-{size_key}.tif"):
            image.save(p, "TIFF", compression="tiff_lzw")
        if p := target(f"tiff-raw-{size_key}.tif"):
            image.save(p, "TIFF", compression="raw")
        if p := target(f"bmp-{size_key}.bmp"):
            image.save(p, "BMP")
        if size_key == "4k":
            if p := target("gif-4k.gif"):
                image.quantize(colors=256, method=Image.Quantize.FASTOCTREE).save(p, "GIF")
            if have_heic and (p := target("heic-4k.heic")):
                image.save(p, "HEIF", quality=75)
            if p := target("avif-4k.avif"):
                try:
                    image.save(p, "AVIF", quality=60, speed=8)
                except Exception as ex:  # noqa: BLE001
                    print("AVIF skipped:", ex)
    alpha = photo("4k", 31, alpha=True)
    if p := target("png-alpha-4k.png"):
        alpha.save(p, "PNG", compress_level=6)
    if p := target("webp-alpha-4k.webp"):
        alpha.save(p, "WEBP", quality=85, method=4)
    if p := target("ico-256.ico"):
        photo("small", 41).resize((256, 256)).save(p, "ICO", sizes=[(256, 256), (48, 48), (32, 32), (16, 16)])
    for name in sorted(os.listdir(single)):
        print(f"  {name}: {os.path.getsize(os.path.join(single, name)) / 1e6:.2f} MB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
