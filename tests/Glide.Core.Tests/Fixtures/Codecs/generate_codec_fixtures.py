r"""Regenerates the tiny synthetic fixtures for the bundled codec provider (glidecodecs.dll).

Developer-only; the generated files are committed next to this script and the tests never run it.
Needs a throwaway venv (never the user's Python):

    python -m venv %TEMP%\glide-fixtures
    %TEMP%\glide-fixtures\Scripts\python -m pip install pillow pillow-heif imagecodecs numpy pillow-jxl-plugin
    %TEMP%\glide-fixtures\Scripts\python -I generate_codec_fixtures.py

Encoders: Pillow (libavif) for AVIF/AVIFS, pillow-heif (libheif + x265) for HEIC, imagecodecs
(libjxl, OpenJPEG) for JXL and JPEG 2000, and the libjxl DLL bundled with pillow-jxl-plugin
(through ctypes) for the one JXL feature imagecodecs does not expose: the orientation header.

Every image uses the same 32x16 layout so the C# test can assert sample colours:
    top-left red, top-right green, bottom-left blue, bottom-right white (quadrants of 16x8).
Alpha images: left half opaque red, right half blue at alpha 128.
Rotated images carry orientation 6 (display = stored rotated 90 degrees clockwise -> 16x32).
"""
import ctypes
import os
import shutil
import struct
import sys

import imagecodecs
import numpy as np
import pillow_heif
from PIL import Image

OUT = os.path.dirname(os.path.abspath(__file__))
W, H = 32, 16
RED, GREEN, BLUE, WHITE = (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255)


def quadrants(dtype=np.uint8, maxval=255):
    a = np.zeros((H, W, 3), dtype)
    scale = maxval / 255.0
    for (y0, x0), c in {(0, 0): RED, (0, 16): GREEN, (8, 0): BLUE, (8, 16): WHITE}.items():
        a[y0:y0 + 8, x0:x0 + 16] = [round(v * scale) for v in c]
    return a


def alpha_image():
    a = np.zeros((H, W, 4), np.uint8)
    a[:, :16] = (255, 0, 0, 255)
    a[:, 16:] = (0, 0, 255, 128)
    return a


def write(name, data):
    with open(os.path.join(OUT, name), 'wb') as f:
        f.write(data)
    print(f'{name:28s} {len(data):6d} bytes')


def copy(src, dst):
    shutil.copyfile(os.path.join(OUT, src), os.path.join(OUT, dst))
    print(f'{dst:28s} (copy of {src})')


def exif_orientation(value):
    exif = Image.Exif()
    exif[0x0112] = value
    return exif.tobytes()


def save_pil(name, image, **kwargs):
    path = os.path.join(OUT, name)
    image.save(path, **kwargs)
    print(f'{name:28s} {os.path.getsize(path):6d} bytes')


# ---- AVIF / AVIFS (Pillow + libavif) -----------------------------------------------------------
rgb = Image.fromarray(quadrants())
save_pil('basic.avif', rgb, quality=100, subsampling='4:4:4')
save_pil('alpha.avif', Image.fromarray(alpha_image(), 'RGBA'), quality=100, subsampling='4:4:4')
save_pil('rotated.avif', rgb, quality=100, subsampling='4:4:4', exif=exif_orientation(6))  # -> irot
write('10bit.avif', imagecodecs.avif_encode(quadrants(np.uint16, 1023), level=100, bitspersample=10))
save_pil('anim.avifs', rgb, format='AVIF', save_all=True, quality=100, subsampling='4:4:4',
         append_images=[Image.new('RGB', (W, H), BLUE)], duration=100)

# ---- HEIC / HEIF (pillow-heif: libheif + x265) --------------------------------------------------
pillow_heif.register_heif_opener()
save_pil('basic.heic', rgb, quality=-1)  # -1 = lossless (4:4:4) in pillow-heif
save_pil('alpha.heic', Image.fromarray(alpha_image(), 'RGBA'), quality=-1)
save_pil('rotated.heic', rgb, quality=-1, exif=exif_orientation(6))  # -> irot
pillow_heif.from_bytes('RGB;16', (W, H), quadrants(np.uint16, 65535).tobytes()).save(
    os.path.join(OUT, '10bit.heic'), quality=-1)
