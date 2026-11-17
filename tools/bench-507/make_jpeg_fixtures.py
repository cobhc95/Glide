"""Small deterministic JPEG fixtures for tests/Glide.Core.Tests/JpegTurboParityTests.cs (5.0.7).

    python tools/bench-507/make_jpeg_fixtures.py tests/Glide.Core.Tests/Fixtures/Jpeg

All pictures are synthetic (no corpus images). Restart markers come from Pillow's restart_marker_rows;
MPF files carry a CIPA DC-007 APP2 index with one large-thumbnail entry appended after the main image.
"""
import argparse, io, math, os, struct
from PIL import Image


def picture(w, h, mode="RGB"):
    im = Image.new("RGB", (w, h))
    px = im.load()
    for y in range(h):
        for x in range(w):
            r = (x * 255 // w + ((x // 97) % 3) * 20) & 255
            g = (y * 255 // h) & 255
            b = int(128 + 100 * math.sin(x / 71.0) * math.cos(y / 59.0)) & 255
            if (x // 16 + y // 16) % 23 == 0:
                r, g, b = 255 - r, 255 - g, 255 - b  # some sharp edges for the IDCT
            px[x, y] = (r, g, b)
    return im.convert(mode) if mode != "RGB" else im


def jpeg(im, **kw):
    b = io.BytesIO()
    im.save(b, "JPEG", quality=kw.pop("quality", 70), **kw)
    return b.getvalue()


def exif_app1(orientation):
    tiff = b"II*\x00\x08\x00\x00\x00\x01\x00" + struct.pack("<HHIHH", 0x112, 3, 1, orientation, 0) + b"\x00\x00\x00\x00"
    payload = b"Exif\x00\x00" + tiff
    return b"\xff\xe1" + struct.pack(">H", len(payload) + 2) + payload


def with_mpf(main, preview, mp_type=0x010002, offset_override=None):
    """main JPEG + APP2 MPF index (2 entries) right after SOI + preview appended after the main EOI."""
    def app2(prev_off, prev_len):
        entries = struct.pack("<IIIHH", 0x030000, 0, 0, 0, 0) + struct.pack("<IIIHH", mp_type, prev_len, prev_off, 0, 0)
        ifd = struct.pack("<H", 3)
        ifd += struct.pack("<HHI4s", 0xB000, 7, 4, b"0100")
        ifd += struct.pack("<HHII", 0xB001, 4, 1, 2)
        ifd += struct.pack("<HHII", 0xB002, 7, 32, 8 + 2 + 3 * 12 + 4)
        ifd += struct.pack("<I", 0)
        tiff = b"II*\x00" + struct.pack("<I", 8) + ifd + entries
        payload = b"MPF\x00" + tiff
        return b"\xff\xe2" + struct.pack(">H", len(payload) + 2) + payload
    size = len(app2(0, 0))
    base = 2 + 8  # TIFF header after SOI + marker/length + "MPF\0"
    off = len(main) + size - base
    if offset_override is not None:
        off = offset_override
    return main[:2] + app2(off, len(preview)) + main[2:] + preview


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    files = {}
    big = picture(2048, 2000)  # >= 4 MP: banded multi-threaded decode
    small = picture(1001, 667)
    files["base420-2048x2000.jpg"] = jpeg(big, subsampling=2)
    files["base420-odd-2051x1999.jpg"] = jpeg(picture(2051, 1999), subsampling=2)
    files["base420-odd-1001x667.jpg"] = jpeg(small, subsampling=2)
    files["base444-1001x667.jpg"] = jpeg(small, subsampling=0)
    files["base422-1003x667.jpg"] = jpeg(picture(1003, 667), subsampling=1)
    files["gray-1001x667.jpg"] = jpeg(small.convert("L"))
    files["gray-2048x2000.jpg"] = jpeg(big.convert("L"))
    files["progressive-1001x667.jpg"] = jpeg(small, subsampling=2, progressive=True)
    files["progressive444-1001x667.jpg"] = jpeg(small, subsampling=0, progressive=True)
    files["restart-2048x2000.jpg"] = jpeg(big, subsampling=2, restart_marker_rows=1)
    files["restart-1001x667.jpg"] = jpeg(small, subsampling=2, restart_marker_rows=2)
    files["cmyk-adobe-320x240.jpg"] = jpeg(picture(320, 240).convert("CMYK"))
    d = files["base444-1001x667.jpg"]
    files["truncated-1001x667.jpg"] = d[: len(d) * 6 // 10]
    for o in range(2, 9):
        files[f"exif{o}-1001x667.jpg"] = d[:2] + exif_app1(o) + d[2:]
    files["exif6-2048x2000.jpg"] = files["base420-2048x2000.jpg"][:2] + exif_app1(6) + files["base420-2048x2000.jpg"][2:]
    # MPF first-view selection: main = grey picture, preview = solid red, so the chosen source is visible.
    mpf_main = jpeg(Image.new("RGB", (2400, 1600), (40, 90, 200)), subsampling=2)
    red = lambda w, h: jpeg(Image.new("RGB", (w, h), (230, 20, 20)), subsampling=2)
    files["mpf-present-2400x1600.jpg"] = with_mpf(mpf_main, red(1200, 800))
    files["mpf-toosmall-2400x1600.jpg"] = with_mpf(mpf_main, red(240, 160))
    files["mpf-aspect-2400x1600.jpg"] = with_mpf(mpf_main, red(1200, 1000))
    files["mpf-corrupt-2400x1600.jpg"] = with_mpf(mpf_main, b"\xff\xd8\xff\xdb garbage " * 40)
    files["mpf-badoffset-2400x1600.jpg"] = with_mpf(mpf_main, red(1200, 800), offset_override=0x7FFFFF00)
    files["mpf-gainmap-2400x1600.jpg"] = with_mpf(mpf_main, red(1200, 800), mp_type=0x000000)
    files["mpf-absent-2400x1600.jpg"] = mpf_main
    total = 0
    for n, data in sorted(files.items()):
        open(os.path.join(a.out, n), "wb").write(data)
        total += len(data)
        print(f"{n:34s} {len(data):8d}")
    print("total", total)


if __name__ == "__main__":
    main()
