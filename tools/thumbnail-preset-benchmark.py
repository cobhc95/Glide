"""Headless benchmark for the Glide Explorer thumbnail provider presets.

Drives Glide.ShellThumbnail.dll exactly the way Explorer's thumbnail host does
(DllGetClassObject -> IClassFactory -> IInitializeWithStream -> IThumbnailProvider::GetThumbnail)
but entirely in-process: nothing is registered, Explorer is never touched, no window is shown and
the Windows thumbnail cache is never read or cleared.

For every generated sample and every preset it records the per-item time (Initialize +
GetThumbnail, median of N runs) and the output quality against a Lanczos reference rendered by
Pillow from the full-resolution source (mean absolute error, 0..255; lower is closer).

Presets are applied with the provider's in-process test override export
(GlideThumbnailSetTestOverrides). A provider build without that export ignores presets; the
script then labels the run "baseline" and still measures it, which is how the before/after table in
the 4.8.0 notes was produced.

Usage (from the repository root):
    python -I tools/thumbnail-preset-benchmark.py --dll native/Glide.ShellThumbnail/build/Release/Glide.ShellThumbnail.dll
           [--corpus <dir>] [--runs 5] [--edge 256] [--json out.json]

Requires Python 3.10+, Pillow and NumPy (only for corpus generation and the reference metric).
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import io
import json
import os
import struct
import statistics
import sys
import time

import numpy as np
from PIL import Image, ImageOps

# ----------------------------------------------------------------------------- corpus -----------


def zone_plate(width: int, height: int, scale: float = 1.0) -> np.ndarray:
    """High-frequency concentric rings: aliasing and filter quality show up clearly."""
    y, x = np.mgrid[0:height, 0:width].astype(np.float32)
    cx, cy = width / 2, height / 2
    r2 = ((x - cx) ** 2 + (y - cy) ** 2) / (max(width, height) * scale)
    rings = (np.sin(r2 * 0.9) * 0.5 + 0.5) * 255
    red = rings
    green = (x / width) * 255
    blue = (y / height) * 255
    return np.stack([red, green, blue], axis=-1).clip(0, 255).astype(np.uint8)


def exif_with_thumbnail(thumbnail_jpeg: bytes, orientation: int = 1) -> bytes:
    """Builds an APP1 Exif segment with IFD0 Orientation and an IFD1 JPEG thumbnail."""
    tiff = bytearray(b"II*\x00" + struct.pack("<I", 8))
    # IFD0: 1 entry (Orientation), next IFD -> IFD1.
    ifd0_offset = 8
    ifd1_offset = ifd0_offset + 2 + 12 + 4
    tiff += struct.pack("<H", 1)
    tiff += struct.pack("<HHIHH", 0x0112, 3, 1, orientation, 0)
    tiff += struct.pack("<I", ifd1_offset)
    # IFD1: Compression=6, JPEGInterchangeFormat, JPEGInterchangeFormatLength.
    thumb_offset = ifd1_offset + 2 + 3 * 12 + 4
    tiff += struct.pack("<H", 3)
    tiff += struct.pack("<HHIHH", 0x0103, 3, 1, 6, 0)
    tiff += struct.pack("<HHII", 0x0201, 4, 1, thumb_offset)
    tiff += struct.pack("<HHII", 0x0202, 4, 1, len(thumbnail_jpeg))
    tiff += struct.pack("<I", 0)
    tiff += thumbnail_jpeg
    payload = b"Exif\x00\x00" + bytes(tiff)
    return b"\xff\xe1" + struct.pack(">H", len(payload) + 2) + payload


def jpeg_bytes(image: Image.Image, quality: int = 90) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, "JPEG", quality=quality)
    return buffer.getvalue()


def strip_app0(jpeg: bytes) -> bytes:
    # Pillow writes SOI + APP0(JFIF). Camera files carry Exif APP1 first instead.
    if jpeg[2:4] == b"\xff\xe0":
        length = struct.unpack(">H", jpeg[4:6])[0]
        return jpeg[:2] + jpeg[4 + length:]
    return jpeg


def write_exif_jpeg(path: str, image: Image.Image, thumb_edge: int, orientation: int = 1,
                    tint_thumbnail: bool = True) -> None:
    thumb = image.copy()
    thumb.thumbnail((thumb_edge, thumb_edge), Image.LANCZOS)
    if tint_thumbnail:
        # Mark the embedded preview so the benchmark can tell when it was used.
        arr = np.asarray(thumb).astype(np.int16)
        arr[..., 1] = (arr[..., 1] * 0.25).astype(np.int16)
        thumb = Image.fromarray(arr.clip(0, 255).astype(np.uint8))
    main = strip_app0(jpeg_bytes(image))
    app1 = exif_with_thumbnail(jpeg_bytes(thumb, 85), orientation)
    with open(path, "wb") as handle:
        handle.write(main[:2] + app1 + main[2:])


def lzw_literal_stream(indices: bytes) -> bytes:
    """GIF LZW stream using only literal codes (valid, simple, deliberately uncompressed)."""
    clear, eoi, width = 256, 257, 9
    out = bytearray()
    bit_buffer = 0
    bit_count = 0

    def emit(code: int) -> None:
        nonlocal bit_buffer, bit_count
        bit_buffer |= code << bit_count
        bit_count += width
        while bit_count >= 8:
            out.append(bit_buffer & 0xFF)
            bit_buffer >>= 8
            bit_count -= 8

    emit(clear)
    run = 0
    for value in indices:
        if run == 250:
            emit(clear)
            run = 0
        emit(value)
        run += 1
    emit(eoi)
    if bit_count:
        out.append(bit_buffer & 0xFF)
    return bytes(out)


def write_gif(path: str, width: int, height: int, palette: list[tuple[int, int, int]],
              frames: list[dict]) -> None:
    """Minimal GIF89a writer. Each frame: left, top, w, h, indices (bytes), transparent, disposal."""
    pal = list(palette) + [(0, 0, 0)] * (256 - len(palette))
    data = bytearray(b"GIF89a")
    data += struct.pack("<HHBBB", width, height, 0xF7, 0, 0)
    for r, g, b in pal:
        data += bytes((r, g, b))
    data += b"\x21\xff\x0bNETSCAPE2.0\x03\x01\x00\x00\x00"
    for frame in frames:
        transparent = frame.get("transparent")
        packed = (frame.get("disposal", 1) << 2) | (1 if transparent is not None else 0)
        data += b"\x21\xf9\x04" + struct.pack("<BHB", packed, frame.get("delay", 10), transparent or 0) + b"\x00"
        data += b"\x2c" + struct.pack("<HHHHB", frame["left"], frame["top"], frame["w"], frame["h"], 0)
        data += b"\x08"
        stream = lzw_literal_stream(frame["indices"])
        for start in range(0, len(stream), 255):
            block = stream[start:start + 255]
            data += bytes((len(block),)) + block
        data += b"\x00"
    data += b"\x3b"
    with open(path, "wb") as handle:
        handle.write(data)


def generate_corpus(directory: str) -> list[str]:
    os.makedirs(directory, exist_ok=True)
    made: list[str] = []

    def target(name: str) -> str | None:
        path = os.path.join(directory, name)
        made.append(path)
        return None if os.path.exists(path) else path

    if p := target("jpeg-small-exif.jpg"):
        write_exif_jpeg(p, Image.fromarray(zone_plate(800, 600)), 160)
    if p := target("jpeg-large-exif160.jpg"):
        write_exif_jpeg(p, Image.fromarray(zone_plate(6000, 4000)), 160)
    if p := target("jpeg-large-exif320.jpg"):
        write_exif_jpeg(p, Image.fromarray(zone_plate(6000, 4000)), 320)
    if p := target("jpeg-large-plain.jpg"):
        Image.fromarray(zone_plate(6000, 4000)).save(p, "JPEG", quality=90)
    if p := target("jpeg-orient6-exif.jpg"):
        write_exif_jpeg(p, Image.fromarray(zone_plate(1600, 1200)), 320, orientation=6)
    if p := target("png-alpha.png"):
        rgb = zone_plate(1024, 1024)
        y, x = np.mgrid[0:1024, 0:1024]
        alpha = (((x // 128 + y // 128) % 2) * 255).astype(np.uint8)
        Image.fromarray(np.dstack([rgb, alpha]), "RGBA").save(p)
    if p := target("png-big.png"):
        Image.fromarray(zone_plate(8000, 6000)).save(p, compress_level=1)
    if p := target("gif-static.gif"):
        Image.fromarray(zone_plate(800, 600)).quantize(256).save(p)
    if p := target("gif-anim-pillow.gif"):
        base = zone_plate(1200, 900)
        frames = [Image.fromarray(np.roll(base, shift * 40, axis=1)).quantize(64) for shift in range(24)]
        frames[0].save(p, save_all=True, append_images=frames[1:], duration=40, loop=0)
    if p := target("gif-anim-offset-first.gif"):
        # Frame 0 covers only the centre of a 400x300 canvas; the rest is transparent.
        palette = [(220, 30, 30), (30, 30, 220), (0, 0, 0)]
        frames = [
            {"left": 100, "top": 75, "w": 200, "h": 150, "indices": bytes([0]) * (200 * 150), "transparent": 2},
            {"left": 0, "top": 0, "w": 400, "h": 300, "indices": bytes([1]) * (400 * 300), "transparent": 2},
        ]
        write_gif(p, 400, 300, palette, frames)
    if p := target("gif-anim-blank-first.gif"):
        # Frame 0 is fully transparent (a common "fade in" animation); frame 1 has the content.
        palette = [(30, 160, 30), (0, 0, 0)]
        frames = [
            {"left": 0, "top": 0, "w": 320, "h": 240, "indices": bytes([1]) * (320 * 240), "transparent": 1},
            {"left": 40, "top": 30, "w": 240, "h": 180, "indices": bytes([0]) * (240 * 180), "transparent": 1},
        ]
        write_gif(p, 320, 240, palette, frames)
    if p := target("gif-anim-large.gif"):
        # ~80 MB animated GIF: the provider should not need to read all of it for frame 0.
        rng = np.random.default_rng(7)
        palette = [tuple(int(v) for v in rng.integers(0, 256, 3)) for _ in range(256)]
        frames = []
        for index in range(36):
            plane = ((zone_plate(1600, 1200)[..., 0].astype(np.uint16) + index * 7) % 256).astype(np.uint8)
            frames.append({"left": 0, "top": 0, "w": 1600, "h": 1200, "indices": plane.tobytes()})
        write_gif(p, 1600, 1200, palette, frames)
    if p := target("webp-lossy.webp"):
        Image.fromarray(zone_plate(4000, 3000)).save(p, "WEBP", quality=85)
    if p := target("webp-alpha.webp"):
        rgb = zone_plate(1024, 768)
        alpha = np.full((768, 1024), 255, np.uint8)
        alpha[:, :512] = 0
        Image.fromarray(np.dstack([rgb, alpha]), "RGBA").save(p, "WEBP", lossless=True)
    if p := target("bmp-large.bmp"):
        Image.fromarray(zone_plate(4000, 3000)).save(p)
    if p := target("tiff-large.tif"):
        Image.fromarray(zone_plate(4000, 3000)).save(p, compression="tiff_lzw")
    if p := target("tga-large.tga"):
        Image.fromarray(zone_plate(3000, 2000)).save(p)
    if p := target("ppm-large.ppm"):
        Image.fromarray(zone_plate(3000, 2000)).save(p)
    return made


# ----------------------------------------------------------------------- reference -------------


def reference_thumbnail(path: str, edge: int) -> np.ndarray:
    image = Image.open(path)
    image.seek(0)
    image = ImageOps.exif_transpose(image).convert("RGBA")
    ratio = min(edge / image.width, edge / image.height)
    size = (max(1, int(image.width * ratio)), max(1, int(image.height * ratio)))
    image = image.resize(size, Image.LANCZOS)
    white = Image.new("RGBA", image.size, (255, 255, 255, 255))
    return np.asarray(Image.alpha_composite(white, image).convert("RGB")).astype(np.int16)


# --------------------------------------------------------------------------- COM ---------------


class GUID(ctypes.Structure):
    _fields_ = [("Data1", wt.DWORD), ("Data2", wt.WORD), ("Data3", wt.WORD), ("Data4", ctypes.c_ubyte * 8)]

    @staticmethod
    def parse(text: str) -> "GUID":
        guid = GUID()
        ctypes.oledll.ole32.CLSIDFromString(ctypes.c_wchar_p(text), ctypes.byref(guid))
        return guid


CLSID_PROVIDER = GUID.parse("{6E3C1B2A-9F41-4E7C-9B1E-2C7A5D8F0A31}")
IID_ICLASSFACTORY = GUID.parse("{00000001-0000-0000-C000-000000000046}")
IID_IINIT_STREAM = GUID.parse("{B824B49D-22AC-4161-AC8A-9916E8FA3F7F}")
IID_ITHUMBNAIL = GUID.parse("{E357FCCD-A995-4576-B01F-234630154E96}")

HRESULT = ctypes.c_long


def vcall(obj: ctypes.c_void_p, index: int, restype, *args_types):
    vtable = ctypes.cast(obj, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    prototype = ctypes.WINFUNCTYPE(restype, ctypes.c_void_p, *args_types)
    return prototype(vtable[index])


def release(obj: ctypes.c_void_p) -> None:
    if obj:
        vcall(obj, 2, wt.ULONG)(obj)


def query(obj: ctypes.c_void_p, iid: GUID) -> ctypes.c_void_p:
    out = ctypes.c_void_p()
    hr = vcall(obj, 0, HRESULT, ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))(obj, ctypes.byref(iid), ctypes.byref(out))
    if hr != 0:
        raise OSError(f"QueryInterface failed 0x{hr & 0xFFFFFFFF:08X}")
    return out


class BITMAP(ctypes.Structure):
    _fields_ = [("bmType", wt.LONG), ("bmWidth", wt.LONG), ("bmHeight", wt.LONG), ("bmWidthBytes", wt.LONG),
                ("bmPlanes", wt.WORD), ("bmBitsPixel", wt.WORD), ("bmBits", ctypes.c_void_p)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG), ("biPlanes", wt.WORD),
                ("biBitCount", wt.WORD), ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD),
                ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD),
                ("biClrImportant", wt.DWORD)]


class DIBSECTION(ctypes.Structure):
    _fields_ = [("dsBm", BITMAP), ("dsBmih", BITMAPINFOHEADER), ("dsBitfields", wt.DWORD * 3),
                ("dshSection", wt.HANDLE), ("dsOffset", wt.DWORD)]


class Diagnostics(ctypes.Structure):
    _fields_ = [("size", ctypes.c_uint32), ("quality", ctypes.c_int32), ("embedded", ctypes.c_int32),
                ("sourceWidth", ctypes.c_uint32), ("sourceHeight", ctypes.c_uint32),
                ("decodedWidth", ctypes.c_uint32), ("decodedHeight", ctypes.c_uint32),
                ("outputWidth", ctypes.c_uint32), ("outputHeight", ctypes.c_uint32),
                ("decodeMicros", ctypes.c_uint64), ("totalMicros", ctypes.c_uint64),
                ("bytesRead", ctypes.c_uint64), ("decoder", ctypes.c_char * 16)]


class Provider:
    def __init__(self, dll_path: str) -> None:
        ctypes.oledll.ole32.CoInitializeEx(None, 2)
        self.dll = ctypes.WinDLL(os.path.abspath(dll_path))
        get_class = self.dll.DllGetClassObject
        get_class.restype = HRESULT
        get_class.argtypes = [ctypes.POINTER(GUID), ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p)]
        self.factory = ctypes.c_void_p()
        hr = get_class(ctypes.byref(CLSID_PROVIDER), ctypes.byref(IID_ICLASSFACTORY), ctypes.byref(self.factory))
        if hr != 0:
            raise OSError(f"DllGetClassObject failed 0x{hr & 0xFFFFFFFF:08X}")
        self.overrides = getattr(self.dll, "GlideThumbnailSetTestOverrides", None)
        if self.overrides is not None:
            self.overrides.restype = None
            self.overrides.argtypes = [ctypes.c_int, ctypes.c_uint, ctypes.c_int]
        self.last_diag = getattr(self.dll, "GlideThumbnailGetLastDiagnostics", None)
        if self.last_diag is not None:
            self.last_diag.restype = ctypes.c_int
            self.last_diag.argtypes = [ctypes.POINTER(Diagnostics)]
        shlwapi = ctypes.windll.shlwapi
        self.open_stream = shlwapi.SHCreateStreamOnFileEx
        self.open_stream.restype = HRESULT
        self.open_stream.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, wt.BOOL, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
        self.gdi = ctypes.windll.gdi32
        self.gdi.GetObjectW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
        self.gdi.DeleteObject.argtypes = [ctypes.c_void_p]

    @property
    def wired(self) -> bool:
        return self.overrides is not None

    def configure(self, quality: int, max_source: int, prefer_embedded: bool) -> None:
        if self.overrides is not None:
            self.overrides(quality, max_source, 1 if prefer_embedded else 0)

    def thumbnail(self, path: str, edge: int) -> tuple[np.ndarray | None, float, dict]:
        started = time.perf_counter()
        instance = ctypes.c_void_p()
        create = vcall(self.factory, 3, HRESULT, ctypes.c_void_p, ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))
        hr = create(self.factory, None, ctypes.byref(IID_ITHUMBNAIL), ctypes.byref(instance))
        if hr != 0:
            raise OSError(f"CreateInstance failed 0x{hr & 0xFFFFFFFF:08X}")
        stream = ctypes.c_void_p()
        init = None
        bitmap = ctypes.c_void_p()
        pixels = None
        try:
            hr = self.open_stream(path, 0x20, 0, False, None, ctypes.byref(stream))  # STGM_READ|SHARE_DENY_WRITE
            if hr != 0:
                raise OSError(f"SHCreateStreamOnFileEx failed 0x{hr & 0xFFFFFFFF:08X}")
            init = query(instance, IID_IINIT_STREAM)
            hr = vcall(init, 3, HRESULT, ctypes.c_void_p, wt.DWORD)(init, stream, 0)
            if hr == 0:
                alpha = wt.DWORD()
                hr = vcall(instance, 3, HRESULT, wt.UINT, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wt.DWORD))(
                    instance, edge, ctypes.byref(bitmap), ctypes.byref(alpha))
            elapsed = (time.perf_counter() - started) * 1000.0
            if hr == 0 and bitmap:
                section = DIBSECTION()
                self.gdi.GetObjectW(bitmap, ctypes.sizeof(section), ctypes.byref(section))
                info = section.dsBm
                height = abs(info.bmHeight)
                raw = ctypes.string_at(info.bmBits, info.bmWidthBytes * height)
                bgra = np.frombuffer(raw, np.uint8).reshape(height, info.bmWidthBytes // 4, 4)[:, :info.bmWidth]
                # The provider always creates a top-down DIB section (negative biHeight); GetObject
                # reports the height as positive, so the bits are already in top-down row order.
                pixels = bgra[..., [2, 1, 0]].astype(np.int16)
        finally:
            if bitmap:
                self.gdi.DeleteObject(bitmap)
            release(init)
            release(stream)
            release(instance)
        diag: dict = {}
        if self.last_diag is not None:
            d = Diagnostics()
            d.size = ctypes.sizeof(d)
            if self.last_diag(ctypes.byref(d)):
                diag = {"decoder": d.decoder.decode(), "embedded": bool(d.embedded),
                        "source": f"{d.sourceWidth}x{d.sourceHeight}",
                        "decoded": f"{d.decodedWidth}x{d.decodedHeight}", "bytesRead": d.bytesRead}
        return pixels, elapsed, diag


def error_against(reference: np.ndarray, output: np.ndarray | None) -> float | None:
    if output is None:
        return None
    if output.shape != reference.shape:
        resized = Image.fromarray(output.clip(0, 255).astype(np.uint8)).resize(
            (reference.shape[1], reference.shape[0]), Image.BILINEAR)
        output = np.asarray(resized).astype(np.int16)
    return float(np.abs(output - reference).mean())


PRESETS = [("Fast", 0), ("Balanced", 1), ("High", 2)]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dll", required=True)
    parser.add_argument("--corpus", default=os.path.join("artifacts", "thumbnail-benchmark", "presets"))
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--edge", type=int, default=256)
    parser.add_argument("--max-source", type=int, default=0)
    parser.add_argument("--no-embedded", action="store_true")
    parser.add_argument("--only", default="")
    parser.add_argument("--json", default="")
    parser.add_argument("--dump", default="", help="directory to write each output and its reference as PNG")
    args = parser.parse_args()

    files = generate_corpus(args.corpus)
    if args.only:
        files = [f for f in files if args.only in os.path.basename(f)]
    provider = Provider(args.dll)
    label = "wired" if provider.wired else "baseline (presets ignored by this provider build)"
    print(f"provider: {args.dll}  [{label}]  edge={args.edge}  maxSource={args.max_source}  "
          f"preferEmbedded={not args.no_embedded}  runs={args.runs}")
    print(f"{'file':28} {'MB':>6} {'preset':9} {'median ms':>10} {'min ms':>8} {'out':>9} {'err':>6}  path")
    results = []
    for path in files:
        reference = reference_thumbnail(path, args.edge)
        size_mb = os.path.getsize(path) / (1024 * 1024)
        for name, quality in PRESETS:
            provider.configure(quality, args.max_source, not args.no_embedded)
            provider.thumbnail(path, args.edge)  # warm-up (file cache, WIC component load)
            times = []
            output = None
            diag = {}
            for _ in range(args.runs):
                output, elapsed, diag = provider.thumbnail(path, args.edge)
                times.append(elapsed)
            err = error_against(reference, output)
            if args.dump:
                os.makedirs(args.dump, exist_ok=True)
                stem = os.path.splitext(os.path.basename(path))[0]
                Image.fromarray(reference.astype(np.uint8)).save(os.path.join(args.dump, f"{stem}.reference.png"))
                if output is not None:
                    Image.fromarray(output.clip(0, 255).astype(np.uint8)).save(os.path.join(args.dump, f"{stem}.{name}.png"))
            shape = f"{output.shape[1]}x{output.shape[0]}" if output is not None else "FAIL"
            detail = " ".join(f"{k}={v}" for k, v in diag.items())
            print(f"{os.path.basename(path):28} {size_mb:6.1f} {name:9} {statistics.median(times):10.2f} "
                  f"{min(times):8.2f} {shape:>9} {err if err is not None else float('nan'):6.2f}  {detail}")
            results.append({"file": os.path.basename(path), "preset": name, "medianMs": statistics.median(times),
                            "minMs": min(times), "output": shape, "error": err, **diag})
    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump({"dll": args.dll, "wired": provider.wired, "edge": args.edge, "results": results}, handle, indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
