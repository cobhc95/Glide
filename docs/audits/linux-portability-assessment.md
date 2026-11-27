# Linux portability assessment

Read-only assessment, 2026-11-18, against the 5.0.8 tree. No code was changed or built. Counts come from
`grep` over `src/` and `native/`; `Archive/` was excluded.

## Verdict

**A Linux port is feasible, and the core viewer is closer to portable than the "Windows-only" label suggests.**
The UI is Avalonia, every project targets plain `net10.0`, `Glide.Core` is almost free of Windows calls, the
Explorer tab is a managed Avalonia surface, and the optional codec ABI is already cross-platform. The Windows
dependence is concentrated at the edges:

- WIC decode and encode, plus Shell previews, in `Glide.Native`
- Media Foundation video
- the custom Win32 window frame
- Win32 layered overlay windows
- GDI+ printing
- registry-based shell integration

Every one of these has a known replacement. Two areas have no Linux equivalent and should be dropped:

- Explorer's Shell thumbnail provider. A freedesktop thumbnailer takes its place.
- Windows-specific window chrome: Snap Layouts, DWM cloak, Mica, and the dark caption.

| Scope | Effort (one engineer who knows the codebase) |
|---|---|
| MVP: core viewer, X11/XWayland, AppImage, no video, print or overlays | **14-18 person-weeks** |
| Near-parity: everything except the features Windows alone can provide | **32-40 person-weeks** |

These figures include roughly 2.5 weeks of seam work on Windows that pays off even if no Linux build is ever made.

**Top blockers, in order:**

1. **Decode and encode run through WIC.** This covers JPEG, PNG, TIFF and GIF saving, ICC/EXIF/XMP copying, TIFF
   and JPEG XR reading, and the Shell-preview fallback.
2. **Video uses Media Foundation.** The Linux replacement is libmpv, GStreamer or FFmpeg.
3. **The custom title bar and tab strip** rely on Win32 non-client hit-testing and Snap Layouts.
4. **Wayland.** Avalonia 11.3 has no native Wayland backend, so Glide would run through XWayland. That means
   blurry fractional scaling, and no global positioning for overlays or for dragging tabs between windows.
5. **Printing uses System.Drawing (GDI+).** That library is Windows-only from .NET 6 onward.

---

## 1. Inventory of Windows-only dependencies

### 1.1 P/Invoke declarations by DLL (`src/`, `DllImport` + `LibraryImport`)

| DLL | Declarations | Main users |
|---|---:|---|
| user32.dll | 79 | `WindowsBrowserFrameController` (29 total, all DLLs), `NativeOverlayWindowRenderer` (28), `ExplorerFileClipboard`, `WindowsClipboard`, `MainWindow.Tabs`, `ToolTipGuard`, `ModifierKeyState`, `WindowCloak` |
| Glide.Native (own C++) | 24 (22 distinct exports) | `IImageDecoderBackend`, `NativeImageEncoder`, `NativeImageProbe`, `NativeVideoPlayer` |
| shell32.dll | 14 | `ExplorerShell`, `WindowsKnownFolders`, `WindowsOpenWith`, `WindowsShellProperties`, `ShellItemNotifier`, `WindowsFileAssociationRegistration` |
| kernel32.dll | 14 | `DiagnosticRunner`, `DecodeMemoryGuard`, `ExplorerFileClipboard` (GlobalAlloc), `Program`, `WindowCloak` |
| gdi32.dll | 8 | `NativeOverlayWindowRenderer` (DIB sections, layered windows) |
| dwmapi.dll | 5 | `WindowCloak`, `PresentationFence`, frame and dark-mode attributes |
| winspool.drv | 3 | `PrintDriverProperties` |
| ole32.dll | 3 | `WindowsExplorerHost`, clipboard |
| avrt.dll | 3 | MMCSS thread priority (`NativeVideoPlayer` / overlay) |
| winmm.dll | 2 | `timeBeginPeriod` |
| shlwapi.dll | 2 | `StrCmpLogicalW` (`Glide.Core/ImageSort.cs`), path helpers |
| uxtheme.dll, comdlg32.dll | 1 each | dark menus, `WindowsColorDialog` |
| **Total** | **159** | 34 files |

