# Glide 5.0.5

Thanks to Vertigo for yet another round of detailed testing and feedback. Settings from 5.0.x load unchanged. The full list is in [CHANGELOG.md](https://github.com/cobhc95/Glide/blob/main/CHANGELOG.md).

## New

- **Open a picture's folder in** (Ctrl+E): Same tab (new default; Back returns to the picture), Existing or new tab, Always a new tab, or New window.
- **Back returns to the picture** after opening its folder, with Forward going back to the folder. Setting: *Back after opening a picture's folder*.
- **Explorer scroll amount:** one row per wheel notch by default, or a fixed number of rows, per view (1 / 2 / 3), or a fixed pixel distance. The scroll speed multiplier applies on top.
- **Alternative file manager:** *Show in <program>* in the right-click menu of the Show in Explorer buttons. Its Settings row has Browse… and clear buttons. Before one is set, *Set up* opens Settings at that option.
- Cancelling Settings with unsaved changes offers Keep editing, Discard or Apply.
- Clear (x) button and *Show all hotkeys* in the hotkey search.
- Text overlay line breaks with `<br>` or `\n`.
- A drop marker when dragging a tab back onto a window.

## Fixes

- Maximize / restore from the title bar no longer flashes or jumps to the corner.
- Tabs dragged partway stay where the preview showed them; drops between tabs land at the marker.
- Right-clicking the status bar's auto-hide button no longer opens the bar menu.
- Settings sections no longer jump when collapsed.
- GIF controls follow the *only for GIFs* setting.
- Smooth text-overlay font size changes.
- Show in File Explorer selects the file. Open file location no longer opens duplicate tabs.
- Recent locations has a clock icon.
- Tooltips no longer get stuck open after a dialog opens, which used to block every other tooltip.
- The Settings change summary shows its list on hover or click.
- Long customiser names show their full text on hover.
- No more redundant tooltips on the Settings category buttons.

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.0.5-Portable.zip`: portable build
- `Glide.Setup.sha256`: checksum