print(f'{"10bit.heic":28s} {os.path.getsize(os.path.join(OUT, "10bit.heic")):6d} bytes')
for ext in ('heif', 'hif', 'heics', 'heifs'):
    copy('basic.heic', f'basic.{ext}')

# ---- JPEG XL (imagecodecs + libjxl via ctypes for orientation) ----------------------------------
write('basic.jxl', imagecodecs.jpegxl_encode(quadrants(), lossless=True))
write('lossy.jxl', imagecodecs.jpegxl_encode(quadrants(), distance=0.5))  # XYB path
write('alpha.jxl', imagecodecs.jpegxl_encode(alpha_image(), lossless=True, usecontainer=True))
write('16bit.jxl', imagecodecs.jpegxl_encode(quadrants(np.uint16, 65535), lossless=True))


def jxl_with_orientation(pixels, orientation):
    import pillow_jxl
    libs = os.path.join(os.path.dirname(os.path.dirname(pillow_jxl.__file__)), 'pillow_jxl_plugin.libs')
    os.add_dll_directory(libs)
    name = next(f for f in os.listdir(libs) if f.startswith('libjxl-'))
    jxl = ctypes.CDLL(os.path.join(libs, name))
    jxl.JxlEncoderCreate.restype = ctypes.c_void_p
    jxl.JxlEncoderFrameSettingsCreate.restype = ctypes.c_void_p
    jxl.JxlEncoderFrameSettingsCreate.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    for fn in ('JxlEncoderSetBasicInfo', 'JxlEncoderSetColorEncoding', 'JxlEncoderCloseInput', 'JxlEncoderDestroy'):
        getattr(jxl, fn).argtypes = [ctypes.c_void_p, ctypes.c_void_p][:2 if fn.startswith('JxlEncoderSet') else 1]
    jxl.JxlEncoderSetFrameLossless.argtypes = [ctypes.c_void_p, ctypes.c_int]
    jxl.JxlEncoderAddImageFrame.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t]
    jxl.JxlEncoderProcessOutput.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_size_t)]

    enc = jxl.JxlEncoderCreate(None)
    info = (ctypes.c_uint8 * 512)()  # JxlBasicInfo (offsets from jxl/codestream_header.h)
    jxl.JxlEncoderInitBasicInfo(info)
    h, w = pixels.shape[:2]
    for offset, value in ((4, w), (8, h), (12, 8), (36, 1), (48, orientation), (52, 3)):
        struct.pack_into('<I', info, offset, value)
    assert jxl.JxlEncoderSetBasicInfo(enc, info) == 0
    color = (ctypes.c_uint8 * 256)()
    jxl.JxlColorEncodingSetToSRGB(color, 0)
    assert jxl.JxlEncoderSetColorEncoding(enc, color) == 0
    settings = jxl.JxlEncoderFrameSettingsCreate(enc, None)
    assert jxl.JxlEncoderSetFrameLossless(settings, 1) == 0
    fmt = struct.pack('<Iii4xQ', 3, 2, 0, 0)  # 3 channels, JXL_TYPE_UINT8, native endian, align 0
    fmt_buf = ctypes.create_string_buffer(fmt, len(fmt))
    data = np.ascontiguousarray(pixels).tobytes()
    assert jxl.JxlEncoderAddImageFrame(settings, fmt_buf, data, len(data)) == 0
    jxl.JxlEncoderCloseInput(enc)
    out = bytearray()
    while True:
        buf = (ctypes.c_uint8 * 65536)()
        ptr = ctypes.c_void_p(ctypes.addressof(buf))
        avail = ctypes.c_size_t(len(buf))
        status = jxl.JxlEncoderProcessOutput(enc, ctypes.byref(ptr), ctypes.byref(avail))
        out += bytes(buf[:len(buf) - avail.value])
        if status == 0:
            break
        assert status == 2, status
    jxl.JxlEncoderDestroy(enc)
    return bytes(out)