Other Windows-only code:

- **Source-generated COM:** `WindowsExplorerHost` (ExplorerBrowser, 582 lines). This is dead code:
  `EnsureNativeExplorerHost()` has no callers, and the Explorer tab is the managed `BrowserSurface`. It can be
  deleted, which removes all COM interop from the app.
- **Registry:** 5 files under `Platform/`: `ThumbnailSettingsStore`, `WindowsFileAssociationRegistration`,
  `WindowsReset`, `WindowsStartupRegistration` and `WindowsThumbnailRegistration`.
- **`System.Drawing.Common`:** `Printing/PrintService.cs` and `PrintAcceptance.cs`.
- **`Microsoft.VisualBasic.FileIO`:** Recycle Bin deletion in `ExplorerFileOperations.DefaultRecycler`. It is
  already guarded by `IsWindows()` and returns `false` elsewhere.
- **Named `EventWaitHandle`:** the single-instance presence sentinel in `ExternalLaunchBroker`. .NET on Unix
  rejects named events, so this throws `PlatformNotSupportedException`. The named pipe itself works on Linux,
  where .NET backs it with a Unix domain socket.
- **Avalonia host:** `Glide.App.csproj` references `Avalonia.Win32` only, and `Program.cs` calls `.UseWin32()`
  with `Win32PlatformOptions`.
- **Runtime guards:** 104 `OperatingSystem.IsWindows()` checks across 45 files. Many paths therefore already
  degrade to a no-op off Windows, but nothing has ever been exercised that way.

### 1.2 Native C++ (`native/`, 7,182 lines)

| Component | Windows APIs | Portability |
|---|---|---|
| `Glide.Native/GlideNative.cpp` (1,373) | WIC (decode, bounded/transform decode, progressive levels, encode, metadata query writer), `IShellItemImageFactory` and the thumbcache (`SIIGBF_INCACHEONLY`), D3D11/DXGI | Not portable. Must be replaced. |
| `Glide.Native/GlideJpeg.cpp` (456) | libjpeg-turbo (TurboJPEG) | Portable with minor `wchar_t` path fixes. Keep it as the fast JPEG route on Linux. |
| `Glide.Native/GlideVideo.cpp` (481) | Media Foundation (`IMFMediaEngine`, `IMFSourceReader`) | Not portable. Must be replaced. |
| `Glide.Codecs/GlideCodecs.cpp` (1,140) | Only `CreateFileW`, `MultiByteToWideChar` and SEH `__try/__except` around decoder bodies | Portable. Libraries are libheif, dav1d, libde265, libjxl, OpenJPEG and libwebp; the ABI header already has a non-`_WIN32` branch. SEH needs a Linux strategy (see Risks). |
| `Glide.ShellThumbnail/*` (3,732) | `IThumbnailProvider` COM server | Windows-only by design. A freedesktop thumbnailer takes its place (section 3). |

`src/Glide.ThumbnailCodecs` (the NativeAOT `glidemanaged.dll`) is pure C# exporting the ABI. It can be
published as `libglidemanaged.so` without changes.

### 1.3 Windows behaviour by feature area

