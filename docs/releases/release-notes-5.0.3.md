# Glide Image Viewer 5.0.3: vertical tabs and a customizable status bar

A feature and polish release on top of [5.0.2](https://github.com/cobhc95/Glide/releases/tag/v5.0.2). Settings from 5.0.x load unchanged.

Thanks to [vertigo220](https://github.com/vertigo220) for another round of detailed feedback in [issue #1](https://github.com/cobhc95/Glide/issues/1) 🙏. Most of this release comes from it.

## ✨ What's new

**Vertical tabs**
- Tabs can sit in a pane on the left of the window. Right-click a tab or the empty title bar and choose **Place tabs vertically** (or **Place tabs on top** to go back). It is also in Settings > Tabs & status bar and can be bound to a hotkey.
- Drag the pane's right edge to resize it; the width is remembered. Double-click the edge for the default width.
- The side pane and the top tab strip can each auto-hide and reveal at the window edge, like the status bar.

**A customizable status bar**
- **Customize status-bar buttons** (status-bar right-click menu or Settings) shows, hides and reorders the built-in groups, and can add dozens more Glide actions as buttons, grouped by purpose. It works like the title-bar customizer. Nothing new is added by default.
- Right-click the status bar's minimise button to turn auto-hide on or off. The button stays visible, with its own icon, while auto-hide is on.
- Mouse wheel over the status bar moves to the previous / next image.
- Optional: single-click the file name to rename it, double-click to open its location.

**Everywhere**
- **Esc cancels every dialog**: prompts, confirmations, customizers, slideshow options, rename. It does exactly what Cancel does and never saves a "Don't remind me again" box. An open dropdown or an active shortcut capture takes the Esc first.
- Settings: an **Always expand** switch in the bottom-left corner (off by default) opens every section expanded, and **Ctrl+F** jumps to the search box.
- Hotkeys to make the tab tooltip text larger or smaller.
- `<br>` starts a new line in the custom text overlay.

## 🔄 What changed

- GIF play / frame buttons are always in the status bar and greyed out on other images, so the bar no longer changes size while you browse. The file path gets more room. To get the old behaviour back, turn on **Show GIF controls only on GIFs** (Settings or the status-bar right-click menu).
- The tab tooltip shows `Name:` and `Path:` on separate lines. Changing its size in Settings previews it on the current tab.
- Tooltips no longer have square brackets around them.
- The Explorer view sizes say **Thumbnails** instead of Icons. Your saved choice carries over.

## 🛠️ What's fixed

- Tab drag: a reorder sticks once the tabs have visibly swapped, and a slightly sloppy drag no longer tears the tab off. Tearing a tab off no longer copies every tab into the new window when the startup action is "Open last session".
- Switching vertical tabs back to the top no longer leaves the tabs squashed until the window is resized.
- Changing the text overlay font size no longer lags.
- The hotkey conflict prompt shows proper quotation marks. The hotkey list keeps its filter after you assign a shortcut, and Enter runs the search again.
- Explorer scrolls a page in about one and a half wheel notches.
- The unsaved-changes list no longer always shows Custom Gestures and Window Placements, and it describes them in words.
- Collapsing a Settings section keeps its header where it was instead of jumping.

## 🔧 Under the hood

- Esc for dialogs is one shared handler; a test checks that every window type is covered.
- Every new setting is searchable in Settings, and every new action can be bound to a hotkey or gesture.
- Tests: Core 1580, Input 21, Explorer 125.

## 📦 Downloads

| File | What it is |
|---|---|
| `Glide.Setup.exe` | Installer (administrator rights; installs to Program Files, adds Open With and Explorer thumbnails) |
| `Glide-5.0.3-Portable.zip` | Portable: extract anywhere, run `Glide.exe` |

SHA256 of `Glide.Setup.exe`: `A44FF544F418DFFB554B2763BB19E9B12AF1CC61D05E8E3B767EAE91F186D3E9`

Install over 5.0.2, 5.0.1, 5.0.0 or any 4.x: settings carry over. Windows 10 1607+ (x64). No telemetry; the only network use is the daily update check (off in Settings) and the optional video-decoder download.

Full list of changes: [CHANGELOG](https://github.com/cobhc95/Glide/blob/main/CHANGELOG.md).
