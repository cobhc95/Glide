# Glide 4.7.0

A usability release built from user feedback: animated GIFs, a new fit mode, better tabs, a simpler Home page and many small fixes.

**Thank you to [@vertigo220](https://github.com/vertigo220)**, whose detailed 21-point report ([#1](https://github.com/cobhc95/Glide/issues/1)) drove nearly everything below. Community feedback is vital to Glide, and every point was read, tested and acted on. Please keep it coming.

## New

- **Animated GIFs play.** Can be turned off in Settings (Viewing & Appearance).
- **Fit to window.** Makes the image as large as fits the window, larger or smaller than 100%. On the status bar, the right-click menu, the `4` key, and as a default view.
- **Right-click + scroll.** Optional mouse gesture to zoom (or change image) by scrolling while holding the right button.
- **Tabs:** option to turn off middle-click to close, and Ctrl+Tab can switch in most-recently-used order.
- **File name on the status bar**, and the full path in the tab tooltip.
- **Status bar position:** right-click the status bar to put it Left, Center or Right.
- **Collapsed status button** can be parked in the lower-right corner, away from the image (on by default; turn it off in the right-click menu or Settings).
- **Speed Boost tray icon:** show it always, only while Glide is closed, or never.

## Changes

- **Home** now shows just your recent files.
- Collapsing the status bar now leaves only its expand button, with no empty strip.
- "Navigate to folder" is now called **Show in File Explorer**, which is what it does.
- The mouse settings now say what "default behavior" does for each action.
- Tooltips no longer cover the buttons they describe.

## Fixes

- A right-click menu no longer stays on screen as a ghost after you click.
- The "End of folder" message no longer replaces the image info permanently.
- "Open last session" now restores your tabs.
- "New tab opens" and "Home page" options no longer revert.
- Many tabs no longer overlap; the tab bar scrolls instead.
- Rename: Enter renames, Escape cancels, and the right arrow lands just before the extension.
- Settings: Cancel closes immediately, and the Mouse & keyboard page no longer opens scrolled down.
- Double-clicking in fullscreen no longer also skips to the next image.

## Downloads

- `Glide.Setup.exe`: installer (requires admin)
- `Glide-4.7.0-Portable.zip`: portable build
- `Glide.Setup.sha256`: installer checksum

Windows x64 only. The source for this release is now on GitHub at the `v4.7.0` tag (the source tree had been behind since 4.5.0).

Full list of changes: see the [changelog](https://github.com/cobhc95/Glide/blob/main/CHANGELOG.md).