| Area | Windows mechanism | Files |
|---|---|---|
| Core decode | WIC via `Glide.Native` (JPEG, PNG/APNG, BMP, GIF, TIFF, ICO/CUR, JPEG XR, BC-compressed DDS, plus Store-extension HEIC/AVIF/RAW) | `IImageDecoderBackend.cs` (5 `IsWindows` guards; Skia fallback exists) |
| Shell preview fallback | `IShellItemImageFactory` for PDF, EPS/AI, Office, CAD and Cinema RAW; thumbcache for tab previews | `IImageDecoderBackend.cs`, `TabPreviewCache.cs` |
| Encode | WIC encoders for PNG, JPEG, BMP, TIFF, GIF and JPEG XR (HEIC/AVIF/JXL through Store extensions), with ICC/EXIF/XMP copied by the WIC metadata writer; ICO and OpenRaster embed PNGs made through WIC | `NativeImageEncoder.cs`, `Encoders/ImageFileEncoder.cs`, `Encoders/DocumentEncoders.cs` |
| Video | Media Foundation | `NativeVideoPlayer.cs`, overlay video |
| Window chrome | `WM_NCHITTEST` subclassing, Snap Layout contract, DWM attributes, cloak | `WindowsBrowserFrameController.cs` (841 lines), `WindowCloak.cs`, `MainWindow.Chrome.cs` |
| Overlays (window-in-window) | Worker-thread layered popup HWNDs with DIB sections | `NativeOverlayWindowRenderer.cs` (714 lines). Avalonia alternatives already exist: `CompositorOverlayRenderer`, `LegacyOverlaySurface`. |
| Tab drag between windows | `EnumThreadWindows`, cursor and window hit-testing | `MainWindow.Tabs.cs`, `TabDragController.cs` |
| Clipboard | `CF_DIB`/PNG image formats; `CF_HDROP` plus `Preferred DropEffect` for file copy and cut | `WindowsClipboard.cs`, `ExplorerFileClipboard.cs` |
| Explorer tab edges | Recycle Bin, known folders, Reveal in Explorer, Open With, Properties, `SHChangeNotify`, `StrCmpLogicalW` | `Explorer/*`, `Platform/WindowsKnownFolders.cs`, `WindowsOpenWith.cs`, `WindowsShellProperties.cs`, `ShellItemNotifier.cs` |
| File associations | Registry ProgIDs and Capabilities | `WindowsFileAssociationRegistration.cs`, `--register-file-associations` |
| Explorer thumbnails | COM `IThumbnailProvider` registration and a registry settings store | `WindowsThumbnailRegistration.cs`, `ThumbnailSettingsStore.cs`, `native/Glide.ShellThumbnail` |
| Start with Windows | HKCU Run key | `WindowsStartupRegistration.cs` |
| Taskbar identity | AppUserModelID | `WindowsTaskbarIdentity.cs` |
| Tray and Speed Boost | Avalonia `TrayIcon` (already portable), warm background process, named pipe plus named event | `App.axaml.cs`, `ExternalLaunchBroker.cs`, 20 files mention Speed Boost |
| Print | GDI+ `PrintDocument`, winspool `DocumentProperties`, DPI queries | `Printing/*` (2,349 lines; the layout engine and preview are portable) |
| Colour picker, message box | comdlg32 `ChooseColor`, `MessageBoxW` | `WindowsColorDialog.cs`, `Program.cs` |
| Reset | Registry cleanup | `WindowsReset.cs` |
| Timing and perf | `timeBeginPeriod`, MMCSS, `DwmFlush` fence, `GlobalMemoryStatusEx`, working set | `PresentationFence.cs`, `DecodeMemoryGuard.cs`, `DiagnosticRunner.cs` |
| Installer and update | Inno Setup; the GitHub update feed downloads the installer | `installer/Glide.iss`, `Updates/*` |
| Build | `build.cmd`, PowerShell scripts, MSVC/CMake, `tools/build-codecs.ps1` | `tools/*` (the Python audit scripts are portable) |

## 2. What Avalonia, SkiaSharp and .NET already provide on Linux

**Rendering and UI**
- Avalonia 11.3 runs on X11 natively and on Wayland through XWayland: windows, input, IME, DPI, fullscreen,
  composition and the compositor custom-visual APIs.
- The `CompositorOverlayRenderer` path is plain Avalonia.

**Images**
- SkiaSharp decodes JPEG, PNG (including APNG first frame), GIF, BMP, ICO, WBMP and WebP. `ThumbnailDecode` and
  the step-3 Skia fallback already use it.
- SkiaSharp encodes PNG, JPEG and WebP, and writes PDFs through `SKDocument`, which can drive printing.
- `Svg.Skia` handles SVG.
- About 19.5k lines of managed decoders are platform-neutral: RAW previews, EXR, PSD, scientific, layered-editor
  and texture formats.

**Platform services**
- `TrayIcon` is backed by the D-Bus StatusNotifierItem protocol. KDE supports it natively; GNOME needs the
  AppIndicator extension.
