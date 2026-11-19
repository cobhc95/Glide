# Glide 5.1.3

Released 2026-11-19. A small follow-up to 5.1.2: text overlay placement is now set per tab layout, a title-bar overlay never covers the tabs, right-clicking the overlay opens its settings, and the hidden mouse pointer stays visible over bars, menus and overlays. Settings from 5.1.x load with the one-time migration noted below. The full list is in [CHANGELOG.md](../../CHANGELOG.md).

## Highlights

- **Text overlay placement per tab layout**: two side-by-side cards, *Tabs along the top* and *Tabs at the side*, each with its own position and alignment. Glide uses the card that matches your tabs, marked *In use now*. Positions: over the picture (top/bottom × left/center/right), *Title bar – left/center/right*, and with side tabs *Vertical tabs – top/bottom*.
- **Never overlaps**: a title-bar overlay makes room for itself. *Left* pushes the tabs right, *right* keeps clear of the window buttons, *center* uses free space after the tabs (or the tabs shrink).
- **Right-click the text overlay** to jump straight to its settings card; also available as the bindable action *Open text overlay settings*.
- **Hide cursor**: the pointer now hides only over the bare picture and always shows over Window-in-Window overlays, the status and button bars, the title bar and tabs, menus and popups, in fullscreen and the normal window.

## Fixes

- In a bar, *Automatic* alignment lined the folder index and picture index up differently, so the two lines drifted apart. They now sit together as one block.

## Defaults and migration

- The single text overlay position, alignment and *Text overlay with vertical tabs* settings are replaced by the two cards (settings schema 22). Your existing choices are copied into the matching card.

## Under the hood

- Title-bar overlay layout is a pure, tested function (`TitleBarOverlayLayout`) re-run on text, tab and window size changes.
- The cursor-hide check and the native `WM_SETCURSOR` handler share one "bare picture" hit test.

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.1.3-Portable.zip`: portable build
- `Glide.Setup.sha256`: checksum (SHA256 `f572555cb1d3e11070c4ec7823471c50de5472658764cf5ed098c964af315f18`)
