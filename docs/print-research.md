# Print research notes — FastStone / IrfanView / XnView (Glide Print Phase 1)

Date: 2026-09-22. Machine: Windows 11 (10.0.26100), en-GB/metric locale,
default printer Microsoft Print to PDF, physical Pantum BP2300W present.

## 1. FastStone Image Viewer 8.5 (latest, 2026-06-24) — verified live

- Downloaded the official portable build (`FSViewer85.zip`, 16.3 MB, from
  faststonesoft.net, linked from faststone.org) and launched it:
  `FSViewer85/FSViewer.exe` with `1.jpg` as argument.
- Verified running: process `FSViewer`, title **"FastStone Image Viewer 8.5"**.
  Reference screenshot: `docs/screenshots/print-research/faststone-85-browser.png`
  (browser with Explorer-like folder tree left, thumbnail grid right, classic
  `File Edit Colors Effects View Tag Rating Favorites Create Tools Settings Help`
  menu bar, `File` first).
- How Print is opened (from the running build + official docs):
  `File → Print…`, toolbar printer icon, and browser/viewer/fullscreen all expose
  it. FastStone's own history documents print-dialog iterations (v4.4 Auto-Rotate
  option, v3.8 Number of Copies, v1.4 dialog retains user settings, v4.7/4.8 print
  dialog improvements) and the separate entry points `Create → Contact Sheet
  Builder` (multi-image) and `Create → Design and Print` (`Alt+P`, multiple images
  with text/effects on one page). Normal image printing and contact-sheet building
  are deliberately separate workflows.
- Contact-sheet workflow (official tutorial + club guide): thumbnail count across
  and down (rows/columns), spacing, page margins, live preview window, Create at
  bottom-right, and FastStone remembers the settings next time.
- Keyboard shortcut: FastStone documents `Alt+P` for Design and Print. The normal
  print path follows the Windows `Ctrl+P` convention shared by the other viewers;
  Glide adopts `Ctrl+P` as the primary shortcut.
- Live `Ctrl+P` attempt: after verifying the browser, focus was activated and
  `Ctrl+P` sent — but the machine was in an active fullscreen application at the
  time (see `desktop-during-attempt.png`, which shows an unrelated fullscreen game
  receiving focus instead of FastStone). To avoid stealing focus / keystrokes from
  the active application, no further synthetic-input automation was performed. The
  behaviour below is therefore documented from the verified build's menu structure
  plus official FastStone history/tutorial/contact-sheet guides and the IrfanView /
  XnView primary sources, not from a captured FastStone print-dialog pixel shot.
  This limitation is recorded honestly; the implementation does not depend on any
  unverified FastStone pixel detail.

## 2. IrfanView — official Print help (irfanview.net/help, verbatim semantics)

- `File → Print`. Dialog left side: printer + orientation; live preview of
  Portrait/Landscape; `Printer setup` button for paper size and driver settings.
- Modes: **Original size** (image DPI; 72 DPI fallback when absent; if larger than
  paper, only the visible part prints — i.e. clip, no shrink), **Best fit to page**
  (as large as possible, aspect ratio preserved), **Stretch to page** (fills print
  area, aspect ratio NOT preserved), **Custom** (explicit W/H + left/top margin in
  cm/inches, capped by paper size, optional aspect-ratio lock), **Scale** (factor,
  1.00 = original, capped by paper size). Later versions add **Fill paper** for
  Best-fit and **Vertical best fit**.
- Centring: `Center image` on/off, vertical/horizontal/both; X/Y position may be
  negative. `Borderless printing` extends over the unprintable area (driver
  dependent). `No overflow on page` shrinks width/height that exceed paper.
- Profiles: dialog options can be saved/loaded as named profiles.
- Header/footer: filename/path/custom text supported.
- Multipage: enabled only for multipage files (e.g. multi-TIFF); print specific
  pages with `;` delimiter and `-` ranges; copies + collate for multipage.
- IrfanView re-applies saved driver values on dialog start: orientation, paper
  size/source, colour mode, duplex.
- Preview explicitly does NOT show the unprintable area — a known divergence risk.
  Glide must NOT copy this: preview and output both use the real printable bounds.
