"""Headless camera-JPEG thumbnail speed benchmark: Glide's Explorer provider vs Windows' own provider.

Generates (once) a folder of camera-like 24 MP JPEGs (6000x4000, sensor-noise texture, 4:2:0, Exif with a
standard 160x120 IFD1 thumbnail) and thumbnails every file in-process:

  * glide    Glide.ShellThumbnail.dll driven like Explorer's thumbnail host (DllGetClassObject -> IInitializeWithStream
             -> GetThumbnail), with the shipped defaults unless --preset is given. Nothing is registered.
  * windows  Windows' in-box Photo Thumbnail Provider {C7657C4A-...} created with CoCreateInstance and driven the same
             way. It is the handler Explorer uses for .jpg on a stock install. Nothing is registered or cached.

Reports per-file median / mean and the time for the whole folder (sequential, like one thumbnail-host thread).
The Explorer *tab* grid is measured by the Glide.Core.Tests benchmark `ExplorerThumbnailSpeedBenchmark` (opt-in,
GLIDE_THUMB_BENCH_DIR=<corpus>), which uses the same corpus.

Usage:  python -I tools/thumbnail-speed-benchmark.py --dll native/Glide.ShellThumbnail/build/Release/Glide.ShellThumbnail.dll
            [--corpus <dir>] [--count 200] [--edge 256] [--preset default|0,0,1 (quality,maxSource,embedded)]
"""

from __future__ import annotations

import argparse
import ctypes
import os
import shutil
import statistics
import struct
import sys
import time

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import importlib.util  # noqa: E402

_spec = importlib.util.spec_from_file_location(
    "preset_bench", os.path.join(os.path.dirname(os.path.abspath(__file__)), "thumbnail-preset-benchmark.py"))
pb = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(pb)  # type: ignore[union-attr]

CLSID_WINDOWS_PHOTO = pb.GUID.parse("{C7657C4A-9F68-40fa-A4DF-96BC08EB3551}")


def camera_image(seed: int) -> Image.Image:
    rng = np.random.default_rng(seed)
    w, h = 6000, 4000
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    base = np.stack([
        128 + 90 * np.sin(x / (700 + seed * 37) + seed),
        128 + 80 * np.cos(y / (500 + seed * 23)),
        128 + 70 * np.sin((x + y) / (900 + seed * 11)),
    ], axis=-1)
    base += rng.normal(0, 9, size=base.shape).astype(np.float32)  # sensor noise: realistic entropy
    return Image.fromarray(base.clip(0, 255).astype(np.uint8))


def with_mpf(main: bytes, preview: bytes) -> bytes:
    """Camera layout: APP2 MPF index right after SOI, the 1620x1080 preview appended after the main image's EOI."""
    def app2(prev_off: int, prev_len: int) -> bytes:
        entries = struct.pack("<IIIHH", 0x030000, 0, 0, 0, 0) + struct.pack("<IIIHH", 0x010002, prev_len, prev_off, 0, 0)
        ifd = struct.pack("<H", 3) + struct.pack("<HHI4s", 0xB000, 7, 4, b"0100") + struct.pack("<HHII", 0xB001, 4, 1, 2)
        ifd += struct.pack("<HHII", 0xB002, 7, 32, 8 + 2 + 3 * 12 + 4) + struct.pack("<I", 0)
        payload = b"MPF\x00" + b"II*\x00" + struct.pack("<I", 8) + ifd + entries
        return b"\xff\xe2" + struct.pack(">H", len(payload) + 2) + payload
    off = len(main) + len(app2(0, 0)) - (2 + 8)
    return main[:2] + app2(off, len(preview)) + main[2:] + preview


