"""30 MP JPEG fixtures for the DCT-scaled first view (tests/Glide.Core.Tests/JpegTurboParityTests.cs).

    python tools/bench-507/make_large_jpeg_fixtures.py tests/Glide.Core.Tests/Fixtures/Jpeg

4480 x 6720 (the size of a stripped camera portrait), 4:2:0, no EXIF except the orientation-6 file. Smooth synthetic
content so the files stay small; a solid red block marks the stored top-left corner so orientation is checkable.
"""
import io, os, struct, sys
from PIL import Image

W, H = 4480, 6720


def picture():
    # Small gradient upscaled (cheap and compresses well), then the red top-left marker.
    small = Image.new("RGB", (W // 64, H // 64))
    px = small.load()
    for y in range(small.height):
        for x in range(small.width):
            px[x, y] = (40 + x * 2 % 160, 60 + y % 140, 180)
    im = small.resize((W, H), Image.BILINEAR)
    im.paste((230, 20, 20), (0, 0, W // 4, H // 4))
    return im


def jpeg(im, **kw):
    b = io.BytesIO()
    im.save(b, "JPEG", quality=60, subsampling=2, **kw)
    return b.getvalue()


def exif6(data):
    tiff = b"II*\x00\x08\x00\x00\x00\x01\x00" + struct.pack("<HHIHH", 0x112, 3, 1, 6, 0) + b"\x00\x00\x00\x00"
    payload = b"Exif\x00\x00" + tiff
    return data[:2] + b"\xff\xe1" + struct.pack(">H", len(payload) + 2) + payload + data[2:]


def main():
    out = sys.argv[1]
    im = picture()
    base = jpeg(im)
    files = {
        "big-base420-4480x6720.jpg": base,
        "big-progressive420-4480x6720.jpg": jpeg(im, progressive=True),
        "big-exif6-4480x6720.jpg": exif6(base),
    }
    for name, data in files.items():
        with open(os.path.join(out, name), "wb") as f:
            f.write(data)
        print(name, len(data))


if __name__ == "__main__":
    main()
