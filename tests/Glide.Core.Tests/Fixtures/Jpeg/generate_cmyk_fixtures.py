"""Writes the CMYK / YCCK JPEG fixtures of JpegCmykTests (5.2.0).

    cl /O2 /EHsc mkycck.cpp /I native/third_party/libjpeg-turbo/include native/third_party/libjpeg-turbo/lib/x64/turbojpeg-static.lib     (from a VS developer prompt)
    python generate_cmyk_fixtures.py <path/to/mkycck.exe> [output-dir]                                       needs numpy, Pillow and tools/perf/generate_perf_data.py

mkycck.cpp compresses raw packed CMYK samples with libjpeg-turbo as plain CMYK or YCCK (Adobe marker, transform 2), baseline or progressive. The samples are written
inverted (255 = no ink), the way Photoshop stores them. cmyk-adobe-320x240.jpg was written by Pillow earlier and is not produced here.
"""
import os, subprocess, sys
import numpy as np
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "..", "..", "tools", "perf"))
import generate_perf_data as perf
EXE = sys.argv[1]
OUT = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else HERE
SCRATCH = os.environ.get("TEMP", ".")
def natural_cmyk(w, h, seed):
    # a CMYK photo with real K: take the RGB photo through a simple GCR separation (K = min ink, C/M/Y reduced by K)
    rgb = np.asarray(perf.make_photo(w, h, seed)).astype(np.float32) / 255.0
    c, m, y = 1 - rgb[..., 0], 1 - rgb[..., 1], 1 - rgb[..., 2]
    k = np.minimum(np.minimum(c, m), y) * 0.8
    out = np.stack([(c - k), (m - k), (y - k), k], -1)
    return np.clip(out * 255 + 0.5, 0, 255).astype(np.uint8)


def write(name, w, h, seed, subsamp, ycck, prog):
    nat = natural_cmyk(w, h, seed)
    stored = (255 - nat).astype(np.uint8)  # Adobe convention: 255 = no ink
    raw = SCRATCH + r"\exp\tmp.raw"
    stored.tofile(raw)
    p = os.path.join(OUT, name)
    subprocess.run([EXE, raw, str(w), str(h), p, str(subsamp), str(int(ycck)), str(int(prog))], check=True)
    print(name, os.path.getsize(p))


write("cmyk-ycck-420-1001x667.jpg", 1001, 667, 5, 2, True, False)
write("cmyk-ycck-progressive-1001x667.jpg", 1001, 667, 6, 0, True, True)
write("cmyk-ycck-444-2048x2000.jpg", 2048, 2000, 7, 0, True, False)
write("cmyk-plain-1001x667.jpg", 1001, 667, 8, 0, False, False)
