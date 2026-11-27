"""Generates the all-format speed audit fixtures (5.2.0): synthetic, deterministic, never downloaded, never written to a photo library.

Usage:  python tools/perf/generate_format_fixtures.py <output-dir> [--force] [--only <substring>]

Writes <output-dir>/single/*, the layout `Glide.PerfBench thumb|decode` and `Glide.FormatAudit` read. It first runs
generate_thumb_fixtures.py (JPEG baseline / progressive / camera MPF, PNG, WebP, TIFF LZW / raw, BMP, GIF, HEIC, AVIF, ICO) and then adds the variants:

  JPEG   gray, 4:4:4, CMYK, ICC profile, EXIF-thumbnail-only camera file, MPO stereo pair
  PNG    gray, 8-bit palette, 16-bit, Adam7 interlaced, APNG
  WebP   lossless, lossless + alpha, animated
  GIF    animated; BMP 8-bit palette, 32-bit alpha; CUR
  TIFF   deflate (strips 4k and 24 MP), tiled 4k, PackBits, JPEG-in-TIFF, tiled 24 MP, 3-level SubIFD pyramid 24 MP, BigTIFF, 16-bit, float32, gray, CMYK, multi-page
  AVIF / HEIC at 12 MP and with alpha; JPEG XL (lossy, lossless, alpha, 12 MP); JPEG 2000 (jp2, j2k); JPEG XR (WPF encoder)
  Photoshop PSD (raw, RLE, ZIP-with-prediction, 16-bit, layered, with and without the embedded 1036 thumbnail)
  OpenEXR (none / RLE / ZIPS / ZIP, half and float), Radiance HDR, PFM
  SVG / SVGZ (simple, 5000-element, embedded raster), TGA (raw, RLE), PCX, PPM, QOI, SGI
  DDS (RGBA, DXT1, DXT5, DXT1 with a mip chain), KTX 1 (RGBA8), FITS, NIfTI, OpenRaster
  Camera RAW containers (synthetic, with ~25 MB of filler "sensor data" so the file reads like a RAW): DNG, NEF, ARW, RAF, CR3.
  They carry a real embedded JPEG preview; a real RAW with a lossless-JPEG raw plane would read the same bytes.

Not generated (no writer on the audit PC, listed in the audit): 12-bit JPEG, DICOM, XCF, PDN, KRA, KTX2, PVR, ASTC, PIZ / B44 / DWA EXR, tiled EXR,
real camera files. Needs: numpy, Pillow, tifffile, opencv-python, pillow_heif (optional) and ffmpeg on PATH (EXR, JPEG XL, APNG).
"""
from __future__ import annotations

import gzip
import io
import os
import shutil
import struct
import subprocess
import sys
import zipfile
import zlib

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from PIL import Image  # noqa: E402
import generate_perf_data as perf  # noqa: E402

