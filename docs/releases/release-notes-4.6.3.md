# Glide 4.6.3

A stability fix for multi-window use.

## Fixes

- **Crash after opening a second window.** Opening another window (for example with Ctrl+N) could make Glide close without warning a little later, often while moving that window. The cause was in how each new window registered its own taskbar button; it is now done in a way that cannot fail this way.
- **Menus hidden behind the window with "Always on top: Hard".** Submenus and tooltips are no longer pushed underneath a hard always-on-top window, and windows no longer fight over stacking order while one is being dragged or resized.
- **More robust window moving.** Moving a window no longer saves settings on every pixel; placement is saved once when the move ends.
- **Crash log.** If Glide ever does crash, the error is now written to `%LOCALAPPDATA%\Glide\crash.log`.

## Downloads

- `Glide.Setup.exe`: installer (requires admin)
- `Glide-4.6.3-Portable.zip`: portable build
- `Glide.Setup.sha256`: installer checksum

Windows x64 only. The GitHub source tree at this tag does not yet contain the 4.6.x changes.
