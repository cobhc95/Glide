# Glide 4.6.0 release notes (draft)

Glide 4.6.0 adds video playback inside Window-in-Window overlays and makes the viewer easier to navigate and customise. This release includes changes made since 4.5.0, including work previously tracked as 4.5.1 and 4.5.2.

## Highlights

- **Video overlays:** Open or drop a supported video into an overlay. Play, pause, restart, move between videos, adjust volume, mute, loop and automatically play the next video. Media Foundation is supported with a bundled FFmpeg fallback. Videos are confined to overlays and do not enter image navigation.
- **Reorganised settings:** Eight groups replace the previous 14 categories. Search covers more on-screen text, ranks direct matches first and includes a shortcut filter. Settings updates are more responsive.
- **Adaptive controls:** The status bar rearranges into rows or an overflow menu as space gets tight. It adds a Tiny size and Save and Save As buttons; menus are grouped and use icons.
- **More viewing choices:** Choose image sort order, configure a selection buffer around the picture, select a slideshow click action and draw a custom area for animated overlays.

## Fixes and improvements

- Image navigation defaults to Explorer-style natural name ordering, with additional sorting options.
- Mouse-wheel navigation direction is corrected and can be reversed in settings.
- Image and folder drag and drop, title-bar double-click, normal-window restoration after fullscreen and launches after Speed Boost standby are fixed.
- PNG text and SVGZ decompression are bounded, malformed codec surfaces are checked, and startup, print preview and overlay rendering have been improved.

## Existing limitations

Save As supports JPEG, PNG, BMP and TIFF; it does not preserve EXIF or other metadata. Photoshop files display their stored flattened composite, which may be out of date if Maximize Compatibility was disabled when saving.

See [CHANGELOG.md](CHANGELOG.md) for the detailed list. These notes are prepared for 4.6.0; the release has not yet been published.
