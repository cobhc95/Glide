# Glide 4.4.0

New features plus a security update for the Explorer thumbnail provider. Built and tested on Windows x64 (463/463 tests passing).

> **Security:** this release fixes a memory-safety bug in Glide's Explorer thumbnail provider. On earlier versions, a crafted `.tga` file could trigger it just by opening its folder in Explorer. All users should update.

## New

### Live folder refresh
When files are downloaded, copied, moved or renamed into the folder you're viewing, they show up right away in next/previous navigation and the image counter. You don't have to reopen the folder. Deleted files drop out, and the current image stays selected.

- Partial downloads (`.crdownload`, `.part`, `.tmp`, `.download`) are ignored until they finish.
- A file is added only once it can be opened.
- New files are inserted in sorted order without rescanning the folder.

### Save and Save As
- **Save** (`Ctrl+S`) overwrites the image in its original format, after asking first.
- **Save As** (`Ctrl+Alt+S`) lets you choose JPEG, PNG, BMP or TIFF.
- Rotation and flips are saved into the image.
- The file is written to a temporary file first, so a failed save never damages the original.
- Both commands are in the right-click menu.

### Fullscreen cursor auto-hide
- In fullscreen, the mouse cursor hides after 3 seconds without movement.
- It comes back only after about a second of continuous movement, so a nudge of the desk, a click or a key press won't show it.
- It stays visible over controls and menus.

## Security and thumbnail fixes (Explorer)
- **TGA:** fixed a stack buffer overflow caused by an unchecked bits-per-pixel value. Also fixed an out-of-bounds read in colour-mapped images and incorrect colour-map parsing.
- **SGI:** fixed an integer overflow in the compressed-data bounds check, plus the table position and layout. Compressed SGI files now produce correct thumbnails.
- **PNM / PBM / PGM / PPM / PAM:** thumbnails now decode correctly. Before, almost all of these rendered wrong.
- **PCX:** 24-bit images now decode in colour.

## Known limitations
- Save As cannot write WebP. Saving a WebP, GIF or HEIC file opens Save As so you can pick another format.
- Saving does not keep EXIF or other metadata; the overwrite prompt warns about this.
- The thumbnail/folder panel doesn't refresh live yet. Only navigation and the counter update.

## Downloads
- `Glide.Setup.exe`: installer (requires admin)
- `Glide-4.4.0-Portable.zip`: portable build
- `Glide.Setup.sha256`: installer checksum
