# 5.1.7 desktop UI test (2026-11-27, installed build from 26/11 17:59, test images in C:\GlideUITest)

Driven with keyboard and mouse on the user's PC.

## Passed
- A2: Back from an image returns to the Home or Explorer tab it was opened from.
- Explorer type-to-select ("ch" selects cherry.jpg, "an" selects anim.gif).
- H1: after clicking the Settings toolbar button, Space goes to the next image and does not reopen Settings.
- H2: a paused GIF stays on the same frame (6) after switching tabs away and back.
- B4: offsets sit inside the per-layout placement cards. B5: per-position template, font size, colour and border for the title bar and the vertical tab bar. B6: separator dropdown plus separator colour. B7: token columns with short names.
- Settings reopens on the same page, with the same expanded section and scroll position.
- E1: "Hide cursor in normal window" is under Mouse & keyboard > Mouse pointer.
- D1: "Duplicated tabs open / open in the" options. D2: both keys set to Shift shows the "inverts both" warning. D3: the modifier label wraps without overlapping. D4: the unsaved prompt shows "None → Shift". D5: "Open in new tab (Shift: background)".
- C2: sort by Date taken. C3: Alt+double-click opens Properties.
- I1: the JPG extension icon appears in the Open dialog and in Properties.
- Batch rename: the "fruit*" preview, then the rename itself.
- F1: the duplicate opens next to the current tab in the foreground, and Back goes to the folder.
- G1: Shift+Delete asks first, with "Don't ask again for this folder in this tab", deletes permanently, then moves to the next image.
- J1/J2: a file moved while its tab is in the background shows its own picture with "File no longer on disk".
- K: overlay mode has the same chrome as picture-in-picture (drag strip, opacity, close, zoom −/+, resize grip), and zoom works. The picture-in-picture overlay resizes from its corner.
- Issue #5: three external opens go into one process as three tabs. With Speed Boost off, closing exits fully.
- "Show in Windows File Explorer" works, and Glide's shell thumbnails are drawn there.

## Bugs found
1. Settings > Text overlay placement, "Tabs along the top" card: the Y offset box sticks out past the card's right edge.
2. The search result label "New-tab custom path" is cut off as "New-tab custom pat".
3. The unsaved-changes prompt uses different names from the Settings page ("Key to open at the other position instead" vs "Key to open at the end instead"; "Key to open in the foreground or background instead" vs "Key to open in the background instead").
4. Tags: with "Enable tags" on, no sidebar appears, even after F5, despite the description saying so. "Show / hide the tag sidebar" has no default key, no context-menu entry and no title-bar button, so it can't be reached.
5. The Tag groups search row shows two "Open" buttons, and the first one does nothing.
6. A tooltip in the Settings search results stayed stuck on screen after the pointer moved away.
7. Batch rename: clicking Rename also opened an image (base420-2048x2000.jpg) in the Explorer tab behind the dialog, and Back then skipped the folder and went to Home.
8. B1: the "Alt+drag to move" tooltip never shows when hovering the text overlay (right-click on it does open its settings, so the hit area is right).

## Settings behaviour (each setting changed, then its effect checked on screen; the user's settings were restored afterwards)
Passed:
- **Text overlay template:** short tokens {w} {h} {ext} {MP} {filesizeb} and the date format {modified:yyyy/MM/dd HH:mm} render correct values.
- **Separator and colour:** choosing "/" updates the overlay live; the Amber colour applies.
- **Offsets:** Y = 200 moves the overlay down and switches the card to Custom. Choosing a preset resets the offsets.
- **Title bar – center:** shows on one line in the title bar. Its own font size (9) applies there only.
- **Explorer thumbnail template:** the thumbnail info line follows the template, including "<br>" and the date format "ddd d MMM HH:mm".
- **Duplicate tabs:** set to "at the end" and "background", the copy goes to the far right and the current tab stays active.
- **D5:** setting the background key to Shift makes the menu read "Open in new tab (Shift: background)".

Bugs found in this pass:
9. The "Text overlay template" setting's description still lists the old long token names ({extension}, {width}, {filesizebytes}…), not the short set.
10. With X offset 5000, the overlay is clamped, but its last character is cut off at the window's right edge.
11. Explorer thumbnail template: for a folder, empty tokens leave stray text ("x /") instead of dropping the empty parts and their separators.
12. Middle-click on an Explorer thumbnail does nothing, so no new tab opens. Middle-click on a tab does close it, so the click is reaching the window.
13. Possible bug: with the text overlay in the title bar, an Explorer tab still shows the last image's info in the title bar.

## Not tested on screen
- **Hide cursor in normal window:** screenshots don't capture the pointer.
- **A1: mouse Back/Forward side buttons:** the control tool can't send them.
- **B2/B3: Alt+drag of the text overlay.** The control tool doesn't hold Alt during a drag.
- **Overlay mode: resize, right-click menu, drag handle.** The control tool reports that window as the desktop shell and blocks those actions.
- **Opening from an admin program.** The UAC prompt can't be driven.
- **Tray "Never" with Speed Boost on.** The user's Speed Boost is off, and I left their settings alone.
