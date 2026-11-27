"""Synthetic 45 MP progressive JPEG folder (5.2.0 thumbnail case: 5464 x 8192, 4:2:0, about 4.6 MB, no EXIF thumbnail).

Usage:  python tools/perf/generate_progressive_45mp.py <output-dir> [--count 133] [--quality 72]

The picture is a smooth gradient with fine noise (so the file size and scan structure resemble a camera photograph);
one master file is encoded, the rest are byte copies with a different name (the decoder cost is per file either way).
Nothing is downloaded and no real picture is used.
"""
from __future__ import annotations

import os
import shutil
import sys

import numpy as np
from PIL import Image


def make_master(path: str, quality: int) -> None:
    w, h = 5464, 8192
    rng = np.random.default_rng(45)
    y = np.linspace(0, 1, h, dtype=np.float32)[:, None]
    x = np.linspace(0, 1, w, dtype=np.float32)[None, :]
    r = 60 + 150 * x + 30 * np.sin(y * 17)
    g = 40 + 120 * y + 40 * np.cos(x * 11)
    b = 90 + 80 * (1 - x) * y
    rgb = np.stack([r + 0 * y, g + 0 * x, b], axis=-1)
    rgb += rng.normal(0, 7, size=(h, w, 1)).astype(np.float32)  # luma grain
    img = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB")
    img.save(path, "JPEG", quality=quality, progressive=True, subsampling="4:2:0", optimize=True)


def main() -> int:
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 2
    out = os.path.abspath(args[0])
    count = int(args[args.index("--count") + 1]) if "--count" in args else 133
    quality = int(args[args.index("--quality") + 1]) if "--quality" in args else 72
    os.makedirs(out, exist_ok=True)
    master = os.path.join(out, "p45-000.jpg")
    if not os.path.exists(master):
        make_master(master, quality)
    for i in range(1, count):
        target = os.path.join(out, f"p45-{i:03d}.jpg")
        if not os.path.exists(target):
            shutil.copyfile(master, target)
    print(f"{count} files, {os.path.getsize(master) / 1048576:.2f} MB each, in {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
