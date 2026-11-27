r"""Regenerates Glide's icons from the source art (round 4 I1/I2).

  python tools/generate-icons.py                 # app icon + file-type icons
  python tools/generate-icons.py --file-types    # file-type icons only (run by the Glide.App build when stale)
  python tools/generate-icons.py --preview-dir DIR   # also write PNG previews for review

Source: src/Glide.App/Assets/GlideIcon.png (512 px tile art).
Outputs:
  src/Glide.App/Assets/Glide.ico                 app icon, tile almost edge to edge (was ~6-9 % padding per side)
                                                 and a larger glyph, so it matches other taskbar icons in size
  src/Glide.App/FileTypeIcons/<ext>.ico          per-extension document icons: white page, coloured band with the
                                                 extension ("PNG", "JPG"), small Glide mark; copied next to
                                                 Glide.exe as FileTypes\<ext>.ico and used as DefaultIcon
Needs Pillow. Every frame size of the old .ico is kept: 16 20 24 32 40 48 64 128 256.
"""
import argparse
import io
import os
import struct
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "src", "Glide.App", "Assets", "GlideIcon.png")
APP_ICO = os.path.join(ROOT, "src", "Glide.App", "Assets", "Glide.ico")
FILE_TYPE_DIR = os.path.join(ROOT, "src", "Glide.App", "FileTypeIcons")
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
MASTER = 1024
# File-type icon frames (size, palette colours or 0 for truecolour). Explorer scales from the nearest frame.
GENERIC_ICON = "generic.ico"
FILE_TYPE_LAYERS = [(16, 0), (24, 0), (32, 0), (48, 0), (256, 64)]
# Less-known extensions get a minimal set (Windows upscales it for huge views); the common ones keep the full set.
RARE_LAYERS = [(16, 0), (32, 64), (48, 64)]
COMMON = set("jpg jpeg jpe jfif png apng gif webp heic heif hif avif jxl bmp dib ico tif tiff svg svgz psd "
             "dng cr2 cr3 crw nef nrw arw orf raf rw2 pef srw".split())
TILE = (27, 31, 38)

# Extension -> (label, band colour). Families share a colour so a folder of mixed files reads at a glance.
# The extension set itself is read from ImageFormatRegistry.cs (DeclaredExtensions), so a new format gets an icon
# the next time this runs, and a test fails until it does. Compound suffixes (.nii.gz, .ome.tif) are skipped: the
# shell only ever sees the last dot (.gz, .tif), so they cannot own an icon.
JPEG = (232, 96, 60)
PNG = (46, 160, 214)
GIF = (156, 89, 209)
WEB = (39, 174, 96)
HEIF = (230, 160, 30)
TIFF = (90, 110, 140)
RAW = (120, 85, 60)
VEC = (220, 70, 140)
LAYERED = (96, 84, 196)
SCIENCE = (24, 130, 130)
TEXTURE = (160, 120, 40)
MISC = (70, 80, 95)
FAMILIES = [
    (JPEG, "jpg jpeg jpe jfif jif jfi pjpeg pjpg mpo jps pns"),
    (PNG, "png apng mng jng"),
    (GIF, "gif"),
    (WEB, "webp avif avifs jxl"),
    (HEIF, "heic heif heics heifs hif"),
    (TIFF, "tif tiff btf jxr wdp hdp jp2 j2k j2c jpc jpx jpf jpm mj2"),
    (VEC, "svg svgz eps epsf ai pdf emf wmf emz wmz cdr cmx cpt dwg dxf skp"),
    (LAYERED, "psd psb pdd xcf ora kra afphoto afdesign afpub clip csp pdn psp pspimage"),
    (SCIENCE, "dcm dicom ima nii mha mhd nrrd ndpi svs vms vmu scn mrxs bif czi lif lsm vsi fits fit fts"),
    (TEXTURE, "dds ktx ktx2 pvr astc basis tex vtf wal spr"),
    (RAW, "dng cr2 cr3 crw nef nrw arw srf sr2 raf orf ori rw2 rwl pef ptx 3fr fff iiq cap eip mef mos mrw x3f erf kdc "
           "dcr k25 bay srw rwz gpr mdc raw r3d ari cinema dcs drf dsc r2d rw1"),
]
FAMILY_OF = {ext: colour for colour, names in FAMILIES for ext in names.split()}


def declared_extensions():
    """Every single-dot extension in ImageFormatRegistry.DeclaredExtensions, in declaration order."""
    path = os.path.join(ROOT, "src", "Glide.Core", "ImageFormatRegistry.cs")
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    block = text[text.index('DeclaredExtensions = """') + len('DeclaredExtensions = """'):]
    block = block[:block.index('"""')]
    found = []
    for item in block.replace(chr(10), ",").split(","):
        item = item.strip().lower()
        if item.startswith(".") and item.count(".") == 1 and item not in found:
            found.append(item)
    return found


