# Glide

**A niche, lightweight, highly configurable image viewer for Windows.**

> **Current release:** Alpha 0.12107  
> **Platform:** Windows x64  
> **License:** MIT for Glide's original source; bundled third-party codecs retain their own licenses.

Glide exists for people who want an image viewer to behave like a fast, direct tool rather than a photo-management application.

It opens quickly, gets out of the way, and then goes much further than a basic viewer when you want it to: drag almost anywhere to move the window, draw a rectangle and click it to zoom exactly there, remap mouse and keyboard behaviour in detail, browse folders in tabs, detach tabs into windows, use slideshows, overlays, searchable settings, sibling-folder navigation, extensive status controls and a very broad image-format pipeline.

![Glide main interface](docs/screenshots/main-interface.jpg)

The interface is deliberately compact. The image remains the centre of attention; most advanced behaviour is available through direct gestures, the status bar, tabs, context actions and searchable Settings.

## Why another image viewer?

Windows already includes an image viewer. That is not the same thing as saying every image-viewing workflow is solved.

The built-in viewer is designed for the general Windows audience. Glide is deliberately more specialized. It is aimed at users who care about **speed, low overhead, keyboard/mouse control, direct manipulation and unusual levels of configurability**.

That niche matters. A few examples:

- **Select-to-zoom:** drag a rectangle over any part of an image, then click inside it to zoom precisely to that region.
- **Move the window from empty space:** the window itself can be dragged from convenient background areas rather than forcing you to target a small title-bar region.
- **Deep input remapping:** clicks, double-clicks, drags, wheel gestures, fullscreen gestures, selection gestures and overlay gestures can all be assigned different actions.
- **Searchable settings:** type what you are looking for instead of hunting through pages.
- **Fast browse behaviour:** rapid navigation uses previews, caching and predictive prefetching so large folders feel immediate.
- **Tabbed workspace:** Home, image and folder/browser tabs can coexist; tabs can be duplicated, restored and detached.
- **Sibling-folder traversal:** move through adjacent folders without repeatedly reopening the file picker.
- **Window-in-window overlays:** place reference images above the current image and control them independently.
- **Extremely broad format handling:** Glide registers 196 image-related suffixes and uses several decoding layers so it can cover far more than the usual JPEG/PNG set.
- **Compact native design:** Win32, Direct2D, DirectWrite and WIC rather than a browser runtime or heavyweight cross-platform shell.

So yes, Glide reinvents part of a wheel. It does so intentionally, because a wheel optimized for a different vehicle is still a different product.

## What Glide is

Glide is a fast Windows image viewer with an unusually flexible interaction model. It is **not** a photo library, RAW-development suite, cloud service or image editor.

The project prioritizes:

- fast first image;
- smooth navigation through large folders;
- a small native UI;
- configurable mouse/keyboard behaviour;
- broad file-format coverage;
- deterministic, local operation;
- portable and installed use;
- no background service;
- no runtime account or cloud dependency.

## Highlights

### Fast image opening and rapid browsing

Glide separates fast preview work from final-quality refinement. During rapid navigation it can show a reduced preview, discard stale requests, use nearby-image caching and prefetch likely next images. When navigation settles, the full-quality image replaces the preview without changing the user's view geometry.

JPEG handling includes progressive-preview logic and reduced decode paths for speed. Cache and prefetch behaviour are configurable.

### Select a region, then zoom directly into it

A persistent selection rectangle is one of Glide's signature interactions:

1. drag over the region you care about;
2. release — the rectangle remains;
3. click inside the selection;
4. Glide zooms that exact image-space rectangle to the viewport.

The selection is stored in image coordinates, so it remains aligned through zooming, resizing and panning.

### Drag-friendly window behaviour

Glide can move the native window from convenient empty/background regions, not just the title bar. This is configurable through the gesture system.

Tabs can also be reordered and detached into their own Glide windows. The alpha currently prioritizes reliable physical mouse-hold semantics during content-tab tear-off; native-style edge placement is supported while full Windows Snap-preview behaviour is still being refined.

### Highly configurable mouse and fullscreen gestures

Glide exposes **24 independent gesture slots**, including:

- left/right/middle click in windowed mode;
- left/right/middle click in fullscreen;
- double-clicks;
- left/right/middle drag;
- empty-background drag;
- normal wheel and Ctrl+wheel;
- selection clicks;
- overlay clicks/wheel;
- empty-background double-click.

Each gesture can be assigned actions such as next/previous image, zoom, Fit, 100%, fullscreen, context menu, clear selection, information, Settings, pan, create selection, move the native window, reset overlay zoom or bring an overlay forward.

This makes Glide useful for people with very different browsing habits without requiring separate builds.

### Tabs and workspace

Glide supports:

