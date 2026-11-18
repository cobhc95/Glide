# Vertigo220 round 3 — results

Version stays 5.0.8; all entries are in CHANGELOG [Unreleased]. Each item is a separate local commit (not pushed). No consult mockups were needed.

| # | Status | Commit | Before → after evidence |
|---|---|---|---|
| 1-3 | Implemented: ▲▼ triangle after the field name, left arrows removed, one entry per field+direction, Dimensions submenu (Width/Height/Total pixels), "Size" → "File size" | 194c206 | New `Dimensions_submenu_sorts_by_width_height_and_total_pixels`; updated sort menu/button tests pass |
| 4 | Fixed (portrait previews were UniformToFill-cropped) | 0c053ab | `Round3_4_the_tab_preview_shows_the_whole_picture_never_a_cropped_part`: failed "only 15% of a 40x200 picture is shown" → passes |
| 5 | Implemented: Tab preview size (%) 50–250 + "Resize tab preview with wheel and" Ctrl/Shift/Alt/None | dea0001, cd086f8, 7accc05 | `Round3_5_...` tests. Found a real bug in the process: ToolTipGuard closed the preview on wheel before resize (failed Expected 120 / Actual 100) → fixed |
| 6 | Implemented: "Show under thumbnails: Date / Resolution / File size", all off by default; tile heights, layout, selection math updated | 8263a30 | `ExplorerTileInfoTests` (band equality, inside selection fill, layout matches) |
| 7 | Fixed: total size for selections that include folders | 10e5124 | `Select_all_with_folders_shows_the_total_size_of_the_selected_files`: "6 items selected" with no size → passes |
| 8 | Implemented: Ctrl+drag toggles (deselects) | e7cd1ae | `Ctrl_drag_over_selected_items_deselects_them_like_Windows_Explorer` fail → pass |
| 9 | Implemented: Escape clears selection | 3b398de | `Escape_clears_the_selection` fail → pass |
| 10 | Fixed: Shift+Home/End = row; Ctrl+Shift = folder | 50277c5 | `Shift_Home_and_End_select_to_the_row_start_and_end_...` fail → pass |
| 11 | Rejected; verified there is no Explorer hover preview (tooltip text only) | — | Code inspection |
| 12 | Implemented: divider drag only, width 120–600 saved as "Drive bar width (px)" | 140b248 | `ExplorerDriveBarTests` (208 → 298, saved) |
| 13 | Implemented: full paths, most → least recent, each once | 467252c | `Recent_order_is_most_recently_visited_first_with_each_place_once`; UI test failed with Back/Forward order before |
| 14 | Fixed: Delete button focused/default, Enter confirms (all confirm dialogs) | 1336f86 | `ConfirmDialogTests`: timed out before → passes |
| 15 | Fixed: reload keeps tiles until new listing is ready | f97c450 | `Reloading_the_shown_folder_keeps_the_tiles_until_the_new_listing_is_ready` fail → pass (visual flash needs your eye) |
| 16 | Implemented: "Properties of {folder}" | 08635bd | Updated tests fail → pass |
| 17 | Fixed: empty-folder text not shown while loading | 13c7759 | `Opening_a_folder_never_shows_the_empty_folder_text_before_its_contents` fail → pass |
| 18-19 | Implemented: position + key, foreground/background + key, labels flip, "at at" gone; migration (settings schema 18): old checkbox on → Shift (position) / Alt (fg/bg), as the user chose, off → None/None | c33ebc6 | Pure tests for keys, labels, migration; Explorer Ctrl-choice test |
| 20 | Implemented: drag moves when fitted; zoomed = pan, bottom bar / Alt+drag moves; "Show drag strip" (8 px, fades) and "Lock position" in the overlay menu, off by default, saved | 54f492d, f3c4a97 | `OverlayRound3Tests` (fit, zoomed, Alt, bottom bar, locked, strip) |
| 21-22 | Fixed: wheel = ±5%; slider thumb tracks pointer to 35%, click sets value, larger grab area | 1f01cc9 | Headless: dot 14 DIP off pointer, wheel no-op → passes |
| 23 | Not reproduced headlessly; very likely the overlay frame on an oversized window (same cause as #26) | — | Frame aligns with window in headless test |
| 24 | Fixed: submenu highlights item under a still cursor (all Glide context menus) | d819976 | Headless: nothing selected → selected |
| 25 | Fixed: current size ticked | 6a09f6a | Test: no tick after 50% → ticked |
| 26 | Fixed: sizes and remembered spot kept within the monitor work area | 8c81fa3 | Headless: bottom 1430 vs work area 1280 → inside |
| 27 | Fixed: one on/off item "Use windowed interactions (drag pans, selection zoom)" | 72cd8b8 | Header ticked while off → toggles correctly |
| 28 | Fixed: leaving overlay restores the remembered normal size (from normal/maximized/fullscreen) | 307427e | Remembered size came back 1596x1031 instead of 1000x700 → correct |
| 29 | Waiting for Vertigo's separate request | — | — |
| 30 | No work (assessment only: docs/audits/linux-portability-assessment.md) | — | — |

## Manual check list
1. Explorer Sort button: "Name ▲", each field listed twice; Sort by > Dimensions > Total pixels ▲.
2. Hover a tall portrait's tab: whole picture shown. Ctrl+wheel over the tab resizes the preview and it stays open.
3. Settings > Explorer tab: turn on Date/Resolution/File size; the lines sit inside the selection highlight; List view hides them.
4. Ctrl+A in a folder with subfolders shows total size; Ctrl+drag deselects; Esc clears; Shift+End selects to row end.
5. Drag the drive bar divider, restart: width kept.
6. Recent locations dropdown: full paths, newest first.
7. Delete a file, press Enter: deleted. Copy/paste/delete: no black flash. Open a big folder / Back: no "No folders or images" flash.
8. Right-click empty space: "Properties of {folder}".
9. Settings > Tabs: new-tab options and key labels flip; Ctrl+middle-click opens in the background when chosen. Migration: old checkbox on → Shift = position, Alt = fg/bg.
10. Overlay: drag fitted picture moves window; zoomed drag pans, bottom bar / Alt+drag moves; Show drag strip and Lock position; opacity slider drag/click/wheel; Size menu tick and immediate highlight; 150%/200% stays above the taskbar; no blue line; leaving overlay (from fullscreen too) restores normal size.
