# Glide 5.1.2

Released 2026-11-19. A small follow-up to 5.1.1: the mouse pointer can now be hidden reliably in fullscreen and in the normal window, and a round of Overlay mode and text overlay improvements suggested by vertigo220. Settings from 5.1.x load unchanged apart from the one-time migration noted below. The full list is in [CHANGELOG.md](../../CHANGELOG.md).

## Highlights

- **Hide the mouse pointer**, now working in fullscreen and new for the normal window (over the picture only). Choose **After a delay** (0.5–30 s in half-second steps, default 2 s) or **Always while over the picture**. Moving, clicking or scrolling shows it again; menus, dialogs, text boxes and drags always show it. Both options are off by default.
- **Overlay mode is easier to move**: pointing at the middle of the top edge shows the drag handle, even with *Show drag strip* off.
- **Opacity slider** tracks the pointer all the way down to its 35 % minimum, which is labelled.
- **Text overlay layout**: alignment (Automatic / Left / Center / Right), an offset from its usual place (remembered per position, or Alt+drag the text to move it), and with vertical tabs it can sit at the top or bottom of the tab bar (tabs make room for it) or in the title bar.
- **Clearer text overlay tokens**: the help explains every token; `{file}` and `{filename}` are the same (name with extension) and `{path}` is the full path.

## Fixes

- *Hide cursor in fullscreen* did nothing: the picture area kept overriding the hidden pointer and the delay setting was ignored. The pointer is now hidden at the Windows level while idle, so it disappears even when the mouse is perfectly still.

## Defaults and migration

- *Hide cursor in fullscreen* is now off by default. Settings saved before 5.1.2 with it on (the old, non-working default) are switched off once (settings schema 20); turn it on again in Window & navigation if you want it.

## Under the hood

- One idle-cursor policy (`FullscreenCursorAutoHide`) drives both modes; the native window frame answers `WM_SETCURSOR` with no cursor while hidden.
- Text overlay placements and per-position offsets come from master lists (`TextOverlayPlacements`, `TextOverlayOffsets`); every new option follows the settings checklist, is searchable and hotkey-bindable where it is a toggle or choice.

Thanks to vertigo220 for this round of suggestions.

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.1.2-Portable.zip`: portable build
- `Glide.Setup.sha256`: checksum (SHA256 `b9133853767018633f180228e9c7e2bc17705fe539b3c6a750246b1365d02e72`)
