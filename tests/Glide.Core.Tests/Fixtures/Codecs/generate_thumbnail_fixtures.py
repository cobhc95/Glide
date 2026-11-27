r"""Fixtures for the 5.2.0 preview shortcuts of the bundled codec provider (HEIC thumbnail item, JPEG XL DC image).

Developer-only; the generated files are committed next to this script and the tests never run it.

    python -I generate_thumbnail_fixtures.py [output-dir]      needs numpy, Pillow, pillow-heif (libheif + x265) and ffmpeg with libjxl

heic-thumb.heic   1600 x 1200 HEVC primary item (the scene) plus a 320 x 240 'thmb' thumbnail item holding the INVERTED scene, so a test
                  can tell which of the two a preview request was served from. Assembled by hand from two single-image files written
                  by pillow-heif (libheif cannot attach a thumbnail through its Python bindings).
jxl-dc.jxl        1600 x 1200 lossy (VarDCT, distance 1.0) picture of the same scene: the DC image is a 200 x 150 preview.
"""
from __future__ import annotations

import os
import shutil
import struct
import subprocess
import sys
import tempfile

import numpy as np
import pillow_heif
from PIL import Image, ImageOps

pillow_heif.register_heif_opener()
OUT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
# GLIDE_FIXTURE_SIZE=4000x3000 GLIDE_FIXTURE_SUFFIX=-12mp writes bigger copies (heic-thumb-12mp.heic, jxl-dc-12mp.jxl) for the speed audit
W, H = (int(v) for v in os.environ.get("GLIDE_FIXTURE_SIZE", "1600x1200").split("x"))
SUFFIX = os.environ.get("GLIDE_FIXTURE_SUFFIX", "")


def scene(w, h):
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    img = np.stack([255 * x / (w - 1), 255 * y / (h - 1), 128 + 120 * np.sin((x + y) / 97.0)], -1)
    for cx, cy, rad, col in ((w * 0.3, h * 0.4, h * 0.25, (230, 40, 40)), (w * 0.7, h * 0.6, h * 0.2, (30, 200, 60))):
        img[(x - cx) ** 2 + (y - cy) ** 2 < rad ** 2] = col
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))


# ---------------------------------------------------------------- minimal ISO BMFF reading / writing
def parse_boxes(data, start, end):
    i = start
    while i + 8 <= end:
        size, kind = struct.unpack(">I4s", data[i:i + 8])
        header = 8
        if size == 1:
            size = struct.unpack(">Q", data[i + 8:i + 16])[0]
            header = 16
        elif size == 0:
            size = end - i
        yield kind.decode("latin1"), i + header, i + size, i
        i += size


def box(kind, payload):
    return struct.pack(">I4s", 8 + len(payload), kind.encode()) + payload


def full(kind, version, flags, payload):
    return box(kind, struct.pack(">I", (version << 24) | flags) + payload)


def read_heic(path):
    data = open(path, "rb").read()
    meta = next(b for b in parse_boxes(data, 0, len(data)) if b[0] == "meta")
    mstart = meta[1] + 4
    props, assoc, pitm, iloc = {}, {}, None, None
    for kind, s, e, _ in parse_boxes(data, mstart, meta[2]):
        if kind == "pitm":
            pitm = struct.unpack(">H", data[s + 4:s + 6])[0]
        elif kind == "iloc":
            iloc = (s, e)
        elif kind == "iprp":
            for k2, s2, e2, b2 in parse_boxes(data, s, e):
                if k2 == "ipco":
                    props = {i + 1: (k3, data[b3:e3]) for i, (k3, s3, e3, b3) in enumerate(parse_boxes(data, s2, e2))}
                elif k2 == "ipma":
                    ver_flags = struct.unpack(">I", data[s2:s2 + 4])[0]
                    big = ver_flags & 1
                    count = struct.unpack(">I", data[s2 + 4:s2 + 8])[0]
                    p = s2 + 8
                    for _ in range(count):
                        if (ver_flags >> 24) < 1:
                            item = struct.unpack(">H", data[p:p + 2])[0]; p += 2
                        else:
                            item = struct.unpack(">I", data[p:p + 4])[0]; p += 4
                        n = data[p]; p += 1
                        idx = []
                        for _ in range(n):
                            if big:
                                v = struct.unpack(">H", data[p:p + 2])[0]; p += 2; idx.append(v & 0x7FFF)
                            else:
                                idx.append(data[p] & 0x7F); p += 1
                        assoc[item] = idx
    s, e = iloc
    version = data[s]
    sizes = struct.unpack(">H", data[s + 4:s + 6])[0]
    off_size, len_size = sizes >> 12, (sizes >> 8) & 15
    base_size, index_size = (sizes >> 4) & 15, sizes & 15
    p = s + 6
    count = struct.unpack(">H", data[p:p + 2])[0] if version < 2 else struct.unpack(">I", data[p:p + 4])[0]
    p += 2 if version < 2 else 4
    items = {}

    def rd(n):
        nonlocal p
        v = int.from_bytes(data[p:p + n], "big") if n else 0
        p += n
        return v

    for _ in range(count):
        item = rd(2 if version < 2 else 4)
        method = rd(2) & 15 if version in (1, 2) else 0
        rd(2)
        base = rd(base_size)
        extents = rd(2)
        out = b""
        for _ in range(extents):
            if version in (1, 2) and index_size:
                rd(index_size)
            off, ln = rd(off_size), rd(len_size)
            out += data[base + off:base + off + ln]
        items[item] = out
    wanted = [(props[i][0], props[i][1]) for i in assoc[pitm] if props[i][0] in ("ispe", "hvcC")]
    kinds = {k for k, _ in wanted}
    assert kinds == {"ispe", "hvcC"}, f"{path}: primary item properties {kinds} (expected a plain hvc1 item)"
    return {"props": dict(wanted), "data": items[pitm]}