FFMPEG = shutil.which("ffmpeg")


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    out = os.path.abspath(sys.argv[1])
    force = "--force" in sys.argv
    only = sys.argv[sys.argv.index("--only") + 1] if "--only" in sys.argv else None
    single = os.path.join(out, "single")
    os.makedirs(single, exist_ok=True)
    base = [sys.executable, os.path.join(HERE, "generate_thumb_fixtures.py"), out] + (["--force"] if force else [])
    if only is None:
        subprocess.run(base, check=True, stdout=subprocess.DEVNULL)

    def target(name):
        if only is not None and only not in name:
            return None
        path = os.path.join(single, name)
        return None if os.path.exists(path) and not force else path

    cache: dict = {}

    def photo(size_key, seed=21, alpha=False) -> Image.Image:
        key = (size_key, seed, alpha)
        if key not in cache:
            cache.clear()
            w, h = perf.SIZES[size_key]
            cache[key] = perf.make_photo(w, h, seed, alpha=alpha)
        return cache[key]

    def arr(size_key, seed=21):
        return np.asarray(photo(size_key, seed))

    built = []

    def done(p):
        built.append(p)
        print("  ", os.path.basename(p), f"{os.path.getsize(p) / 1e6:.2f} MB", flush=True)

    # ---------------------------------------------------------------- JPEG variants
    if p := target("jpeg-gray-4k.jpg"):
        photo("4k").convert("L").save(p, "JPEG", quality=90); done(p)
    if p := target("jpeg-444-4k.jpg"):
        photo("4k").save(p, "JPEG", quality=92, subsampling=0); done(p)
    if p := target("jpeg-cmyk-4k.jpg"):
        photo("4k").convert("CMYK").save(p, "JPEG", quality=90); done(p)
    if p := target("jpeg-icc-4k.jpg"):
        from PIL import ImageCms
        icc = ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()
        photo("4k").save(p, "JPEG", quality=90, subsampling=2, icc_profile=icc); done(p)
    if p := target("jpeg-exif-thumb-24mp.jpg"):
        image = photo("24mp", 11)
        thumb = image.copy(); thumb.thumbnail((160, 120))
        exif = perf.exif_with_orientation_and_thumbnail(6, thumb)
        image.save(p, "JPEG", quality=92, subsampling=2, exif=exif); done(p)
    if p := target("mpo-12mp.mpo"):
        left = photo("12mp", 11)
        right = left.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
        left.save(p, "MPO", save_all=True, append_images=[right], quality=90); done(p)

    # ---------------------------------------------------------------- PNG variants
    if p := target("png-gray-4k.png"):
        photo("4k").convert("L").save(p, "PNG", compress_level=6); done(p)
    if p := target("png-palette-4k.png"):
        photo("4k").quantize(colors=256, method=Image.Quantize.FASTOCTREE).save(p, "PNG", compress_level=6); done(p)
    if p := target("png-16bit-4k.png"):
        a16 = (arr("4k").astype(np.uint16) << 8) | arr("4k").astype(np.uint16)
        write_png(p, a16, interlace=False); done(p)
    if p := target("png-interlaced-4k.png"):
        write_png(p, arr("4k"), interlace=True); done(p)
    if p := target("png-up-filter-4k.png"):  # same writer as the two above, so the interlace / 16-bit rows compare like for like
        write_png(p, arr("4k"), interlace=False); done(p)
    if p := target("apng-720p.png") if FFMPEG else None:
        ffmpeg(["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=10", "-frames:v", "20", "-plays", "0", "-f", "apng", p]); done(p)

    # ---------------------------------------------------------------- WebP / GIF / BMP / CUR
    if p := target("webp-lossless-4k.webp"):
        photo("4k").save(p, "WEBP", lossless=True, quality=60, method=2); done(p)
    if p := target("webp-lossless-alpha-4k.webp"):
        photo("4k", 31, alpha=True).save(p, "WEBP", lossless=True, quality=60, method=2); done(p)
    if p := target("webp-anim-720p.webp"):
        frames = anim_frames(1280, 720, 12)
        frames[0].save(p, "WEBP", save_all=True, append_images=frames[1:], duration=100, loop=0, quality=80); done(p)
    if p := target("gif-anim-720p.gif"):
        frames = [f.quantize(colors=128, method=Image.Quantize.FASTOCTREE) for f in anim_frames(1280, 720, 12)]
        frames[0].save(p, "GIF", save_all=True, append_images=frames[1:], duration=100, loop=0); done(p)
    if p := target("bmp-8bit-4k.bmp"):
        photo("4k").quantize(colors=256, method=Image.Quantize.FASTOCTREE).save(p, "BMP"); done(p)
    if p := target("bmp-32alpha-4k.bmp"):
        photo("4k", 31, alpha=True).save(p, "BMP"); done(p)
    if p := target("cur-256.cur"):
        buf = io.BytesIO()
        photo("small", 41).resize((256, 256)).save(buf, "ICO", sizes=[(256, 256), (32, 32)])
        data = bytearray(buf.getvalue()); struct.pack_into("<H", data, 2, 2)  # type 2 = cursor
        for i in range(struct.unpack_from("<H", data, 4)[0]):
            struct.pack_into("<HH", data, 6 + 16 * i + 4, 4, 4)  # hotspot
        open(p, "wb").write(data); done(p)

    # ---------------------------------------------------------------- TIFF variants
    import tifffile
    a4 = None

    def a4k():
        return arr("4k")

    if p := target("tiff-deflate-4k.tif"):
        tifffile.imwrite(p, a4k(), photometric="rgb", compression="zlib", predictor=False, rowsperstrip=64); done(p)
    if p := target("tiff-packbits-4k.tif"):
        photo("4k").save(p, "TIFF", compression="packbits"); done(p)
    if p := target("tiff-jpeg-4k.tif"):
        photo("4k").save(p, "TIFF", compression="jpeg", quality=90); done(p)
    if p := target("tiff-gray-4k.tif"):
        photo("4k").convert("L").save(p, "TIFF", compression="tiff_lzw"); done(p)
    if p := target("tiff-cmyk-4k.tif"):
        photo("4k").convert("CMYK").save(p, "TIFF", compression="raw"); done(p)
    if p := target("tiff-16bit-deflate-4k.tif"):
        tifffile.imwrite(p, (a4k().astype(np.uint16) << 8) | a4k().astype(np.uint16), photometric="rgb", compression="zlib", rowsperstrip=64); done(p)
    if p := target("tiff-float32-4k.tif"):
        tifffile.imwrite(p, a4k().astype(np.float32) / 255.0, photometric="rgb", compression="zlib", rowsperstrip=64); done(p)
    if p := target("tiff-multipage-1080p.tif"):
        frames = anim_frames(1920, 1080, 5)
        frames[0].save(p, "TIFF", save_all=True, append_images=frames[1:], compression="tiff_lzw"); done(p)
    if p := target("tiff-deflate-24mp.tif"):  # strips, same codec as the tiled / BigTIFF / pyramid 24 MP files: separates "tiled" from "deflate"
        tifffile.imwrite(p, arr("24mp"), photometric="rgb", compression="zlib", rowsperstrip=64); done(p)
    if p := target("tiff-tiled-deflate-4k.tif"):
        tifffile.imwrite(p, a4k(), photometric="rgb", compression="zlib", tile=(256, 256)); done(p)
    if p := target("tiff-tiled-deflate-24mp.tif"):
        tifffile.imwrite(p, arr("24mp"), photometric="rgb", compression="zlib", tile=(256, 256)); done(p)
    if p := target("tiff-bigtiff-deflate-24mp.tif"):
        tifffile.imwrite(p, arr("24mp"), photometric="rgb", compression="zlib", rowsperstrip=64, bigtiff=True); done(p)
    if p := target("tiff-pyramid-deflate-24mp.tif"):
        full = arr("24mp")
        with tifffile.TiffWriter(p, bigtiff=False) as tw:
            tw.write(full, photometric="rgb", compression="zlib", tile=(256, 256), subifds=3, metadata=None)
            level = full
            for _ in range(3):  # 1/2, 1/4, 1/8 as SubIFDs (reduced-resolution, NewSubfileType 1)
                level = np.asarray(Image.fromarray(level).resize((level.shape[1] // 2, level.shape[0] // 2), Image.Resampling.BOX))
                tw.write(level, photometric="rgb", compression="zlib", tile=(256, 256), subfiletype=1, metadata=None)
        done(p)

    # ---------------------------------------------------------------- HEIC / AVIF / JXL / JP2 / JXR
    try:
        import pillow_heif
        pillow_heif.register_heif_opener()
        if p := target("heic-12mp.heic"):
            photo("12mp", 11).save(p, "HEIF", quality=75); done(p)
    except Exception as ex:  # noqa: BLE001
        print("HEIC skipped:", ex)
    if p := target("avif-12mp.avif"):
        try:
            photo("12mp", 11).save(p, "AVIF", quality=60, speed=10); done(p)
        except Exception as ex:  # noqa: BLE001
            print("AVIF skipped:", ex)
    if p := target("avif-alpha-4k.avif"):
        try:
            photo("4k", 31, alpha=True).save(p, "AVIF", quality=60, speed=10); done(p)
        except Exception as ex:  # noqa: BLE001
            print("AVIF alpha skipped:", ex)
    if FFMPEG:
        for name, size, seed, alpha, extra in [("jxl-4k.jxl", "4k", 21, False, ["-distance", "1.0", "-effort", "4"]),
                                               ("jxl-12mp.jxl", "12mp", 11, False, ["-distance", "1.0", "-effort", "4"]),
                                               ("jxl-lossless-4k.jxl", "4k", 21, False, ["-distance", "0", "-effort", "3"]),
                                               ("jxl-alpha-4k.jxl", "4k", 31, True, ["-distance", "1.0", "-effort", "4"])]:
            if p := target(name):
                src = temp_png(photo(size, seed, alpha), out)
                ffmpeg(["-i", src, "-c:v", "libjxl", *extra, p]); done(p)
        for name, comp, fmt in [("exr-zip-half-4k.exr", "zip16", "half"), ("exr-zips-half-4k.exr", "zip1", "half"), ("exr-rle-half-4k.exr", "rle", "half"),
                                ("exr-none-float-4k.exr", "none", "float"), ("exr-zip-float-4k.exr", "zip16", "float")]:
            if p := target(name):
                src = temp_png(photo("4k"), out)
                ffmpeg(["-i", src, "-vf", "format=gbrpf32le", "-c:v", "exr", "-compression", comp, "-format", fmt, p]); done(p)
    if p := target("jp2-4k.jp2"):
        photo("4k").save(p, "JPEG2000", quality_mode="rates", quality_layers=[12], irreversible=True, num_resolutions=6); done(p)
    if p := target("jp2-lossless-4k.jp2"):
        photo("4k").save(p, "JPEG2000", quality_mode="rates", quality_layers=[1], irreversible=False, num_resolutions=6); done(p)
    if p := target("j2k-4k.j2k"):
        photo("4k").save(p, "JPEG2000", quality_mode="rates", quality_layers=[12], irreversible=True, num_resolutions=6); done(p)
    for name, size, seed in [("jxr-4k.jxr", "4k", 21), ("jxr-12mp.jxr", "12mp", 11)]:
        if p := target(name):
            src = temp_png(photo(size, seed), out)
            if write_jxr(src, p): done(p)

    # ---------------------------------------------------------------- HDR / PFM
    import cv2
    if p := target("hdr-4k.hdr"):
        cv2.imwrite(p, np.ascontiguousarray(a4k()[:, :, ::-1]).astype(np.float32) / 255.0 * 2.0); done(p)
    if p := target("pfm-4k.pfm"):
        cv2.imwrite(p, np.ascontiguousarray(a4k()[:, :, ::-1]).astype(np.float32) / 255.0); done(p)

    # ---------------------------------------------------------------- Photoshop
    thumb_jpeg = jpeg_bytes(photo("4k").resize((160, 90)))
    if p := target("psd-raw-4k.psd"):
        write_psd(p, a4k(), compression=0, thumb=None); done(p)
    if p := target("psd-rle-4k.psd"):
        write_psd(p, a4k(), compression=1, thumb=thumb_jpeg); done(p)
    if p := target("psd-zip-pred-4k.psd"):
        write_psd(p, a4k(), compression=3, thumb=thumb_jpeg); done(p)
    if p := target("psd-16bit-raw-4k.psd"):
        write_psd(p, (a4k().astype(np.uint16) << 8) | a4k().astype(np.uint16), compression=0, thumb=thumb_jpeg); done(p)
    if p := target("psd-layers-rle-4k.psd"):
        write_psd(p, a4k(), compression=1, thumb=thumb_jpeg, layers=2); done(p)

    # ---------------------------------------------------------------- SVG
    if p := target("svg-simple.svg"):
        open(p, "w").write(make_svg(40, 1200, 800, seed=1)); done(p)
    if p := target("svg-5000.svg"):
        open(p, "w").write(make_svg(5000, 3000, 2000, seed=2)); done(p)
    if p := target("svgz-5000.svgz"):
        with gzip.open(p, "wt") as f: f.write(make_svg(5000, 3000, 2000, seed=2)); done(p)
    if p := target("svg-embedded-png.svg"):
        import base64
        buf = io.BytesIO(); photo("12mp", 11).resize((2000, 1500)).save(buf, "PNG")
        open(p, "w").write(f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="2000" height="1500" viewBox="0 0 2000 1500"><image width="2000" height="1500" xlink:href="data:image/png;base64,{base64.b64encode(buf.getvalue()).decode()}"/></svg>'); done(p)

    # ---------------------------------------------------------------- legacy rasters
    if p := target("tga-4k.tga"):
        photo("4k").save(p, "TGA"); done(p)
    if p := target("tga-rle-4k.tga"):
        photo("4k").save(p, "TGA", compression="tga_rle"); done(p)
    if p := target("pcx-4k.pcx"):
        photo("4k").save(p, "PCX"); done(p)
    if p := target("ppm-4k.ppm"):
        photo("4k").save(p, "PPM"); done(p)
    if p := target("qoi-4k.qoi"):
        photo("4k").save(p, "QOI"); done(p)
    if p := target("sgi-4k.sgi"):
        photo("4k").save(p, "SGI"); done(p)

    # ---------------------------------------------------------------- textures
    if p := target("dds-rgba-4k.dds"):
        photo("4k", 31, alpha=True).save(p, "DDS"); done(p)
    if p := target("dds-dxt1-4k.dds"):
        photo("4k").save(p, "DDS", pixel_format="DXT1"); done(p)
    if p := target("dds-dxt5-4k.dds"):
        photo("4k", 31, alpha=True).save(p, "DDS", pixel_format="DXT5"); done(p)
    if p := target("dds-dxt1-mips-4k.dds"):
        write_dds_mips(p, photo("4k"), "DXT1"); done(p)
    if p := target("ktx1-rgba8-4k.ktx"):
        write_ktx1(p, np.asarray(photo("4k", 31, alpha=True))); done(p)

    # ---------------------------------------------------------------- scientific, layered
    if p := target("fits-int16-2k.fits"):
        write_fits(p, np.asarray(photo("4k").convert("L").resize((2048, 2048))).astype(np.int16) * 200); done(p)
    if p := target("nifti-256x256x64.nii"):
        write_nifti(p); done(p)
    if p := target("ora-4k.ora"):
        write_ora(p, photo("4k")); done(p)

    # ---------------------------------------------------------------- camera RAW containers
    if only is None or "raw" in (only or ""):
        write_raws(single, photo("24mp", 11), force)

    print(f"done: {len(built)} new fixture(s); {len(os.listdir(single))} files in {single}")
    return 0


# ------------------------------------------------------------------------------------------------ helpers

def ffmpeg(args):
    subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", *args], check=True)


def temp_png(image: Image.Image, out: str) -> str:
    path = os.path.join(out, "_tmp-src-%d-%d-%s.png" % (image.width, image.height, image.mode))
    if not os.path.exists(path):
        image.save(path, "PNG", compress_level=1)
    return path


def anim_frames(w, h, n):
    base = perf.make_photo(w, h, 5)
    frames = []
    for i in range(n):
        arr_ = np.roll(np.asarray(base), i * 37, axis=1)
        frames.append(Image.fromarray(arr_))
    return frames


def jpeg_bytes(image: Image.Image, quality=85) -> bytes:
    buf = io.BytesIO(); image.convert("RGB").save(buf, "JPEG", quality=quality); return buf.getvalue()


def png_chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def write_png(path, a: np.ndarray, interlace: bool):
    """8/16-bit gray/RGB/RGBA PNG with the Up filter on every row (vectorised); optional Adam7 interlace."""
    h, w = a.shape[:2]
    ch = 1 if a.ndim == 2 else a.shape[2]
    depth = 16 if a.dtype == np.uint16 else 8
    ctype = {1: 0, 2: 4, 3: 2, 4: 6}[ch]
    sample = a if a.ndim == 3 else a[:, :, None]
    if depth == 16:
        sample = sample.astype(">u2")
    raw = sample.view(np.uint8).reshape(h, -1) if depth == 16 else sample.reshape(h, -1)

    def filtered(rows: np.ndarray) -> bytes:
        up = rows.copy()
        up[1:] = (rows[1:].astype(np.int16) - rows[:-1].astype(np.int16)).astype(np.uint8)
        out_ = np.empty((rows.shape[0], rows.shape[1] + 1), np.uint8)
        out_[:, 0] = 2
        out_[:, 1:] = up
        return out_.tobytes()

    if not interlace:
        data = filtered(raw)
    else:
        parts = []
        bpp = ch * depth // 8
        full = sample if depth == 8 else sample.view(np.uint8).reshape(h, w, ch * 2)
        for xs, ys, xd, yd in [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]:
            sub = full[ys::yd, xs::xd]
            if sub.size == 0:
                continue
            parts.append(filtered(np.ascontiguousarray(sub).reshape(sub.shape[0], -1)))
        data = b"".join(parts)
    ihdr = struct.pack(">IIBBBBB", w, h, depth, ctype, 0, 0, 1 if interlace else 0)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", ihdr) + png_chunk(b"IDAT", zlib.compress(data, 6)) + png_chunk(b"IEND", b""))


def write_jxr(src: str, dst: str) -> bool:
    ps = ("Add-Type -AssemblyName PresentationCore;"
          f"$s=[IO.File]::OpenRead('{src}');$d=[Windows.Media.Imaging.BitmapDecoder]::Create($s,'PreservePixelFormat','OnLoad');"
          "$e=New-Object Windows.Media.Imaging.WmpBitmapEncoder;$e.ImageQualityLevel=0.8;"
          "$e.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($d.Frames[0]));"
          f"$o=[IO.File]::Create('{dst}');$e.Save($o);$o.Close();$s.Close()")
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(dst):
        print("JXR skipped:", r.stderr.strip()[:200])
        return False
    return True


def packbits_literal(row: bytes) -> bytes:
    out_ = bytearray()
    for i in range(0, len(row), 128):
        chunk = row[i:i + 128]
        out_.append(len(chunk) - 1)
        out_.extend(chunk)
    return bytes(out_)


def write_psd(path, a: np.ndarray, compression: int, thumb: bytes | None, layers: int = 0):
    h, w, ch = a.shape
    depth = 16 if a.dtype == np.uint16 else 8
    planes = [np.ascontiguousarray(a[:, :, c]) for c in range(ch)]
    if depth == 16:
        planes = [p.astype(">u2") for p in planes]

    def plane_bytes(p):
        return p.tobytes() if depth == 8 else p.view(np.uint8).tobytes()

    def encode(plane_list, comp):
        if comp == 0:
            return b"".join(plane_bytes(p) for p in plane_list)
        if comp == 1:
            rows = []
            for p in plane_list:
                pb = np.frombuffer(plane_bytes(p), np.uint8).reshape(h, -1)
                rows.extend(packbits_literal(pb[y].tobytes()) for y in range(h))
            counts = b"".join(struct.pack(">H", len(r)) for r in rows)
            return counts + b"".join(rows)
        if comp == 3:  # ZIP with prediction: per-row horizontal delta, one zlib stream
            buf = bytearray()
            for p in plane_list:
                q = p.astype(np.int32)
                d = q.copy(); d[:, 1:] = (q[:, 1:] - q[:, :-1]) & (0xFF if depth == 8 else 0xFFFF)
                buf += d.astype(np.uint8 if depth == 8 else ">u2").tobytes()
            return zlib.compress(bytes(buf), 6)
        raise ValueError(comp)

    res = b""
    if thumb:
        body = struct.pack(">IIIIIIHH", 1, 160, 90, 160 * 3, 160 * 90 * 3, len(thumb), 24, 1) + thumb
        if len(body) % 2: body += b"\0"
        res += b"8BIM" + struct.pack(">H", 1036) + b"\0\0" + struct.pack(">I", len(body)) + body
    res += b"8BIM" + struct.pack(">H", 1005) + b"\0\0" + struct.pack(">I", 16) + struct.pack(">IHHIHH", 72 << 16, 1, 1, 72 << 16, 1, 1)
    layer_section = b"\0\0\0\0"
    if layers:
        recs = b""; data = b""
        for li in range(layers):
            chans = list(range(ch))  # the layer carries the same pixels, raw, no alpha
            payloads = [struct.pack(">H", 0) + plane_bytes(planes[c]) for c in chans]
            recs += struct.pack(">iiiiH", 0, 0, h, w, len(chans))
            for c, pl in zip(chans, payloads):
                recs += struct.pack(">hI", c, len(pl))
            name = bytes([6]) + b"Layer" + str(li + 1).encode()
            name += b"\0" * ((-len(name)) % 4)
            extra = struct.pack(">I", 0) + struct.pack(">I", 0) + name
            recs += b"8BIM" + b"norm" + bytes([255, 0, 8, 0]) + struct.pack(">I", len(extra)) + extra
            data += b"".join(payloads)
        info = struct.pack(">h", layers) + recs + data
        if len(info) % 2: info += b"\0"
        layer_info = struct.pack(">I", len(info)) + info
        layer_section = struct.pack(">I", len(layer_info) + 4) + layer_info + struct.pack(">I", 0)
    with open(path, "wb") as f:
        f.write(b"8BPS" + struct.pack(">H", 1) + b"\0" * 6 + struct.pack(">HIIHH", ch, h, w, depth, 3))
        f.write(struct.pack(">I", 0))
        f.write(struct.pack(">I", len(res)) + res)
        f.write(layer_section)
        f.write(struct.pack(">H", compression) + encode(planes, compression))


def make_svg(count, w, h, seed):
    rng = np.random.default_rng(seed)
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}">',
             '<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2a6fdb"/><stop offset="1" stop-color="#f0a030"/></linearGradient></defs>',
             f'<rect width="{w}" height="{h}" fill="url(#g)"/>']
    for _ in range(count):
        x, y = rng.integers(0, w), rng.integers(0, h)
        c = "#%06x" % int(rng.integers(0, 0xFFFFFF))
        k = rng.integers(0, 3)
        if k == 0:
            parts.append(f'<circle cx="{x}" cy="{y}" r="{rng.integers(5, 80)}" fill="{c}" fill-opacity="0.6"/>')
        elif k == 1:
            parts.append(f'<rect x="{x}" y="{y}" width="{rng.integers(10, 150)}" height="{rng.integers(10, 150)}" fill="{c}" stroke="#000" stroke-width="1" fill-opacity="0.5"/>')
        else:
            pts = " ".join(f"{x + rng.integers(-60, 60)},{y + rng.integers(-60, 60)}" for _ in range(5))
            parts.append(f'<path d="M{pts.split()[0]} L{" L".join(pts.split()[1:])} Z" fill="{c}" fill-opacity="0.55"/>')
    parts.append("</svg>")
    return "\n".join(parts)


def write_dds_mips(path, image: Image.Image, fmt: str):
    levels = []
    img = image
    while True:
        buf = io.BytesIO(); img.save(buf, "DDS", pixel_format=fmt)
        levels.append(buf.getvalue())
        if img.width == 1 and img.height == 1:
            break
        img = img.resize((max(1, img.width // 2), max(1, img.height // 2)), Image.Resampling.BOX)
    header = bytearray(levels[0][:128])
    flags = struct.unpack_from("<I", header, 8)[0] | 0x20000
    struct.pack_into("<I", header, 8, flags)
    struct.pack_into("<I", header, 28, len(levels))                       # dwMipMapCount
    struct.pack_into("<I", header, 108, struct.unpack_from("<I", header, 108)[0] | 0x400008)  # caps: complex | mipmap
    with open(path, "wb") as f:
        f.write(bytes(header)); [f.write(lv[128:]) for lv in levels]


def write_ktx1(path, rgba: np.ndarray):
    h, w = rgba.shape[:2]
    ident = b"\xabKTX 11\xbb\r\n\x1a\n"
    hdr = struct.pack("<13I", 0x04030201, 0x1401, 1, 0x1908, 0x8058, 0x1908, w, h, 0, 0, 1, 1, 0)
    data = rgba[::-1].tobytes()  # KTX rows are bottom-up
    with open(path, "wb") as f:
        f.write(ident + hdr + struct.pack("<I", len(data)) + data)


def write_fits(path, a: np.ndarray):
    h, w = a.shape
    cards = ["SIMPLE  =                    T", "BITPIX  =                   16", "NAXIS   =                    2",
             f"NAXIS1  = {w:>20}", f"NAXIS2  = {h:>20}", "BZERO   =                    0", "BSCALE  =                    1", "END"]
    header = "".join(c.ljust(80) for c in cards)
    header = header.ljust((len(header) + 2879) // 2880 * 2880).encode("ascii")
    data = a[::-1].astype(">i2").tobytes()
    data += b"\0" * ((-len(data)) % 2880)
    open(path, "wb").write(header + data)


def write_nifti(path):
    w, h, d = 256, 256, 64
    rng = np.random.default_rng(7)
    vol = (np.add.outer(np.arange(h), np.arange(w))[None] * 3 + rng.integers(0, 200, (d, h, w))).astype(np.int16)
    hdr = bytearray(348)
    struct.pack_into("<i", hdr, 0, 348)
    struct.pack_into("<8h", hdr, 40, 3, w, h, d, 1, 1, 1, 1)
    struct.pack_into("<h", hdr, 70, 4)   # datatype int16
    struct.pack_into("<h", hdr, 72, 16)  # bitpix
    struct.pack_into("<8f", hdr, 76, 1, 1, 1, 1, 0, 0, 0, 0)
    struct.pack_into("<f", hdr, 108, 352)  # vox_offset
    hdr[344:348] = b"n+1\0"
    open(path, "wb").write(bytes(hdr) + b"\0\0\0\0" + vol.tobytes())


def write_ora(path, image: Image.Image):
    def png(i):
        b = io.BytesIO(); i.save(b, "PNG", compress_level=6); return b.getvalue()
    w, h = image.size
    stack = (f'<?xml version="1.0" encoding="UTF-8"?><image version="0.0.3" w="{w}" h="{h}"><stack>'
             f'<layer name="Layer" src="data/layer0.png" x="0" y="0" opacity="1.0" visibility="visible"/></stack></image>')
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("mimetype", "image/openraster", compress_type=zipfile.ZIP_STORED)
        z.writestr("stack.xml", stack)
        z.writestr("data/layer0.png", png(image), compress_type=zipfile.ZIP_STORED)
        z.writestr("mergedimage.png", png(image), compress_type=zipfile.ZIP_STORED)
        t = image.copy(); t.thumbnail((256, 256))
        z.writestr("Thumbnails/thumbnail.png", png(t))


# ------------------------------------------------------------------------------------------------ RAW containers

def tiff_ifd(entries, base, next_ifd=0):
    """Little-endian IFD at file offset `base`: entries = [(tag, type, count, value-or-bytes)]; values > 4 bytes are appended after the IFD."""
    entries = sorted(entries, key=lambda e: e[0])
    n = len(entries)
    size = 2 + n * 12 + 4
    extra = bytearray()
    body = struct.pack("<H", n)
    fmt = {1: "B", 3: "H", 4: "I", 2: "c"}
    sizes = {1: 1, 2: 1, 3: 2, 4: 4}
    for tag, typ, count, val in entries:
        if isinstance(val, (bytes, bytearray)):
            raw = bytes(val)
        elif isinstance(val, (list, tuple)):
            raw = b"".join(struct.pack("<" + fmt[typ], v) for v in val)
        else:
            raw = struct.pack("<" + fmt[typ], val)
        if len(raw) <= 4:
            body += struct.pack("<HHI", tag, typ, count) + raw.ljust(4, b"\0")
        else:
            body += struct.pack("<HHII", tag, typ, count, base + size + len(extra))
            extra += raw + (b"\0" if len(raw) % 2 else b"")
    return body + struct.pack("<I", next_ifd) + bytes(extra)


def write_raws(single, image: Image.Image, force: bool):
    rng = np.random.default_rng(99)
    sensor = rng.integers(0, 4096, size=12_000_000, dtype=np.uint16).tobytes()  # 24 MB of 12-bit-ish filler "sensor data"
    full = jpeg_bytes(image, 92)
    mid = jpeg_bytes(image.resize((1620, 1080)), 88)
    thumb = jpeg_bytes(image.resize((160, 107)), 80)

    def want(name):
        p = os.path.join(single, name)
        return p if (force or not os.path.exists(p)) else None

    # NEF: IFD0 (thumbnail strip + SubIFD pointer) -> SubIFD 1 = full-size JPEG via JPEGInterchangeFormat, SubIFD 2 = raw.
    # Layout: header 8 | IFD0 | SubIFD offsets | SubIFD A | SubIFD B | thumb | full | sensor
    def tiff_raw(path, preview, preview_w, preview_h, via_strips, dng):
        # Pass 1 with dummy offsets to learn the IFD sizes, pass 2 with the real ones.
        offsets = {"thumb": 0, "prev": 0, "sensor": 0}
        for _ in range(2):
            ifd0_off = 8
            ifd0_entries = [(0x100, 4, 1, 160), (0x101, 4, 1, 107), (0x10F, 2, 6, b"Glide\0"), (0x112, 3, 1, 6),
                            (0x111, 4, 1, offsets["thumb"]), (0x117, 4, 1, len(thumb)), (0x103, 3, 1, 7), (0xFE, 4, 1, 1)]
            sub_a = [(0xFE, 4, 1, 1), (0x100, 4, 1, preview_w), (0x101, 4, 1, preview_h), (0x103, 3, 1, 7 if via_strips else 6)]
            sub_a += [(0x111, 4, 1, offsets["prev"]), (0x117, 4, 1, len(preview))] if via_strips else [(0x201, 4, 1, offsets["prev"]), (0x202, 4, 1, len(preview))]
            sub_b = [(0xFE, 4, 1, 0), (0x100, 4, 1, 6048), (0x101, 4, 1, 4024), (0x103, 3, 1, 1), (0x106, 3, 1, 32803),
                     (0x111, 4, 1, offsets["sensor"]), (0x117, 4, 1, len(sensor))]
            if dng:
                ifd0_entries.append((0xC612, 1, 4, bytes([1, 4, 0, 0])))
            # SubIFDs tag (0x14A) needs the offsets of A and B, which depend on IFD0's size: compute sizes first.
            probe = tiff_ifd(ifd0_entries + [(0x14A, 4, 2, [0, 0])], ifd0_off)
            sub_off_a = ifd0_off + len(probe)
            probe_a = tiff_ifd(sub_a, sub_off_a)
            sub_off_b = sub_off_a + len(probe_a)
            probe_b = tiff_ifd(sub_b, sub_off_b)
            data_off = sub_off_b + len(probe_b)
            offsets = {"thumb": data_off, "prev": data_off + len(thumb), "sensor": data_off + len(thumb) + len(preview)}
        ifd0 = tiff_ifd(ifd0_entries + [(0x14A, 4, 2, [sub_off_a, sub_off_b])], ifd0_off)
        a_ = tiff_ifd(sub_a, sub_off_a)
        b_ = tiff_ifd(sub_b, sub_off_b)
        with open(path, "wb") as f:
            f.write(b"II*\0" + struct.pack("<I", ifd0_off) + ifd0 + a_ + b_ + thumb + preview + sensor)

    if p := want("raw-dng-1600-preview-24mp.dng"):
        tiff_raw(p, jpeg_bytes(image.resize((1600, 1067)), 88), 1600, 1067, via_strips=True, dng=True); done_raw(p)
    if p := want("raw-nef-fullsize-preview-24mp.nef"):
        tiff_raw(p, full, 6000, 4000, via_strips=False, dng=False); done_raw(p)
    if p := want("raw-arw-1620-preview-24mp.arw"):
        tiff_raw(p, mid, 1620, 1080, via_strips=False, dng=False); done_raw(p)
    if p := want("raw-raf-fullsize-preview-24mp.raf"):
        head = bytearray(160)
        head[0:16] = b"FUJIFILMCCD-RAW "; head[16:20] = b"0201"; head[20:28] = b"FF129503"; head[28:32] = b"X-T5"
        jpg_off = 160
        cfa_off = jpg_off + len(full) + (-len(full)) % 16
        struct.pack_into(">IIIIII", head, 84, jpg_off, len(full), 0, 0, cfa_off, len(sensor))
        with open(p, "wb") as f:
            f.write(bytes(head) + full + b"\0" * ((-len(full)) % 16) + sensor)
        done_raw(p)
    if p := want("raw-cr3-1620-preview-24mp.cr3"):
        def box(kind, payload): return struct.pack(">I", 8 + len(payload)) + kind + payload
        canon = bytes([0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48])
        prev_uuid = bytes([0xEA, 0xF4, 0x2B, 0x5E, 0x1C, 0x98, 0x4B, 0x88, 0xB9, 0xFB, 0xB7, 0xDC, 0x40, 0x6E, 0x4D, 0x16])
        cmt1 = box(b"CMT1", b"II*\0" + struct.pack("<I", 8) + tiff_ifd([(0x112, 3, 1, 6)], 8))
        thmb = box(b"THMB", struct.pack(">IHHI", 0, 160, 107, len(thumb)) + b"\0" * 4 + thumb)
        moov = box(b"moov", box(b"uuid", canon + cmt1 + thmb))
        prvw = box(b"PRVW", struct.pack(">IHHHI", 0, 1, 1620, 1080, len(mid)) + mid)
        with open(p, "wb") as f:
            f.write(box(b"ftyp", b"crx \0\0\0\1crx isom") + moov + box(b"uuid", prev_uuid + b"\0" * 8 + prvw) + box(b"mdat", sensor))
        done_raw(p)


def done_raw(p):
    print("  ", os.path.basename(p), f"{os.path.getsize(p) / 1e6:.2f} MB", flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