- Avalonia's clipboard handles text, plus custom MIME formats as `byte[]` on X11. Image and file-list support has
  to be built on those custom formats.
- `StorageProvider` file and folder pickers use the xdg-desktop-portal or GTK dialogs.
- Tmds.DBus ships with Avalonia's Linux backends. It can be reused for `org.freedesktop.FileManager1`, portals
  and notifications.

**.NET runtime**
- NativeAOT for linux-x64 and linux-arm64.
- `FileSystemWatcher` uses inotify.
- Named pipes are backed by Unix domain sockets, and `PipeOptions.CurrentUserOnly` works.
- `Environment.SpecialFolder` resolves to XDG directories: `LocalApplicationData` → `~/.local/share`,
  `MyPictures` → the XDG Pictures directory.

**Glide's own code**
- `NativeLibrary`-based codec provider loading (`NativeCodecProvider.cs`).
- `Glide.Core` uses only `StrCmpLogicalW` and one `IsWindows` check.
- The Explorer tab is a managed surface whose side effects already go through the replaceable
  `Explorer/ExplorerHost` seam.

## 3. Replacement options per area

| Area | Linux replacement | Recommendation |
|---|---|---|
| JPEG decode | Port `GlideJpeg.cpp` (libjpeg-turbo, DCT-scaled bounded decode) to `libglidenative.so`; fall back to Skia | Keep the turbo path: first-frame speed is a headline metric |
| PNG/GIF/BMP/ICO/WebP decode | Skia (already the fallback route) | Bounded decode via `SKCodec.GetScaledDimensions` and sampling |
| TIFF decode and encode | Add libtiff (with zlib, libdeflate, zstd, jpeg) to `glidecodecs` as an ABI v2 format | Needed: TIFF is a "core fast path" suffix |
| JPEG XR | jxrlib (dormant upstream) or drop | Drop on Linux: rare, and the format is effectively abandoned |
| BC-compressed DDS | Extend the existing managed `TextureBlockDecoders` | Small job, and removes a Windows dependency on Windows too |
| HEIC/AVIF/JXL/JP2 | Already in `glidecodecs`; rebuild as `.so` | Free once the native build is ported |
| Camera RAW (beyond the embedded preview) | LibRaw provider (already on the roadmap in `format-coverage.md`) | Optional. Linux users have no "WIC RAW extension" fallback |
| Shell preview fallback (PDF/EPS/Office/CAD) | Read the freedesktop thumbnail cache (`~/.cache/thumbnails/{normal,large,x-large,xx-large}/<md5(uri)>.png`); optionally poppler for PDF | Cache read-only, like Windows `SIIGBF_INCACHEONLY`; degrades to "no preview" |
| Encoders and metadata | Skia for PNG/JPEG/WebP; the existing `SimpleRasterEncoders` for BMP and others; libtiff for TIFF; a managed GIF quantiser; managed ICC/EXIF/XMP injection (JPEG APP1/APP2 segments, PNG `iCCP`/`eXIf`/`iTXt`, TIFF tags) | Writing metadata in managed code also makes Windows behaviour testable without WIC |
| Video | libmpv render API (OpenGL/software into a bitmap), GStreamer `appsink`, or FFmpeg (libavformat/libavcodec) decode to BGRA | libmpv is the least code. FFmpeg gives frame-exact control that matches `GlideVideoReadFrame`. Watch codec licensing (section 7). |
| Window chrome | `SystemDecorations.None` / `ExtendClientAreaToDecorationsHint`, with `BeginMoveDrag`/`BeginResizeDrag`, double-click maximise and our own resize grips | Two chrome policies behind one `IWindowFrame` seam |
| Overlays | `CompositorOverlayRenderer` or `LegacyOverlaySurface` in transparent topmost Avalonia windows; click-through via XShape input region (X11 only) | Degraded on Wayland: no absolute positioning or click-through |
| Tab drag between windows | `GlideWindowRegistry` plus `PointerEventArgs` screen points | Works on X11; restricted on Wayland/XWayland |
| Image clipboard | Avalonia `DataObject` with `image/png` as bytes | S |
| File copy and cut | `text/uri-list` plus `x-special/gnome-copied-files` (GNOME, Nautilus and Thunar) and `application/x-kde-cutselection` (KDE) | M, with per-file-manager testing |
| Recycle Bin | freedesktop Trash spec in managed code (`$XDG_DATA_HOME/Trash/{files,info}`, plus `$topdir/.Trash-$uid` on other mounts), or shell out to `gio trash` | Managed implementation: deterministic and testable |
| Known folders | `~/.config/user-dirs.dirs` (`XDG_*_DIR`) and `Environment.SpecialFolder` | S |
| Reveal in file manager | D-Bus `org.freedesktop.FileManager1.ShowItems`; fallback `xdg-open <parent>` | S |
| Open With chooser | xdg-desktop-portal `OpenURI` with `ask=true`; fallback `xdg-open` | S (the GNOME portal shows a chooser; KDE varies) |
| Properties dialog | Drop, or show Glide's own metadata panel | Drop |
| Natural sort | Managed `StrCmpLogicalW`-compatible comparer | S. It must match the existing sort on Windows too, with a parity test. |
| Single instance and Speed Boost | Keep the pipe. Replace the named event with an `flock` lock file in `$XDG_RUNTIME_DIR`. Run at login via `~/.config/autostart/glide.desktop`. | S-M. Speed Boost matters less (no "Explorer double-click latency" contract) but is cheap to keep. |
| File associations | `.desktop` file with `MimeType=` covering about 196 suffixes (shared-mime-info XML for the exotic ones); `xdg-mime default` when the user opts in | M (needs a MIME map generated from `ImageFormatRegistry`) |
| File-manager thumbnails | freedesktop `.thumbnailer` entry: `Exec=glide-thumbnailer -s %s %i %o`. It is a small NativeAOT CLI that reuses the `Glide.Imaging` decoders and `glidecodecs`. Read by Nautilus, Nemo, Caja and Thunar (tumbler); KDE Dolphin needs a KIO ThumbCreator plugin (C++/Qt) | M (L with KDE) |
| Taskbar identity | `WM_CLASS` / Wayland `app_id` = `.desktop` `StartupWMClass` | S |
| Colour picker, message box | Avalonia `ColorView`, plus an Avalonia dialog | S |
| Print | Layout and preview stay. Render each page with Skia into a PDF (`SKDocument.CreatePdf`). Submit via `lp`/IPP (CUPS); list printers via `lpstat -e` or libcups P/Invoke; paper size and DPI from `lpoptions`/IPP attributes. | M. The GTK print dialog is not reachable from Avalonia, so Glide's own dialog stays (it already exists). |
| Settings store, update feed | Already files and HTTP. Update: AppImage via zsync/AppImageUpdate; deb, rpm and Flatpak updates handled by the package manager (disable self-update) | S |
| Timing and perf | No-ops (`timeBeginPeriod`, MMCSS, DwmFlush); `/proc/self/status` and `/proc/meminfo` for diagnostics and the memory guard (or `GC.GetGCMemoryInfo`) | S |

