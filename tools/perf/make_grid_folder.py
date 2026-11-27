"""Builds the 200-image mixed folder the `Glide.PerfBench grid` mode times (copies of the thumbnail fixtures under distinct names).

Usage:  python tools/perf/make_grid_folder.py <fixture-dir>/single <output-folder>

Mix (200 files): 70 JPEG (30 baseline 12 MP, 15 baseline 24 MP, 10 progressive 24 MP, 15 camera JPEG with an MPF preview),
35 PNG (25 opaque 4K, 10 with alpha), 30 WebP (20 lossy 4K, 10 24 MP), 25 TIFF (4K, LZW and uncompressed), 15 BMP 4K,
15 GIF 4K, 10 ICO. Distinct paths, so the disk cache and the OS see 200 different files.
"""
import os
import shutil
import sys

PLAN = [
    ("jpeg-base-12mp.jpg", 30), ("jpeg-base-24mp.jpg", 15), ("jpeg-prog-24mp.jpg", 10), ("jpeg-camera-mpf-24mp.jpg", 15),
    ("png-4k.png", 25), ("png-alpha-4k.png", 10),
    ("webp-4k.webp", 20), ("webp-24mp.webp", 10),
    ("tiff-lzw-4k.tif", 13), ("tiff-raw-4k.tif", 12),
    ("bmp-4k.bmp", 15), ("gif-4k.gif", 15), ("ico-256.ico", 10),
]


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    source, target = sys.argv[1], sys.argv[2]
    os.makedirs(target, exist_ok=True)
    count = 0
    for name, copies in PLAN:
        stem, ext = os.path.splitext(name)
        for i in range(copies):
            destination = os.path.join(target, f"{stem}-{i:02d}{ext}")
            if not os.path.exists(destination):
                shutil.copyfile(os.path.join(source, name), destination)
            count += 1
    print(f"{count} files in {target}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