def make_corpus(directory: str, count: int, mpf: bool = False) -> list[str]:
    os.makedirs(directory, exist_ok=True)
    files = [os.path.join(directory, f"IMG_{i:04d}.JPG") for i in range(count)]
    if all(os.path.exists(f) for f in files):
        return files
    uniques = []
    for seed in range(min(8, count)):
        path = files[seed]
        if not os.path.exists(path):
            image = camera_image(seed)
            pb.write_exif_jpeg(path, image, 160, 1, tint_thumbnail=False)
            if mpf:
                with open(path, "rb") as handle:
                    main = handle.read()
                with open(path, "wb") as handle:
                    handle.write(with_mpf(main, pb.jpeg_bytes(image.resize((1620, 1080), Image.LANCZOS), 90)))
        uniques.append(path)
    for i in range(len(uniques), count):
        if not os.path.exists(files[i]):
            shutil.copyfile(uniques[i % len(uniques)], files[i])
    return files


class WindowsProvider(pb.Provider):
    def __init__(self) -> None:  # noqa: D401 - reuse the stream/DIB plumbing, swap the class factory
        ctypes.oledll.ole32.CoInitializeEx(None, 2)
        self.factory = ctypes.c_void_p()
        co_get = ctypes.oledll.ole32.CoGetClassObject
        co_get(ctypes.byref(CLSID_WINDOWS_PHOTO), 1, None, ctypes.byref(pb.IID_ICLASSFACTORY), ctypes.byref(self.factory))
        self.overrides = None
        self.last_diag = None
        shlwapi = ctypes.windll.shlwapi
        self.open_stream = shlwapi.SHCreateStreamOnFileEx
        self.open_stream.restype = pb.HRESULT
        self.open_stream.argtypes = [pb.wt.LPCWSTR, pb.wt.DWORD, pb.wt.DWORD, pb.wt.BOOL, ctypes.c_void_p,
                                     ctypes.POINTER(ctypes.c_void_p)]
        self.gdi = ctypes.windll.gdi32
        self.gdi.GetObjectW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
        self.gdi.DeleteObject.argtypes = [ctypes.c_void_p]


def run(provider, files: list[str], edge: int) -> dict:
    provider.thumbnail(files[0], edge)  # warm-up (DLL init, WIC component load)
    times, embedded, decoded, decoders = [], 0, set(), set()
    started = time.perf_counter()
    for f in files:
        _, ms, diag = provider.thumbnail(f, edge)
        times.append(ms)
        if diag.get("embedded"):
            embedded += 1
        if diag.get("decoder"):
            decoders.add(diag["decoder"])
        if diag.get("decoded"):
            decoded.add(diag["decoded"])
    total = (time.perf_counter() - started) * 1000
    return {"median": statistics.median(times), "mean": statistics.fmean(times), "total": total,
            "embedded": embedded, "decoded": sorted(decoded), "decoders": sorted(decoders)}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dll", required=True)
    ap.add_argument("--corpus", default=os.path.join(os.environ.get("TEMP", "."), "glide-thumb-bench"))
    ap.add_argument("--count", type=int, default=200)
    ap.add_argument("--edge", type=int, nargs="+", default=[96, 256])
    ap.add_argument("--preset", default="default")
    ap.add_argument("--mpf", action="store_true", help="corpus files also carry a 1620x1080 MPF preview (Canon/Nikon/Sony layout)")
    args = ap.parse_args()
    corpus = args.corpus + ("-mpf" if args.mpf else "")
    files = make_corpus(corpus, args.count, args.mpf)
    print(f"corpus: {corpus} ({len(files)} x 6000x4000 JPEG, {os.path.getsize(files[0]) / 1e6:.1f} MB, 160x120 Exif thumb)")
    glide = pb.Provider(args.dll)
    if args.preset != "default":
        q, m, e = (int(v) for v in args.preset.split(","))
        glide.configure(q, m, bool(e))
    windows = WindowsProvider()
    for edge in args.edge:
        for name, provider in (("glide", glide), ("windows", windows)):
            r = run(provider, files, edge)
            print(f"{name:8s} edge {edge:4d}: median {r['median']:7.2f} ms  mean {r['mean']:7.2f} ms  "
                  f"folder {r['total'] / 1000:6.2f} s  embedded {r['embedded']}/{len(files)}  decoded {r["decoded"]} {r["decoders"]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