def build_file_types():
    return {ext: (ext[1:].upper(), FAMILY_OF.get(ext[1:], MISC)) for ext in declared_extensions()}


FILE_TYPES = build_file_types()


def font(px):
    for name in ("segoeuib.ttf", "arialbd.ttf", "DejaVuSans-Bold.ttf"):
        for folder in (os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts"), ""):
            try:
                return ImageFont.truetype(os.path.join(folder, name) if folder else name, px)
            except OSError:
                pass
    return ImageFont.load_default()


def glyph_layer(source):
    """The white/cyan picture glyph of the source tile, as its own RGBA layer (tile colour removed)."""
    glyph = Image.new("RGBA", source.size, (0, 0, 0, 0))
    src = source.load()
    dst = glyph.load()
    for y in range(source.height):
        for x in range(source.width):
            r, g, b, a = src[x, y]
            if a < 250:
                continue
            dist = max(abs(r - TILE[0]), abs(g - TILE[1]), abs(b - TILE[2]))
            alpha = min(1.0, dist / 120.0)
            if alpha <= 0.02:
                continue
            # Un-blend from the tile colour so antialiased edges keep the glyph colour.
            col = tuple(int(max(0, min(255, TILE[i] + ((r, g, b)[i] - TILE[i]) / alpha))) for i in range(3))
            dst[x, y] = col + (int(alpha * 255),)
    return glyph


def app_master(source):
    """Tile 1.5 % from the edge (was 6.4 %) with the glyph 12 % larger inside it."""
    ss = MASTER
    img = Image.new("RGBA", (ss, ss), (0, 0, 0, 0))
    margin = round(ss * 0.015)
    tile_size = ss - 2 * margin
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((margin, margin, ss - margin - 1, ss - margin - 1), radius=round(tile_size * 0.225), fill=TILE + (255,))
    # Source tile spans 33..479 (446 px); map it onto the new tile, then scale the glyph up about the centre.
    tile_box = source.crop((33, 33, 479, 479))
    glyph = glyph_layer(tile_box)
    scaled = round(tile_size * 1.12)
    glyph = glyph.resize((scaled, scaled), Image.LANCZOS)
    offset = (ss - scaled) // 2
    img.alpha_composite(glyph, (offset, offset))
    return img


def glide_mark(size):
    """Small Glide mark for file icons: the app tile shrunk."""
    return APP_MASTER_CACHE[0].resize((size, size), Image.LANCZOS)


def file_type_master(label, colour, detail=True):
    s = MASTER
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    left, right, top, bottom = round(s * 0.14), round(s * 0.86), round(s * 0.03), round(s * 0.97)
    fold = round(s * 0.22)
    page = [(left, top), (right - fold, top), (right, top + fold), (right, bottom), (left, bottom)]
    outline = (120, 126, 136, 255)
    d.polygon(page, fill=(250, 251, 252, 255))
    d.line(page + [page[0]], fill=outline, width=round(s * 0.018), joint="curve")
    d.polygon([(right - fold, top), (right - fold, top + fold), (right, top + fold)], fill=(214, 218, 224, 255), outline=outline)
    d.line([(right - fold, top), (right - fold, top + fold), (right, top + fold)], fill=outline, width=round(s * 0.018))
    # Coloured band across the lower page, overhanging the left edge like ImageGlass/PotPlayer type icons.
    band_top, band_bottom = round(s * 0.56), round(s * 0.86)
    d.rounded_rectangle((round(s * 0.04), band_top, round(s * 0.90), band_bottom), radius=round(s * 0.04), fill=colour + (255,))
    if detail:
        # Largest size that fits the band: 0.25 for short labels, shrinking for AFPHOTO / PSPIMAGE.
        limit = (round(s * 0.90) - round(s * 0.04)) * 0.9
        px = round(s * (0.25 if len(label) <= 3 else 0.205))
        f = font(px)
        tb = d.textbbox((0, 0), label, font=f)
        while tb[2] - tb[0] > limit and px > 20:
            px -= 8
            f = font(px)
            tb = d.textbbox((0, 0), label, font=f)
        cx = (round(s * 0.04) + round(s * 0.90)) / 2
        cy = (band_top + band_bottom) / 2
        d.text((cx - (tb[0] + tb[2]) / 2, cy - (tb[1] + tb[3]) / 2), label, font=f, fill=(255, 255, 255, 255))
    mark = glide_mark(round(s * 0.36))
    img.alpha_composite(mark, (round(s * 0.24), round(s * 0.13)))
    return img


def save_ico(master_for_size, path):
    frames = [master_for_size(size).resize((size, size), Image.LANCZOS) for size in SIZES]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    frames[-1].save(path, format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])