write('rotated.jxl', jxl_with_orientation(quadrants(), 6))

# ---- JPEG XL with an ICC profile that permutes the device channels (colour-management regression) --------


def icc_s15(v):
    return struct.pack(">i", int(round(v * 65536)))


def xyz_tag(x, y, z):
    return b"XYZ \0\0\0\0" + icc_s15(x) + icc_s15(y) + icc_s15(z)


def curv_gamma(g):
    return b"curv\0\0\0\0" + struct.pack(">I", 1) + struct.pack(">H", int(round(g * 256))) + b"\0\0"


def desc_tag(text):
    t = text.encode("ascii") + b"\0"
    return b"desc\0\0\0\0" + struct.pack(">I", len(t)) + t + struct.pack(">IIHB", 0, 0, 0, 0) + bytes(67)


def text_tag(text):
    return b"text\0\0\0\0" + text.encode("ascii") + b"\0"


def permuted_rgb_icc():
    r, g, b = (0.4360747, 0.2225045, 0.0139322), (0.3850649, 0.7168786, 0.0971045), (0.1430804, 0.0606169, 0.7141733)
    tags = {
        b"desc": desc_tag("Glide test: permuted RGB"), b"cprt": text_tag("Public domain test profile"),
        b"wtpt": xyz_tag(0.9642, 1.0, 0.8249),
        b"rXYZ": xyz_tag(*g), b"gXYZ": xyz_tag(*b), b"bXYZ": xyz_tag(*r),
        b"rTRC": curv_gamma(2.2), b"gTRC": curv_gamma(2.2), b"bTRC": curv_gamma(2.2),
    }
    order = list(tags)
    offset = 128 + 4 + 12 * len(order)
    entries, body = [], b""
    for sig in order:
        data = tags[sig]
        entries.append(struct.pack(">4sII", sig, offset + len(body), len(data)))
        body += data + bytes((4 - len(data) % 4) % 4)
    header = bytearray(128)
    struct.pack_into(">I", header, 8, 0x02400000)
    header[12:16] = b"mntr"; header[16:20] = b"RGB "; header[20:24] = b"XYZ "; header[36:40] = b"acsp"
    struct.pack_into(">III", header, 68, *[int(round(v * 65536)) for v in (0.9642, 1.0, 0.8249)])
    profile = bytearray(header) + struct.pack(">I", len(order)) + b"".join(entries) + body
    struct.pack_into(">I", profile, 0, len(profile))
    return bytes(profile)


