"""5.2.0 fixtures for Glide's own progressive DC reader (GlideJpegDc.cpp). Synthetic gradients, never a real picture.

Usage:  python generate_progressive_dc_fixtures.py   (writes next to this script)

progressive45-5464x8192.jpg    45 MP 4:2:0 progressive, libjpeg's standard scan script (interleaved DC first scan, Al = 1),
                               the structure of the 45 MP camera folders the Explorer tab was slow on; smooth, so only ~280 KB
progressive-dri-2000x3000.jpg  4:2:0 progressive with restart markers (DRI) every 37 MCUs
progressive-gray-2000x3000.jpg grayscale progressive
"""
import os

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))


def gradient(w, h):
    y = np.linspace(0, 1, h, dtype=np.float32)[:, None]
    x = np.linspace(0, 1, w, dtype=np.float32)[None, :]
    rgb = np.stack([60 + 150 * x + 0 * y, 40 + 120 * y + 0 * x, 90 + 80 * (1 - x) * y], -1)
    return Image.fromarray(rgb.astype(np.uint8), "RGB")


gradient(5464, 8192).save(os.path.join(HERE, "progressive45-5464x8192.jpg"), "JPEG", quality=40, progressive=True, subsampling="4:2:0", optimize=True)
gradient(2000, 3000).save(os.path.join(HERE, "progressive-dri-2000x3000.jpg"), "JPEG", quality=60, progressive=True, subsampling="4:2:0",
                          optimize=True, restart_marker_blocks=37)
gradient(2000, 3000).convert("L").save(os.path.join(HERE, "progressive-gray-2000x3000.jpg"), "JPEG", quality=60, progressive=True, optimize=True)