def png_bytes(image, colours):
    """Optimised PNG of one icon frame; colours > 0 quantises to an 8-bit palette with alpha (flat art, no visible loss)."""
    if colours:
        # Transparent pixels carry the outline grey, so the octree does not blend black into the antialiased edge.
        grey = Image.new("RGBA", image.size, (120, 126, 136, 0))
        grey.alpha_composite(image)
        image = grey.quantize(colors=colours, method=Image.FASTOCTREE, dither=Image.NONE)
    buffer = io.BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def save_compact_ico(master_for_size, path, layers):
    """File-type icon: fewer frames (Explorer scales between them) and palette PNGs for the large ones."""
    entries = []
    for size, colours in layers:
        frame = master_for_size(size).resize((size, size), Image.LANCZOS)
        entries.append((size, png_bytes(frame, colours)))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    directory = b""
    for size, data in entries:
        directory += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as handle:
        handle.write(header + directory + b"".join(data for _, data in entries))


APP_MASTER_CACHE = []


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--file-types", action="store_true", help="only the per-extension icons")
    parser.add_argument("--preview-dir")
    args = parser.parse_args()

    source = Image.open(SOURCE).convert("RGBA")
    APP_MASTER_CACHE.append(app_master(source))

    if not args.file_types:
        save_ico(lambda size: APP_MASTER_CACHE[0], APP_ICO)
    for ext, (label, colour) in FILE_TYPES.items():
        # The label is unreadable below 32 px: the band colour and the mark carry the type there.
        masters = {True: file_type_master(label, colour, True), False: file_type_master(label, colour, False)}
        save_compact_ico(lambda size: masters[size >= 32], os.path.join(FILE_TYPE_DIR, ext.lstrip(".") + ".ico"),
                         FILE_TYPE_LAYERS if ext.lstrip(".") in COMMON else RARE_LAYERS)
    # generic.ico: the same page without a label, for Applications\Glide.exe (the class Windows uses when the user picked
    # Glide through "Open with" instead of a per-extension ProgID: it cannot be per extension).
    generic = file_type_master("", MISC, False)
    save_compact_ico(lambda size: generic, os.path.join(FILE_TYPE_DIR, GENERIC_ICON), FILE_TYPE_LAYERS)
    with open(os.path.join(FILE_TYPE_DIR, "stamp.txt"), "w", encoding="ascii") as stamp:
        stamp.write(" ".join(sorted(FILE_TYPES)) + "\n")

    if args.preview_dir:
        write_previews(source, args.preview_dir)
    print(f"Icons written: {APP_ICO if not args.file_types else '(app icon unchanged)'}, {len(FILE_TYPES)} file types in {FILE_TYPE_DIR}")


def write_previews(source, out):
    os.makedirs(out, exist_ok=True)
    old = Image.open(os.path.join(ROOT, "src", "Glide.App", "Assets", "GlideIcon.png")).convert("RGBA")
    taskbar = (32, 32, 36)
    sheet = Image.new("RGBA", (760, 300), taskbar + (255,))
    d = ImageDraw.Draw(sheet)
    f = font(16)
    d.text((16, 10), "Before (old art, taskbar sizes 24 / 32 / 48 / 128)", font=f, fill=(230, 230, 230, 255))
    d.text((16, 160), "After (regenerated Glide.ico)", font=f, fill=(230, 230, 230, 255))
    new_ico = Image.open(APP_ICO)
    x = 16
    for size in (24, 32, 48, 128):
        old_frame = old.resize((size, size), Image.LANCZOS)
        new_ico.size = (size, size)
        new_frame = new_ico.copy().convert("RGBA")
        sheet.alpha_composite(old_frame, (x, 36))
        sheet.alpha_composite(new_frame, (x, 186))
        x += size + 40
    sheet.save(os.path.join(out, "app-icon-before-after.png"))

    shown = [".jpg", ".png", ".gif", ".webp", ".heic", ".tiff", ".svg", ".bmp", ".avif", ".dng", ".psd", ".ico"]
    cols = len(shown) // 2
    grid = Image.new("RGBA", (cols * 150 + 20, 2 * 230 + 60), (243, 243, 243, 255))
    g = ImageDraw.Draw(grid)
    g.text((10, 8), "File-type icons (Explorer large icons 96 px, details 32 px, list 16 px)", font=font(15), fill=(30, 30, 30, 255))
    for i, ext in enumerate(shown):
        ico = Image.open(os.path.join(FILE_TYPE_DIR, ext.lstrip(".") + ".ico"))
        ico.size = (256, 256)
        big = ico.copy().convert("RGBA").resize((96, 96), Image.LANCZOS)
        cx = 20 + (i % cols) * 150
        cy = 40 + (i // cols) * 230
        grid.alpha_composite(big, (cx + 10, cy))
        for j, size in enumerate((32, 16)):
            ico.size = (size, size)
            grid.alpha_composite(ico.copy().convert("RGBA"), (cx + 10 + j * 44, cy + 110))
        g.text((cx + 10, cy + 150), "photo" + ext, font=font(13), fill=(30, 30, 30, 255))
    grid.save(os.path.join(out, "file-type-icons.png"))


if __name__ == "__main__":
    sys.exit(main())