- Home tabs;
- image tabs;
- folder/browser tabs;
- new tab;
- close tab;
- restore closed tab;
- duplicate tab;
- next/previous tab;
- direct tab selection;
- drag/reorder;
- detach into another Glide window;
- attach/move tabs between Glide windows;
- configurable tab width and overflow behaviour.

The workspace is intentionally closer to a fast "viewer + lightweight folder navigator" than a full file manager.

### Fullscreen

Fullscreen is designed for rapid image review:

- borderless fullscreen;
- keyboard and mouse navigation;
- configurable fullscreen wheel/click behaviour;
- optional cursor auto-hide;
- reliable return to the previous window state;
- slideshow integration;
- Escape hierarchy that can stop slideshow, leave fullscreen or follow configured close behaviour.

### Slideshow

The slideshow has its own configurable interval, looping and folder-continuation options. It integrates with fullscreen and restores the pre-slideshow state when stopped.

### Window-in-window reference images

You can add floating reference-image overlays over the main viewer:

- multiple overlays;
- select and move;
- bring to front;
- close / clear;
- adjustable opacity;
- content-only zoom;
- keyboard `+` / `-` zoom;
- Ctrl+wheel zoom;
- configurable zoom step.

The overlay frame and the image zoom are intentionally separate: zooming the overlay image does not unexpectedly resize the frame.

### Text overlay

Glide can show a customizable text overlay over the image. By default it can display the current image index, for example:

`[3/82]`

Opacity, text colour, position and related styling are configurable.

### Status bar

The status bar can expose compact controls and current-image statistics such as:

- picture index;
- resolution;
- zoom percentage;
- file size;
- format;
- preview state;
- navigation;
- Fit / Fit Width / Fit Height / 100%;
- slideshow;
- image information;
- Home;
- overlays.

Individual groups can be shown or hidden.

### Sibling-folder navigation

Glide can navigate folders adjacent to the current folder and optionally:

- skip folders with no supported images;
- wrap at parent-directory ends;
- open first or last image on entry;
- include hidden sibling folders;
- continue automatically when reaching the end of the current image list;
- expose Previous Folder / Next Folder / Explore Parent controls.

### File operations

Common actions include:

- copy image pixels;
- copy the image file;
- copy filename;
- copy folder path;
- copy full path;
- rename;
- delete to Recycle Bin;
- open with configurable external programs;
- open containing folder;
- drag-and-drop file/folder opening;
- command-line opening;
- Windows Open With integration.

Three configurable external-program slots can be assigned their own hotkeys.

### Hotkeys

Hotkeys are centralized, searchable and user-configurable. The capture UI visibly enters a waiting state when you are assigning a new shortcut, and action hints/tooltips can reflect the current assignment rather than a stale hard-coded shortcut.

### Searchable Settings

The Settings window has a category rail and global search. Search is designed to answer "where is the setting for X?" without requiring users to remember which page contains it.

Settings support Apply / OK / Cancel semantics, dirty-state detection, profiles/presets, reset-to-defaults and theme-aware rendering.

## Image-format support

Glide has **196 registered suffixes** in the current source tree. Support is deliberately layered instead of forcing every format through one giant library.

### Direct/core paths

Windows Imaging Component (WIC) and compact Glide decoders cover common and simple formats such as:

JPEG, PNG, BMP/DIB, GIF, TIFF, ICO/CUR, JPEG XR, PSD composite, TGA, PNM/PPM/PGM/PBM/PAM, QOI, PCX, Radiance HDR/RGBE, farbfeld, DDS, WBMP, PFM and XBM.

### Bundled native codec layer

The source includes pinned decoder sources for:

| Format family | Decoder |
|---|---|
| WebP | libwebp 1.6.0 |
| HEIF / HEIC / HIF | libheif 1.23.4 + libde265 1.1.1 |
| AVIF | libheif 1.23.4 + dav1d 1.5.1 |
| JPEG XL | libjxl 0.12.0 |
| JPEG 2000 | OpenJPEG 2.5.3 |
| OpenEXR | TinyEXR 3.2.0 + miniz |

Supporting pinned sources include Highway, Brotli and a minimal skcms package.

### Windows/provider fallback

For specialist formats, Glide can also use Windows-installed codec/thumbnail/preview providers. This is how a lightweight viewer can participate in formats associated with design, camera, medical, microscopy, scientific and CAD applications without permanently bundling every heavyweight SDK.

That means **registered is not identical to guaranteed built-in decode**. Some specialist formats require an appropriate Windows codec or shell provider to be installed. Glide rejects generic file icons rather than pretending an icon is a successful image decode.

See [SUPPORTED_FORMATS.md](SUPPORTED_FORMATS.md) for the complete registered suffix list and decoder model.

### Animation note

GIF/TIFF multi-frame metadata is recognized. The native WebP path can decode the composed first frame; full animated WebP playback is not yet advertised in this alpha.