def jxl_with_icc(pixels, icc):
    import pillow_jxl
    libs = os.path.join(os.path.dirname(os.path.dirname(pillow_jxl.__file__)), 'pillow_jxl_plugin.libs')
    os.add_dll_directory(libs)
    name = next(f for f in os.listdir(libs) if f.startswith('libjxl-'))
    jxl = ctypes.CDLL(os.path.join(libs, name))
    jxl.JxlEncoderCreate.restype = ctypes.c_void_p
    jxl.JxlEncoderFrameSettingsCreate.restype = ctypes.c_void_p
    jxl.JxlEncoderFrameSettingsCreate.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    for fn in ('JxlEncoderSetBasicInfo', 'JxlEncoderCloseInput', 'JxlEncoderDestroy'):
        getattr(jxl, fn).argtypes = [ctypes.c_void_p, ctypes.c_void_p][:2 if fn == 'JxlEncoderSetBasicInfo' else 1]
    jxl.JxlEncoderSetICCProfile.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_size_t]
    jxl.JxlEncoderSetFrameLossless.argtypes = [ctypes.c_void_p, ctypes.c_int]
    jxl.JxlEncoderAddImageFrame.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t]
    jxl.JxlEncoderProcessOutput.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_size_t)]

    enc = jxl.JxlEncoderCreate(None)
    info = (ctypes.c_uint8 * 512)()  # JxlBasicInfo (offsets from jxl/codestream_header.h)
    jxl.JxlEncoderInitBasicInfo(info)
    h, w = pixels.shape[:2]
    for offset, value in ((4, w), (8, h), (12, 8), (36, 1), (48, 1), (52, 3)):
        struct.pack_into('<I', info, offset, value)
    assert jxl.JxlEncoderSetBasicInfo(enc, info) == 0
    assert jxl.JxlEncoderSetICCProfile(enc, icc, len(icc)) == 0
    settings = jxl.JxlEncoderFrameSettingsCreate(enc, None)
    assert jxl.JxlEncoderSetFrameLossless(settings, 1) == 0
    fmt = struct.pack('<Iii4xQ', 3, 2, 0, 0)  # 3 channels, JXL_TYPE_UINT8, native endian, align 0
    fmt_buf = ctypes.create_string_buffer(fmt, len(fmt))
    data = np.ascontiguousarray(pixels).tobytes()
    assert jxl.JxlEncoderAddImageFrame(settings, fmt_buf, data, len(data)) == 0
    jxl.JxlEncoderCloseInput(enc)
    out = bytearray()
    while True:
        buf = (ctypes.c_uint8 * 65536)()
        ptr = ctypes.c_void_p(ctypes.addressof(buf))
        avail = ctypes.c_size_t(len(buf))
        status = jxl.JxlEncoderProcessOutput(enc, ctypes.byref(ptr), ctypes.byref(avail))
        out += bytes(buf[:len(buf) - avail.value])
        if status == 0:
            break
        assert status == 2, status
    jxl.JxlEncoderDestroy(enc)
    return bytes(out)




write('icc-permuted.jxl', jxl_with_icc(quadrants(), permuted_rgb_icc()))  # shown as green/blue/red/white by a colour-managed decoder

# ---- JPEG 2000 family (imagecodecs + OpenJPEG) --------------------------------------------------
j2k = imagecodecs.jpeg2k_encode(quadrants(), codecformat='J2K', resolutions=3)
write('basic.j2k', j2k)
copy('basic.j2k', 'basic.j2c')
copy('basic.j2k', 'basic.jpc')
jp2 = imagecodecs.jpeg2k_encode(quadrants(), codecformat='JP2', resolutions=3)
write('basic.jp2', jp2)
write('alpha.jp2', imagecodecs.jpeg2k_encode(alpha_image(), codecformat='JP2'))
write('16bit.jp2', imagecodecs.jpeg2k_encode(quadrants(np.uint16, 65535), codecformat='JP2'))


def box(kind, payload):
    return struct.pack('>I', 8 + len(payload)) + kind + payload


assert jp2[20:24] == b'jp2 ', jp2[:32]
jpx = bytearray(jp2)
jpx[20:24] = b'jpx '  # ftyp brand; the compatibility list still names 'jp2 '
write('basic.jpx', bytes(jpx))
copy('basic.jpx', 'basic.jpf')
signature = box(b'jP  ', b'\r\n\x87\n')
# Minimal synthetic JPM / MJ2 wrappers: enough box structure to carry the first codestream.
write('basic.jpm', signature + box(b'ftyp', b'jpm \x00\x00\x00\x00jpm ') + box(b'jp2c', j2k))
write('basic.mj2', signature + box(b'ftyp', b'mjp2\x00\x00\x00\x00mjp2') + box(b'mdat', box(b'jp2c', j2k)))

# ---- Malformed inputs (must fail cleanly) -------------------------------------------------------
avif = open(os.path.join(OUT, 'basic.avif'), 'rb').read()
write('truncated.avif', avif[:len(avif) * 2 // 3])
write('garbage.heic', avif[:32] + bytes((i * 37 + 11) & 0xFF for i in range(512)))
write('garbage.jxl', b'\xff\x0a' + bytes((i * 91 + 7) & 0xFF for i in range(256)))
write('truncated.j2k', j2k[:len(j2k) // 2])
write('garbage.jp2', signature + box(b'ftyp', b'jp2 \x00\x00\x00\x00jp2 ') + bytes(range(256)) * 2)
print('done', sys.version.split()[0])