## 4. Features dropped or degraded on Linux

**Dropped**
- Snap Layouts flyout, Mica/DWM dark caption, and window cloaking.
- The Windows Properties dialog.
- JPEG XR read and save.
- The Explorer thumbnail provider for Windows Explorer. Its replacement is the freedesktop thumbnailer;
  KDE coverage only arrives with a KIO plugin.
- WIC Store-extension formats (Cinema RAW `.r3d`/`.ari` and others) and the Shell preview route for
  PDF/EPS/Office/CAD. The latter only shows something if the freedesktop cache already holds a thumbnail.
- "Start with Windows" wording.

**Degraded**
- **Wayland / XWayland:** fractional scaling is blurry; overlay windows cannot be placed in absolute positions or
  made click-through; tab drag-out-and-reattach between windows is limited; there is no always-on-top guarantee.
- **Tray on GNOME** needs the AppIndicator extension.
- **File cut and paste** depends on the desktop environment.
- **"Open with" chooser** quality depends on the portal.
- **Printing** loses driver-specific property sheets.
- **Video codec coverage** depends on the libraries bundled or installed. HEVC and AAC under Flatpak need the
  ffmpeg-full extension.
- **Touchpad pinch and swipe** depend on Avalonia's XInput2 gesture support; verify against `Glide.Input` and
  `GestureCatalog`.