## Settings tour

The screenshots below use the final settled Settings frame for each page, avoiding duplicate intermediate captures.

### General

![General settings](docs/screenshots/settings-general.jpg)

General controls application-level behaviour: restoring the previous window position/size and maximized state, initial launch behaviour, whether closing the final viewer window exits the application, and whether Glide is remembered as the preferred Open/Open Folder location.

### Viewing & Interface — page 1

![Viewing and Interface page 1](docs/screenshots/settings-viewing-1.jpg)

This page controls the visual shell and primary viewing behaviour: whether window controls auto-hide, whether controls appear only when needed, whether the status bar is shown, whether clicking empty areas can move the native window, theme selection, title-bar/path presentation, fullscreen entry/exit behaviour and image-fit defaults.

### Viewing & Interface — page 2

![Viewing and Interface page 2](docs/screenshots/settings-viewing-2.jpg)

Zoom and navigation preferences live here: zoom step, fullscreen cursor auto-hide delay, theme/accent selection, whether opening a folder immediately displays the first image, and how Glide behaves after reaching the end of a folder.

### Viewing & Interface — page 3

![Viewing and Interface page 3](docs/screenshots/settings-viewing-3.jpg)

The text-overlay section controls the on-image information overlay. It can show a picture counter such as `[3/82]`, use a custom text pattern, choose a position, font size, bold state, colour and opacity, and enable/disable the overlay independently of the status bar.

### Mouse & Fullscreen — page 1

![Mouse and Fullscreen page 1](docs/screenshots/settings-mouse-fullscreen-1.jpg)

This begins the high-level mouse profile: double-click behaviour, fullscreen navigation convention, wheel behaviour, left/right drag defaults, click-inside-selection behaviour and other direct mouse mappings. The default profile preserves Glide's own interaction style, but these behaviours can be replaced.

### Mouse & Fullscreen — page 2

![Mouse and Fullscreen page 2](docs/screenshots/settings-mouse-fullscreen-2.jpg)

The advanced gesture matrix starts here. Individual windowed mouse gestures can be assigned separately rather than sharing one global "mouse mode". This allows, for example, wheel navigation while Ctrl+wheel zooms, or left drag selection while right drag pans.

### Mouse & Fullscreen — page 3

![Mouse and Fullscreen page 3](docs/screenshots/settings-mouse-fullscreen-3.jpg)

Fullscreen gestures are independently assignable. A fullscreen left click does not have to mean the same thing as a windowed left click; right, middle, wheel, Ctrl+wheel and background actions all have separate slots.

### Mouse & Fullscreen — page 4

![Mouse and Fullscreen page 4](docs/screenshots/settings-mouse-fullscreen-4.jpg)

Selection and overlay-specific gestures complete the matrix. Actions can change when clicking inside an active selection or when interacting with a Window-in-Window overlay. This is also where empty-background double-click behaviour can be assigned.

### Performance & Startup — page 1

![Performance page 1](docs/screenshots/settings-performance-1.jpg)

Performance controls determine how aggressively Glide trades preview speed against final quality. Options cover initial image quality, rapid-navigation preview behaviour, progressive-JPEG strategy, predictive prefetch and cache-related behaviour. The goal is immediate browsing without permanently lowering final render quality.

### Performance & Startup — page 2

![Performance page 2](docs/screenshots/settings-performance-2.jpg)

Fine tuning includes decoded-cache capacity, rapid-preview target resolution and the delay before a full-quality refinement replaces a temporary preview. These controls are mainly for users who browse very large images or want to tune RAM/latency trade-offs.

### Status Bar — page 1

![Status Bar page 1](docs/screenshots/settings-status-bar-1.jpg)

Each status-bar control can be exposed or hidden. The page includes navigation controls and quick viewer actions such as zoom/Fit modes, slideshow, information, Home, collapse/expand behaviour and overlay access.

### Status Bar — page 2

![Status Bar page 2](docs/screenshots/settings-status-bar-2.jpg)

Image statistics are independently configurable. Glide can show or suppress the current picture position, pixel resolution, zoom percentage, file size, image format and preview/refinement state.

### Slideshow

![Slideshow settings](docs/screenshots/settings-slideshow.jpg)

Slideshow settings control image interval, looping and whether playback continues across adjacent folders. Slideshow integrates with fullscreen and is designed to restore the exact pre-slideshow window state when stopped.

### Tabs & Workspace — page 1

![Tabs and Workspace page 1](docs/screenshots/settings-tabs-workspace-1.jpg)

Tab geometry and core workspace behaviour are configurable: minimum/maximum tab width, restoring previously closed tabs, closing the source window when its final tab is detached, tab-strip behaviour, overflow/navigation controls and related tab-management preferences.

### Tabs & Workspace — page 2

