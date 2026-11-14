# Glide 4.5.0

Adds Photoshop file support. Built and tested on Windows x64 (463/463 tests passing).

## New

### Photoshop PSD / PSB files
`.psd`, `.psb` (large document format) and `.pdd` files now open in Glide, and they get thumbnails both inside Glide and in Windows Explorer.

- Glide shows the flattened image that Photoshop stores inside the file, so files open quickly without rebuilding layers.
- RGB, CMYK, grayscale, indexed colour, bitmap, duotone and Lab images are supported.
- 8, 16 and 32 bits per channel are supported, and transparent images keep their transparency.
- Damaged or truncated files are rejected safely instead of crashing the viewer or Explorer.

**After upgrading:** re-register Explorer thumbnails in Settings so Explorer starts using Glide for `.psd` and `.psb`. If old blank icons remain, clear the Windows thumbnail cache (Disk Cleanup → Thumbnails).

## Known limitations
- Layers are not shown individually. If a PSD was saved with "Maximize Compatibility" turned off, the stored flattened image can be out of date, and that is what Glide shows.

## Downloads
- `Glide.Setup.exe`: installer (requires admin)
- `Glide-4.5.0-Portable.zip`: portable build
- `Glide.Setup.sha256`: installer checksum