**Unchanged (managed today)**
- Tabs and sessions, navigation and history, settings and settings search, hotkeys and gestures.
- Slideshow, fullscreen, the zoom and pan viewport, and managed decoders for about 150 suffixes.
- Updates feed parsing and backup/reset of Glide's own data.

## 5. Packaging

| Format | Fit | Notes |
|---|---|---|
| **AppImage** | Best first target | One self-contained NativeAOT binary plus `libSkiaSharp.so`, `libHarfBuzzSharp.so`, `libglidenative.so`, `libglidecodecs.so`, `libglidemanaged.so` and `codecs.index.json`. Closest to the existing portable ZIP. Desktop integration (`.desktop`, MIME, thumbnailer) is opt-in from Settings, like today's `--register-*` commands. Build on an old glibc base (Ubuntu 22.04 or a manylinux-style container). |
| **Flatpak** (Flathub) | Second target; widest reach | The sandbox is the issue. A viewer that walks sibling folders and an Explorer tab that moves and deletes files need `--filesystem=home` (or `host`), which Flathub reviewers accept for file managers and viewers but question. Trash must use the portal or `--filesystem=xdg-data/Trash`. Thumbnailers cannot be installed from inside a Flatpak. The update feed must be disabled. |
| **.deb / .rpm** | Optional | Simple once the AppImage layout exists (nfpm or `dpkg-deb`). Lets the thumbnailer and `.desktop`/MIME files install system-wide. Shipping libheif/libde265 statically keeps the LGPL relink obligation that `codecs/README.md` already documents. |
| Snap | Skip | Little benefit over Flatpak for this audience. |

The Inno Setup installer, the registry-based reset and the Windows code-signing flow stay Windows-only. The update
service needs a per-package-kind policy: self-update for AppImage only.

## 6. Effort estimate by area

S is 1 week or less, M is 1-3 weeks, L is more than 3 weeks. Figures are person-weeks for one engineer who knows
the codebase, agent-assisted, including tests. Calendar time is longer, because Linux desktop QA cannot be
automated from this Windows host (see Risks).

| # | Area | Size | Person-weeks | MVP? |
|---|---|:-:|---:|:-:|
| 1 | Platform seams on Windows (`IPlatformServices` split: frame, clipboard, shell, trash, known folders, launch broker, print sink, video backend, decode backend selection); delete dead `WindowsExplorerHost` | M | 2-3 | yes |
| 2 | Build: conditional `Avalonia.X11` and Linux SkiaSharp/HarfBuzz native assets, `UsePlatformDetect`, exclude `Platform/Windows*` by RID, NativeAOT linux-x64, CMake for `libglidenative`/`libglidecodecs`, bash build script, Linux CI with headless tests | M | 2-3 | yes |
| 3 | Decode: libjpeg-turbo bridge on Linux, Skia bounded route, libtiff in `glidecodecs`, managed BC DDS, freedesktop thumbnail-cache fallback | M-L | 3-4 | yes (JPEG/PNG/GIF/WebP/TIFF) |
| 4 | Encode plus managed ICC/EXIF/XMP writer, GIF quantiser, ICO/ORA PNG via Skia | M | 2-3 | partial (PNG/JPEG/WebP) |
| 5 | `glidecodecs`/`glidemanaged` as `.so` (SEH replacement, UTF-8 paths, `.so` names in `codecs.index.json`, SHA pinning) | M | 1.5-2 | yes |
| 6 | Window chrome and title-bar tab strip on Linux | M | 2 | yes (basic) |
| 7 | Clipboard: images, file copy/cut | S-M | 1-1.5 | images only |
| 8 | Explorer tab edges: Trash, XDG dirs, reveal, open-with, natural sort, inotify limits | M | 2-2.5 | yes |
| 9 | Single instance, Speed Boost, autostart, tray, taskbar identity | S-M | 1-1.5 | yes |
| 10 | Video backend (libmpv or FFmpeg) for the viewer and overlay video | L | 3-4 | no |
| 11 | Overlays on Linux (compositor renderer, transparency, X11 click-through, Wayland fallback) | M-L | 2-3 | no |
| 12 | Tab drag between windows on Linux | S-M | 1 | no |
| 13 | Print via Skia PDF and CUPS | M | 2-3 | no |
| 14 | Desktop integration: `.desktop` and MIME map from `ImageFormatRegistry`, freedesktop thumbnailer CLI (KIO plugin +2), reset/uninstall | M | 2-3 | `.desktop` only |
| 15 | Settings: platform availability per entry, hiding Windows-only settings on Linux, audit-tool updates | S | 0.5-1 | yes |
| 16 | Packaging: AppImage, then Flatpak manifest and Flathub review, then deb/rpm; update policy per package | M | 2-3 | AppImage only |
| 17 | Linux QA matrix (GNOME/KDE, X11/XWayland, 100/150/200% scaling, IME, touchpad, HiDPI multi-monitor) and perf baseline (first-frame/navigation vs Windows) | M-L | 2.5-3.5 | partial |
| | **Total** | | **32-46** (realistic midpoint about 37) | MVP subset **14-18** |