![Tabs and Workspace page 2](docs/screenshots/settings-tabs-workspace-2.jpg)

Folder-workspace options control adjacent-folder traversal and browser-tab behaviour: skipping empty sibling folders, wrapping at directory ends, first/last image choice, hidden folders, folder tooltips, parent-folder exploration and automatic folder continuation.

### Window in Window

![Window in Window settings](docs/screenshots/settings-window-in-window.jpg)

Reference-image overlays can be enabled and tuned here. Controls include default overlay opacity, remembering overlay zoom in saved layouts, highlight/accent behaviour, overlay zoom step and gesture behaviour. Overlay zoom affects the image content rather than resizing the frame.

### Windows Integration

![Windows Integration settings](docs/screenshots/settings-windows-integration.jpg)

Windows integration can register Glide in Open With / Default Apps, clean its registration, and configure three external-program slots. Each external program can receive the current image and can be invoked by a configurable hotkey.

### Profiles & Presets

![Profiles and Presets](docs/screenshots/settings-profiles-presets.jpg)

Profiles let users keep different Glide behaviours without manually rebuilding dozens of settings. Presets provide coherent starting points; user profiles can be saved, loaded, imported and exported.

### Hotkeys

![Hotkey settings](docs/screenshots/settings-hotkeys.jpg)

The hotkey table presents actions, current assignments and categories in one searchable list. Shortcuts can be changed, added, removed or reset without editing configuration files.

### Settings search

![Settings search](docs/screenshots/settings-search.jpg)

The search box filters settings across categories. The screenshot demonstrates searching for hotkey-related controls and surfacing the relevant actions directly, avoiding page-by-page hunting.

### Diagnostics

![Diagnostics settings](docs/screenshots/settings-diagnostics.jpg)

Glide includes a built-in automated diagnostics system. It can run a broad regression suite and export a compact snapshot for troubleshooting. Tests cover formats, decode behaviour, performance, window state and UI integrity, using bundled deterministic fixtures.

## Building from source

The current source build is intentionally Windows-native.

### Requirements

- Windows 10/11 x64
- Visual Studio 2022 or newer Build Tools with **Desktop development with C++**
- Windows SDK / `rc.exe`
- CMake
- Inno Setup 7 only if building the installer

### Build

From a Visual Studio Developer Command Prompt:

```bat
build.cmd
```

The build script:

1. verifies and builds the pinned offline codec layer;
2. compiles Glide with MSVC;
3. embeds resources;
4. links the x64 executable;
5. packages the portable runtime under `dist\`;
6. keeps diagnostic fixtures available for the built-in test suite.

Builds do not need to download codec source code: the pinned source archives and SHA-256 manifest are included in the repository.

For installer details see [BUILDING.md](BUILDING.md).

## Repository layout

```text
Glide/
├─ main.cpp                       main application/window orchestration
├─ image_decode*.cpp/.h           WIC + compact decoder layer
├─ image_cache.*                  decoded image cache
├─ image_prefetch.*               predictive prefetch
├─ input_hotkeys.*                hotkey infrastructure
├─ overlay_state.*                Window-in-Window overlay state
├─ platform_shell.*               Windows shell integration
├─ viewer_view_state.*            viewer geometry/state helpers
├─ ui_settings_*                  Settings shell/layout
├─ diagnostic_harness.*           automated regression harness
├─ image_formats.h                canonical extension registry
├─ codec_bridge/                  native codec bridge
├─ native_codecs/                 CMake integration for bundled codecs
├─ codec_sources/                 pinned offline source archives + hashes
├─ diagnostic_fixtures/           deterministic test images
├─ installer/                     Inno Setup definition and associations
├─ third_party/licenses/          third-party license texts
└─ docs/screenshots/              public feature/settings screenshots
```

## Privacy and networking

Glide is a local desktop viewer. It does not require an account or cloud connection to view images. The release build does not need to download codecs at runtime.

## Alpha status / current limitations

This is an **alpha**. Bugs and rough edges are expected.

Known limitations include:

- full animated WebP playback is not yet claimed;
- some specialist registered formats rely on Windows or application-provided thumbnail/preview handlers;
- content-tab tear-off currently prioritizes reliable physical mouse-hold behaviour; complete native Windows Snap Preview parity is still being refined;
- shell icon presentation may be affected by Windows icon caching on some builds/paths.

If you hit a reproducible problem, please open an issue with the format, steps and—when useful—the built-in diagnostic report.

## Contributing

Bug reports and focused pull requests are welcome. Glide is deliberately niche, so contributions should preserve its defining qualities: **fast startup, small footprint, direct interaction, predictable behaviour and configurability without framework bloat**.

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Glide's original source code is released under the [MIT License](LICENSE).

Bundled third-party codecs and support libraries retain their own licenses. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and `third_party/licenses/`.
