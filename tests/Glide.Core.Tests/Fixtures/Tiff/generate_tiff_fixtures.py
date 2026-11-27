"""Generates the small TIFF fixtures of NativeTiffReaderTests (5.2.0 native TIFF reader, native/Glide.Native/GlideTiff.cpp).

Usage:  python generate_tiff_fixtures.py [output-dir]      (default: this folder)   needs numpy and Pillow (pyramid levels) only

A minimal TIFF writer lives here (classic and BigTIFF, either byte order, strips or tiles, none / LZW / Deflate / PackBits, the horizontal
predictor, SubIFD and reduced-page pyramids) because the readers under test must see structures no single library writes. Every picture is
the same deterministic synthetic scene (posterised gradients, circles, sharp stripes: it compresses to a few tens of KB), so the tests can
compare the native reader with WIC (classic files) or with a classic twin (BigTIFF / pyramid) that carries the same pixels.
"""
from __future__ import annotations

import os
import struct
import sys
import zlib

import numpy as np

OUT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
W, H = 1001, 667


# ---------------------------------------------------------------- scene
def scene(w: int, h: int) -> np.ndarray:
    """RGB uint8, h x w x 3."""
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    r = 255 * x / max(w - 1, 1)
    g = 255 * y / max(h - 1, 1)
    b = 128 + 120 * np.sin((x + y) / 53.0)
    img = (np.clip(np.stack([r, g, b], -1), 0, 255) // 24) * 24
    for cx, cy, rad, col in ((w * 0.3, h * 0.4, h * 0.25, (230, 40, 40)), (w * 0.7, h * 0.6, h * 0.2, (30, 200, 60)), (w * 0.5, h * 0.5, h * 0.08, (250, 250, 20))):
        mask = (x - cx) ** 2 + (y - cy) ** 2 < rad ** 2
        img[mask] = col
    stripes = ((x // 9).astype(int) % 2 == 0) & (y > h * 0.8)
    img[stripes] = (15, 15, 15)
    return img.astype(np.uint8)


# ---------------------------------------------------------------- codecs
def lzw_encode(data: bytes) -> bytes:
    out = bytearray()
    acc = 0
    bits = 0

    def emit(code, width):
        nonlocal acc, bits
        acc = (acc << width) | code
        bits += width
        while bits >= 8:
            out.append((acc >> (bits - 8)) & 255)
            bits -= 8
        acc &= (1 << bits) - 1

    fresh = lambda: {bytes([i]): i for i in range(256)}  # noqa: E731
    table = fresh()
    nxt, width = 258, 9
    emit(256, width)
    w = b""
    for byte in data:
        wc = w + bytes([byte])
        if wc in table:
            w = wc
            continue
        emit(table[w], width)
        table[wc] = nxt
        nxt += 1
        # the decoder runs one table entry behind the encoder, so with "early change" the encoder widens at 512 / 1024 / 2048
        if nxt == 512: width = 10
        elif nxt == 1024: width = 11
        elif nxt == 2048: width = 12
        elif nxt > 4094:
            emit(256, width)
            table = fresh()
            nxt, width = 258, 9
        w = bytes([byte])
    if w:
        emit(table[w], width)
    emit(257, width)
    if bits:
        out.append((acc << (8 - bits)) & 255)
    return bytes(out)


def packbits_encode(data: bytes) -> bytes:
    out = bytearray()
    i, n = 0, len(data)
    while i < n:
        j = i + 1
        while j < n and j - i < 128 and data[j] == data[i]:
            j += 1
        if j - i >= 2:
            out += bytes([257 - (j - i) & 255, data[i]])
            i = j
            continue
        k = i
        while k < n and k - i < 128 and (k + 1 >= n or data[k] != data[k + 1] or k + 2 >= n or data[k + 1] != data[k + 2]):
            k += 1
        k = max(k, i + 1)
        out += bytes([k - i - 1]) + data[i:k]
        i = k
    return bytes(out)


COMPRESS = {"none": (1, lambda b: b), "zlib": (8, lambda b: zlib.compress(b, 6)), "lzw": (5, lzw_encode), "packbits": (32773, packbits_encode)}


# ---------------------------------------------------------------- writer
class Page:
    def __init__(self, data, photometric, compression="zlib", predictor=False, tile=None, rps=None, extrasamples=(), subfiletype=0, orientation=1):
        self.data = data if data.ndim == 3 else data[..., None]
        self.photometric, self.compression, self.predictor = photometric, compression, predictor
        self.tile, self.rps, self.extrasamples, self.subfiletype, self.orientation = tile, rps, extrasamples, subfiletype, orientation
        self.subifds: list[Page] = []


def write_tiff(path, pages, big=False, be=False):
    bo = ">" if be else "<"
    buf = bytearray(b"MM" if be else b"II")
    if big:
        buf += struct.pack(bo + "HHHQ", 43, 8, 0, 0)
    else:
        buf += struct.pack(bo + "HI", 42, 0)
    first_ifd_field = 8 if big else 4
    offs_fmt = "Q" if big else "I"
    inline = 8 if big else 4

    def align():
        if len(buf) & 1:
            buf.append(0)

    def u(fmt, *v):
        return struct.pack(bo + fmt, *v)

    def serialise(arr):
        dt = arr.dtype.newbyteorder(bo)
        return arr.astype(dt).tobytes()

    def encode_units(p: Page):
        arr = p.data
        h, w, spp = arr.shape
        if p.tile:
            tw, th = p.tile
            ph, pw = -(-h // th) * th, -(-w // tw) * tw
            padded = np.zeros((ph, pw, spp), arr.dtype)
            padded[:h, :w] = arr
            units = [padded[ty:ty + th, tx:tx + tw] for ty in range(0, ph, th) for tx in range(0, pw, tw)]
        else:
            rps = p.rps or h
            units = [arr[y:y + rps] for y in range(0, h, rps)]
        out = []
        for unit in units:
            u_arr = unit.copy()
            if p.predictor:
                # rows are laid out as pixels x samples: difference against the previous PIXEL, same sample
                rows = unit.astype(np.int64)
                flat = rows.reshape(rows.shape[0], -1)
                d = flat.copy()
                d[:, spp:] = flat[:, spp:] - flat[:, :-spp]
                u_arr = (d & (0xFFFF if arr.dtype == np.uint16 else 0xFF)).astype(arr.dtype)
            raw = serialise(u_arr)
            out.append(COMPRESS[p.compression][1](raw))
        return out

    def write_ifd(p: Page, subifd_offsets=()):
        """Writes the strip / tile data, then the IFD; returns (ifd_offset, position of the next-IFD field)."""
        arr = p.data
        h, w, spp = arr.shape
        units = encode_units(p)
        offsets, counts = [], []
        for blob in units:
            align()
            offsets.append(len(buf))
            counts.append(len(blob))
            buf.extend(blob)
        align()
        bps = 16 if arr.dtype == np.uint16 else 8
        entries = []  # (tag, type, values)
        entries.append((254, 4, [p.subfiletype])) if p.subfiletype else None
        entries.append((256, 4, [w]))
        entries.append((257, 4, [h]))
        entries.append((258, 3, [bps] * spp))
        entries.append((259, 3, [COMPRESS[p.compression][0]]))
        entries.append((262, 3, [p.photometric]))
        otype = 16 if big else 4
        if p.tile:
            entries.append((322, 4, [p.tile[0]]))
            entries.append((323, 4, [p.tile[1]]))
            entries.append((324, otype, offsets))
            entries.append((325, otype, counts))
        else:
            entries.append((273, otype, offsets))
            entries.append((278, 4, [p.rps or h]))
            entries.append((279, otype, counts))
        if p.orientation != 1:
            entries.append((274, 3, [p.orientation]))
        entries.append((277, 3, [spp]))
        entries.append((284, 3, [1]))
        if p.predictor:
            entries.append((317, 3, [2]))
        if subifd_offsets:
            entries.append((330, 18 if big else 4, list(subifd_offsets)))
        if p.extrasamples:
            entries.append((338, 3, list(p.extrasamples)))
        entries.sort(key=lambda e: e[0])
        sizes = {3: 2, 4: 4, 16: 8, 18: 8}
        fmts = {3: "H", 4: "I", 16: "Q", 18: "Q"}
        ifd_pos = len(buf)
        count_bytes = u("Q", len(entries)) if big else u("H", len(entries))
        entry_size = 20 if big else 12
        head = len(count_bytes) + len(entries) * entry_size + inline
        ext = bytearray()
        body = bytearray()
        for tag, typ, vals in entries:
            data = b"".join(u(fmts[typ], v) for v in vals)
            if len(data) <= inline:
                value = data + b"\0" * (inline - len(data))
                # inline values are left aligned in the field; for big endian SHORTs that is the natural packing
            else:
                value = u(offs_fmt, ifd_pos + head + len(ext))
                ext += data
            body += u("HH", tag, typ) + (u("Q", len(vals)) if big else u("I", len(vals))) + value
        buf.extend(count_bytes)
        buf.extend(body)
        next_field = len(buf)
        buf.extend(b"\0" * inline)
        buf.extend(ext)
        align()
        return ifd_pos, next_field

    prev_next = first_ifd_field
    for page in pages:
        sub_offsets = []
        for sub in page.subifds:
            off, _ = write_ifd(sub)
            sub_offsets.append(off)
        ifd, next_field = write_ifd(page, sub_offsets)
        buf[prev_next:prev_next + inline] = struct.pack(bo + offs_fmt, ifd)
        prev_next = next_field
    with open(path, "wb") as f:
        f.write(buf)
    print(f"{os.path.basename(path)} {len(buf)}")


def out(name):
    return os.path.join(OUT, name)


rgb = scene(W, H)
gray = (rgb[..., 0].astype(np.float32) * 0.3 + rgb[..., 1] * 0.59 + rgb[..., 2] * 0.11).astype(np.uint8)
alpha = np.clip(255 * (np.arange(W)[None, :] / (W - 1)) * np.ones((H, 1)), 0, 255).astype(np.uint8)
rgb16 = (rgb.astype(np.uint16) << 8) | rgb.astype(np.uint16)
rgba = np.dstack([rgb, alpha])
rgba_assoc = np.dstack([(rgb.astype(np.uint16) * alpha[..., None] + 127) // 255, alpha]).astype(np.uint8)
cmyk = np.dstack([255 - rgb[..., 0], 255 - rgb[..., 1], 255 - rgb[..., 2], (np.minimum(np.minimum(255 - rgb[..., 0], 255 - rgb[..., 1]), 255 - rgb[..., 2]) // 3).astype(np.uint8)])

# tiled, classic (every one has partial tiles at the right and bottom edge)
write_tiff(out("tiled-deflate-256.tif"), [Page(rgb, 2, "zlib", tile=(256, 256))])
write_tiff(out("tiled-lzw-pred2-128.tif"), [Page(rgb, 2, "lzw", True, tile=(128, 128))])
small = np.ascontiguousarray(rgb[:131, :257])
write_tiff(out("tiled-packbits-64.tif"), [Page(small, 2, "packbits", tile=(64, 64))])
write_tiff(out("tiled-none-96x32.tif"), [Page(small, 2, "none", tile=(96, 32))])
write_tiff(out("tiled-gray-deflate.tif"), [Page(gray, 1, "zlib", tile=(128, 128))])
write_tiff(out("tiled-graywhite-deflate.tif"), [Page(255 - gray, 0, "zlib", tile=(128, 128))])
write_tiff(out("tiled-rgb16-deflate-pred2.tif"), [Page(rgb16, 2, "zlib", True, tile=(128, 128))])
write_tiff(out("tiled-rgb16-be.tif"), [Page(rgb16, 2, "zlib", True, tile=(128, 128))], be=True)
write_tiff(out("tiled-rgb-be-lzw.tif"), [Page(rgb, 2, "lzw", True, tile=(128, 128))], be=True)
write_tiff(out("tiled-rgba-unassoc.tif"), [Page(rgba, 2, "zlib", tile=(128, 128), extrasamples=(2,))])
write_tiff(out("tiled-rgba-assoc.tif"), [Page(rgba_assoc, 2, "zlib", tile=(128, 128), extrasamples=(1,))])
write_tiff(out("tiled-cmyk.tif"), [Page(cmyk, 5, "zlib", tile=(128, 128))])
write_tiff(out("tiled-orient6.tif"), [Page(rgb, 2, "zlib", tile=(128, 128), orientation=6)])
write_tiff(out("strips-cmyk.tif"), [Page(np.ascontiguousarray(cmyk[:300, :400]), 5, "lzw", rps=40)])
write_tiff(out("strips-lzw-twin.tif"), [Page(rgb, 2, "lzw", True, rps=37)])

# pyramids: 1200 x 900 base (256 tiles), three reduced levels (600 x 450, 300 x 225, 150 x 112) as SubIFDs and as reduced pages
from PIL import Image  # noqa: E402

big_img = scene(1200, 900)
levels = [big_img]
for _ in range(3):
    levels.append(np.asarray(Image.fromarray(levels[-1]).resize((levels[-1].shape[1] // 2, levels[-1].shape[0] // 2), Image.Resampling.BOX)))
base = Page(levels[0], 2, "zlib", tile=(256, 256))
base.subifds = [Page(lv, 2, "zlib", tile=(128, 128), subfiletype=1) for lv in levels[1:]]
write_tiff(out("pyramid-subifd.tif"), [base])
write_tiff(out("pyramid-pages.tif"), [Page(levels[0], 2, "zlib", tile=(256, 256))] + [Page(lv, 2, "zlib", tile=(128, 128), subfiletype=1) for lv in levels[1:]])
write_tiff(out("pyramid-twin-classic.tif"), [Page(levels[0], 2, "zlib", tile=(256, 256))])

# BigTIFF: same pixels as a classic twin
write_tiff(out("bigtiff-strips-lzw-be.tif"), [Page(rgb, 2, "lzw", True, rps=37)], big=True, be=True)
write_tiff(out("bigtiff-tiled-deflate.tif"), [Page(rgb, 2, "zlib", tile=(256, 256))], big=True)
write_tiff(out("bigtiff-gray16-deflate.tif"), [Page((gray.astype(np.uint16) << 8) | gray, 1, "zlib", rps=64)], big=True)
write_tiff(out("bigtiff-twin-gray16.tif"), [Page((gray.astype(np.uint16) << 8) | gray, 1, "zlib", rps=64)])

# damaged files: they must be declined or fail cleanly, never crash
src = open(out("tiled-deflate-256.tif"), "rb").read()
open(out("damaged-truncated.tif"), "wb").write(src[: len(src) // 2])
buf = bytearray(src)
buf[8:16] = b"\xff" * 8
open(out("damaged-header-scribble.tif"), "wb").write(bytes(buf))
buf = bytearray(src)
for i in range(len(buf) // 3, len(buf) // 3 + 2048):
    buf[i] = (buf[i] * 7 + 13) & 255
open(out("damaged-deflate-data.tif"), "wb").write(bytes(buf))
print("done")
