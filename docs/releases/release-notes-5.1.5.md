# Glide 5.1.5

Released 2026-11-21. A small follow-up to 5.1.4: Explorer thumbnails keep transparency, the installer lets you choose Glide's or Windows' thumbnails, and the slideshow timer restarts when you skip. Settings from 5.1.x load unchanged. The full list is in [CHANGELOG.md](../../CHANGELOG.md).

## Highlights

- **Choose your Explorer thumbnails at install**: a new *Use Glide's thumbnails in File Explorer (recommended)* checkbox, ticked by default and remembered for upgrades. Untick it to keep Windows' own thumbnails (an earlier Glide registration is removed). Silent installs can opt out with `/MERGETASKS="!thumbnails"`.
- **Thumbnails refresh on every install**: with Glide's thumbnails on, the installer clears the thumbnail cache and tells Explorer, so new thumbnails show straight away without visiting Settings.

## Fixes

- **Transparent thumbnails**: PNG, WebP, GIF, AVIF, HEIF, JPEG XL, TIFF, ICO, 32-bit BMP/TGA, PSD and other formats with transparency now show as transparent in File Explorer instead of on a white square.
- **Stale thumbnail provider**: installing now removes a leftover per-user registration from another Glide copy (portable or development build) that made Explorer keep using an old thumbnail provider.
- **Slideshow timer**: skipping to the next or previous picture restarts the full interval from when the new picture appears. A paused slideshow stays paused.

## Under the hood

- The shell thumbnail provider returns premultiplied ARGB instead of flattening onto white.
- New command-line switch `--refresh-thumbnails [--disabled]`, shared with Settings > Refresh thumbnails (`ThumbnailCacheRefresher`).

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.1.5-Portable.zip`: portable build
- `Glide.Setup.sha256`: checksum (SHA256 `3a9438868e61f1eb89024b77236938a431249ce38f6e83c3bbd5e2434483dcff`)