def build_heic(main, thumb, out_path):
    # item 1 = primary, item 2 = thumbnail; properties: 1 ispe(main) 2 hvcC(main) 3 ispe(thumb) 4 hvcC(thumb)
    ipco = box("ipco", main["props"]["ispe"] + main["props"]["hvcC"] + thumb["props"]["ispe"] + thumb["props"]["hvcC"])
    ipma = full("ipma", 0, 0, struct.pack(">I", 2) + struct.pack(">HB", 1, 2) + bytes([1, 0x80 | 2]) + struct.pack(">HB", 2, 2) + bytes([3, 0x80 | 4]))
    iprp = box("iprp", ipco + ipma)
    hdlr = full("hdlr", 0, 0, struct.pack(">I", 0) + b"pict" + b"\0" * 12 + b"\0")
    pitm = full("pitm", 0, 0, struct.pack(">H", 1))

    def infe(item_id):
        return full("infe", 2, 0, struct.pack(">HH", item_id, 0) + b"hvc1" + b"\0")

    iinf = full("iinf", 0, 0, struct.pack(">H", 2) + infe(1) + infe(2))
    iref = full("iref", 0, 0, box("thmb", struct.pack(">HHH", 2, 1, 1)))

    def iloc(o1, o2):
        body = struct.pack(">BBH", 0x44, 0x00, 2)
        body += struct.pack(">HHHII", 1, 0, 1, o1, len(main["data"]))
        body += struct.pack(">HHHII", 2, 0, 1, o2, len(thumb["data"]))
        return full("iloc", 0, 0, body)

    ftyp = box("ftyp", b"heic" + struct.pack(">I", 0) + b"mif1heic")

    def meta(o1, o2):
        return full("meta", 0, 0, hdlr + pitm + iinf + iref + iprp + iloc(o1, o2))

    first = len(ftyp) + len(meta(0, 0)) + 8  # 8 = mdat header
    m = meta(first, first + len(main["data"]))
    mdat = box("mdat", main["data"] + thumb["data"])
    open(out_path, "wb").write(ftyp + m + mdat)
    print(f"{os.path.basename(out_path)} {os.path.getsize(out_path)} bytes")


tmp = tempfile.mkdtemp()
try:
    img = scene(W, H)
    img.save(os.path.join(tmp, "main.heic"), quality=45)
    ImageOps.invert(img.resize((320, 240), Image.Resampling.BOX)).save(os.path.join(tmp, "thumb.heic"), quality=60)
    build_heic(read_heic(os.path.join(tmp, "main.heic")), read_heic(os.path.join(tmp, "thumb.heic")), os.path.join(OUT, f"heic-thumb{SUFFIX}.heic"))

    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg:
        img.save(os.path.join(tmp, "scene.png"))
        subprocess.run([ffmpeg, "-y", "-loglevel", "error", "-i", os.path.join(tmp, "scene.png"), "-c:v", "libjxl", "-distance", "1.0", "-effort", "4",
                        os.path.join(OUT, f"jxl-dc{SUFFIX}.jxl")], check=True)
        print(f"jxl-dc{SUFFIX}.jxl {os.path.getsize(os.path.join(OUT, f'jxl-dc{SUFFIX}.jxl'))} bytes")
    else:
        print("ffmpeg not found: jxl-dc.jxl not written")
finally:
    shutil.rmtree(tmp, ignore_errors=True)