## 7. Phased plan

**Phase 1: seams on Windows (2.5-4 weeks, no Linux build yet, ships in normal Windows releases)**
- Introduce `Glide.App/Platform/IPlatformServices` (or small per-area interfaces) and move every P/Invoke
  behind it.
- Generalise the existing `Explorer/ExplorerHost` replaceable-edge pattern: frame controller, clipboard,
  trash, known folders, reveal/open-with/properties, startup/autostart, file associations, thumbnail
  registration, launch-broker presence, print sink and video backend.
- Make `NativeImageDecoder`/`NativeImageEncoder` one backend among several, selected by capability instead of
  `IsWindows()`.
- Replace `StrCmpLogicalW` with a managed comparer plus a parity test.
- Move metadata writing to managed code (testable without WIC). Add managed BC DDS.
- Delete `WindowsExplorerHost`.
- Add an architecture test that fails if a `DllImport`/`LibraryImport` appears outside `Platform/Windows/` or
  `Glide.Imaging/Native/`.

**Phase 2: Linux build with the core viewer (8-11 weeks)**
- RID-conditional project setup, X11 backend, CMake for `libglidenative` (turbo JPEG only) and
  `libglidecodecs` (plus libtiff).
- Skia bounded decode, PNG/JPEG/WebP save.
- Linux frame chrome, tray, single instance, Speed Boost, XDG folders, Trash, reveal, image clipboard.
- Headless test suite on Linux CI. AppImage.
- Exit criteria: opening and navigating a folder of JPEG/PNG/WebP/TIFF/HEIC/AVIF/RAW at first-frame times
  comparable to Windows; Explorer tab browse, rename, move and delete to Trash; settings search shows no
  Windows-only items.

**Phase 3: integration and parity (10-14 weeks)**
- Video (libmpv or FFmpeg), overlays, tab drag between windows, print via CUPS, file copy/cut interop.
- `.desktop`/MIME association, freedesktop thumbnailer (then KIO).
- Flatpak and deb/rpm, the QA matrix, and Linux numbers added to the perf table in release notes.

**Phase 4 (optional)**
- Wayland-native once Avalonia ships a stable backend; revisit overlays and tab drag then.
- LibRaw provider.
- arm64 builds.

## 8. Risks

1. **Wayland.** Avalonia 11.3 has no stable native Wayland backend, so Glide runs under XWayland: blurry
   fractional scaling, and no global window positioning, which hurts overlays and cross-window tab drag.
   Re-check Avalonia's Wayland status before Phase 3; it may decide the overlay design.
2. **Hostile-input isolation.** `glidecodecs` uses SEH to turn decoder hardware faults into failures. Linux has
   no safe equivalent: a `SIGSEGV` handler with `siglongjmp` out of library code is unsafe. Options:
   - rely on the existing bounds and caps plus fuzzing;
   - decode exotic formats out of process. The thumbnailer CLI architecture makes this natural, but it costs
     latency on the viewer path.
   This decision affects security posture and the perf budget.