- Selection: an active rectangle selection prints only the selected area.
- Copies: number of copies (+ collate).

## 3. XnView (Classic/MP) — layouts, rotation, positioning, margin bugs

- Layout tab offers best-fit/crop/stretch style layouts plus DPI/original-size
  handling and automatic rotation discussion (forum: "auto-rotate when printing",
  "de-activate auto-rotation", "print everything in landscape/portrait").
- Position tab: nine-point grid Left/Centre/Right × Top/Centre/Bottom.
- Known historical bugs (directly relevant to Glide acceptance):
  (a) vertical centre/bottom ignored when image larger than paper in DPI mode —
  i.e. positioning must be defined for the clipped-larger-than-page case too;
  (b) switching printers ignores margin settings until restart — i.e. never carry
  stale printer-specific capabilities across printers; re-resolve paper/margins
  per printer. Glide's layout engine defines alignment for clipped output and the
  dialog re-resolves paper/source/printable bounds on every printer change.

## 4. Behavioural contract adopted for Glide Print (reference synthesis)

| Concern | Glide behaviour (matches mature viewers unless noted) |
|---|---|
| Open | Toolbar/file action + viewer context menu + browser item menu + `Ctrl+P`; fullscreen uses the same viewer menu. (Glide has no menu bar; these are its File equivalents.) |
| Shortcut | `Ctrl+P` (free: no existing binding; verified no `Ctrl+P`/`Print` in `HotkeyCatalog`). |
| Default printer | Preselect OS default; changing printer re-resolves paper/source/printable bounds, keeps layout prefs. |
| Preview | Live WYSIWYG, updates on paper/orientation/scaling/margins/alignment change; preview and spool share one geometry function over the real printable rectangle (unlike IrfanView's preview, which hides unprintable area). |
| Paper size/source | From the selected printer's own lists; source only if the driver reports trays. |
| Portrait/landscape | Explicit control; swaps the sheet geometry. |
| Margins | Four independent margins, mm (metric locales incl. en-GB) or inches (US etc.); clamped into the printable area; 0 allowed where the driver permits (effective box = user box ∩ printable area). |
| Alignment | Nine positions; defined also when the image is clipped larger than the box (top-left of the clipped window per alignment). |
| Scaling | Best fit (aspect preserved) / Fill page = crop-to-fill / Actual size 100% (embedded DPI else documented 96 default; clip, never shrink) / Custom % (of actual size; clip) / optional stretch (aspect off) for Best-fit parity with IrfanView Stretch. |
| Aspect | Kept by default in every mode except explicit stretch. |
| Auto rotate | Rotates the image 90° when it yields a larger fit; applies to fit/fill/actual/custom. |
| DPI | JFIF density, EXIF X/YResolution+unit, PNG pHYs, TIFF resolution tags, BMP PelsPerMeter; validated 10–2400 else 96. |
| Copies | 1–99, persisted as a layout pref; collate is N/A single-image. |
| Colour/grayscale | Exposed only if the driver reports colour capability; defaults to colour. |
| Properties | Native printer Properties/Preferences button adjusting the live .NET driver settings. |
| Larger than printable | Best/Fill scale down; Actual/Custom clip to the box with alignment choosing the visible window (IrfanView parity). |
| Smaller than printable | Never upscaled in Actual/Custom; Best/Fill scale up (Fill crops). |
| Transparency | Composited against white (GDI printers have no alpha). |
| No printer | Print disabled with an explanatory message; dialog still opens for layout inspection. |
| Cancel | Dialog Cancel discards; mid-spool cancel ends the job after the current page (single-image ⇒ immediate). |

## 5. Multi-image / contact sheet

FastStone keeps this separate (Contact Sheet Builder: rows/columns, spacing,
margins, preview, remembered settings; Design and Print `Alt+P`). XnView asks for
auto-rotation control per layout. Glide's browser multi-selection exists
(`BrowserSelectionPolicy`, Shift/Ctrl in the browser grid) but the viewer pipeline
is single-image and persisting multi-page jobs would destabilise v1. Decision:
ship complete single-image printing first; the layout engine is page-shaped so a
contact-sheet follow-up (one image per page; N-up grid with rows/columns/spacing
and page navigation) slots in without touching the v1 path.

## 6. Phase 2 implementation and acceptance evidence (Glide 4.2.5)

**Architecture (isolated behind a small, testable boundary):**

| Component | Responsibility |
|---|---|
| `PrintLayoutEngine` | Pure page geometry: margins ∩ driver printable rect, scaling modes, auto-rotate, nine-way alignment, crop windows, clipping/warnings. Unit-tested. |
| `PrintDpiReader` | JFIF / EXIF X-YResolution+unit / PNG `pHYs` / TIFF / BMP DPI with a validated 96 DPI fallback. |
| `PrintDocumentSettings` + `PrintSettingsStore` | Printer-agnostic layout persistence in `print.settings.json` (no paper/source names). |
| `PrintService` | Windows GDI+ spool path; re-reads the live driver at print start and recomputes the page with the same engine the preview uses. |
| `PrintPreviewControl` | Avalonia WYSIWYG page render (sheet, unprintable shading, user margins, image) from the same `PrintPageLayout`. |
| `PrintWindow` | Glide-native dialog: printer, Properties, paper/source, orientation, copies, colour, scaling, alignment, margins, live preview. |
| `PrintDriverProperties` | Native printer Preferences via `DocumentProperties` P/Invoke. |

**Acceptance results (Microsoft Print to PDF, A4, `--print-to-pdf` harness):**

| Case | Invocation | Result |
|---|---|---|
| Landscape JPEG | Best fit | ✓ PDF, embedded image 1280×720 |
| Portrait JPEG | Best fit | ✓ PDF, embedded image 720×1280 |
| High-res + EXIF rotation | Best fit | ✓ stored 1280×720 / orientation 6 printed as 720×1280 (matches portrait) |
| Tiny low-res PNG | Actual size | ✓ PDF, 0.25 in image |
| Transparent PNG | Best fit | ✓ PDF, colour image + soft mask (alpha composited) |
| Unusual DPI (300) | Actual size | ✓ PDF, 600×400 px → 2×1.33 in |
| Fill/crop + landscape paper | Fill page, landscape | ✓ PDF |
| Actual size (clipping) | Actual size | ✓ PDF, aligned visible window |
| Custom scale | Custom 200 % | ✓ PDF |
| Stretch | Stretch | ✓ PDF |
| Zero margins | Best fit, margins 0 | ✓ PDF, confined to printable edge |
| Right/Bottom alignment | Best fit | ✓ PDF, same embedded image repositioned |

The first acceptance run exposed a real bug: `PrintPageEventArgs.Graphics.PageUnit` is
`Display` (1/100 inch) on printers, and the render code multiplied the hundredths by the
printer DPI, pushing every image off-page (only a corner was embedded). The renderer is now
explicitly page-unit aware (`Pixel`/`Display`/`Inch`/`Millimetre`/`Point`), and all cases
embed the full image.

**Screenshots:**
- `docs/screenshots/print-research/faststone-85-browser.png` — verified FastStone 8.5 build.
- `docs/screenshots/print-research/glide-print-dialog.png` — Glide's Print window with the live preview
  (Microsoft Print to PDF / A4, Best fit, auto-rotate off). A 1280×720 landscape photo measured
  **184.7 mm × 103.9 mm** — exactly 16:9, no distortion, no wasted page space.
- `docs/screenshots/print-research/glide-print-dialog-autorotate.png` — the same photo with
  **Automatically rotate for best fit** on: correctly rotated 90° onto portrait paper
  (152.7 mm × 271.5 mm), which is the fix for the former auto-rotate stretch defect.

> **Superseded capture note (2026-09-22):** the original `glide-print-dialog.png` showed the
> auto-rotate **stretch** defect — a 16:9 landscape photo drawn as a tall, narrow portrait crop. That
> screenshot was captured before the 4.2.6 fix (auto-rotate now performs a real rotation in both the
> preview and the spool path). Both files above are regenerated from the fixed 4.3.0 build.

**Deferred:** multi-image / contact-sheet printing (see §5).