3. **First-frame performance parity.** WIC's `IWICBitmapSourceTransform` reduced decode and progressive-level
   previews are what the 5.0 perf numbers rest on. libjpeg-turbo DCT scaling covers JPEG; PNG and TIFF on Linux
   will be slower to first pixel unless they are tiled and bounded. Benchmark early in Phase 2, using
   `tools/perf`.
4. **Codec licensing and patents.** HEVC through libde265 and video codecs through FFmpeg or libmpv. Flathub and
   distros prefer dynamic linking to system or runtime FFmpeg. Static LGPL linking keeps the relink obligation.
5. **Test debt.** Many tests read source text or reflect into private members (`ARCHITECTURE.md`), and some
   assert Windows paths, registry behaviour or `\` separators. Expect a test-triage pass on the first Linux CI
   run. 13 test files reference Windows APIs directly.
6. **No Linux test machine.** Project rules forbid driving the user's desktop UI, and this host is Windows.
   WSL2 with WSLg covers X11/XWayland smoke tests and headless CI. GNOME/KDE, Flatpak and HiDPI behaviour need a
   real Linux machine or VM, run manually by the user.
7. **Divergence cost.** Every future feature has to state its Linux behaviour. Without the Phase 1 seams and
   architecture test, Windows-only calls will creep back into `MainWindow` partials. 45 files already branch on
   `IsWindows()`.
8. **Desktop-environment fragmentation.** Tray, file cut/paste formats, the portal Open-With chooser and
   thumbnailer hosting (GNOME/tumbler versus KDE KIO) each need per-DE testing.

## 9. How audit Stages C and D help

- **Stage D, "Interop: consolidate duplicate Win32 declarations, move to LibraryImport,
  DefaultDllImportSearchPaths".** This is the natural moment to gather the 159 declarations into a
  `Platform/Windows/` interop layer behind interfaces. Done with portability in mind, it covers most of Phase 1's
  P/Invoke work at little extra cost. Recommendation: define the interfaces during this step, not after.
- **Stage D, "Imaging without Avalonia: decoded-image type instead of Bitmap; Glide.Codecs.Managed project
  replacing 18 linked files".** This is the single most useful prerequisite.
  - A UI-free decode pipeline is what the freedesktop thumbnailer CLI, an out-of-process decode sandbox and
    Linux headless perf runs all need.
  - It also makes the decode backends (WIC, Skia, turbo, provider) swappable behind one surface type.
  - `Glide.Codecs.Managed` would build unchanged as `libglidemanaged.so`.
- **Stage D, splitting `MainWindow`** (status bar → gestures → tab strip → Explorer controller → history →
  navigation). Platform calls currently live inside partials: `MainWindow.Tabs` P/Invokes, `ViewerChrome`
  P/Invokes, Explorer reveal and properties. Each extracted controller can take its platform service by
  constructor, so the Windows-only code ends up in a small number of injectable places. Prioritise extracting
  the tab strip and Explorer controller with injected services, because they hold most of the platform edges.
- **Stage C, settings single source.** Add a `Platforms` (or `RequiresCapability`) field to `SettingsCatalog`
  entries while generating state, accessors and the control map from one catalogue. Windows-only settings can
  then be hidden on Linux and excluded consistently from settings search, hotkey-bindable setting actions and
  `tools/audit-settings-search.py`. Examples: file associations, Explorer thumbnail provider, Start with Windows,
  Snap/DWM options, JPEG XR. Doing this in Stage C avoids re-touching every setting later. The audit scripts
  already enforce that every setting is discoverable, so they need a platform-aware exclusion rule rather than
  per-setting exceptions.
- **Stage C stable Choice keys (R10)** removes English-label persistence. That matters for Linux settings files
  shared across machines, and for future localisation.
- **What the stages do not cover:** the video backend, printing, the Linux window frame, packaging and the
  codec SEH question. Those remain Linux-specific work (about 15-20 of the person-weeks above).
