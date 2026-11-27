# Vertigo220 feedback, round 3 (received 2026-11-18) — queue step 6

Rules (user): fix all bugs automatically, each reproduced before and verified after; take improvements to current behaviour on board; complete behaviour changes → consult the user first with visuals sent as PNG pictures.

## User decisions (2026-11-18)
- #5 Tab preview size: APPROVED (option and/or modifier+wheel).
- #11 Explorer hover preview: REJECTED — do not add (no modifier-key variant either). Glide has no Explorer hover preview today; if any exists, remove it.
- #6 Extra info under thumbnails: APPROVED — optional, OFF by default; must fit the selection highlight/fill and the equal top/side band styling, and the tile heights.
- #18 New-tab options redesign: APPROVED as proposed — "New tabs open [at the end/next to current]" + "Key to open … instead: [None/Shift/Ctrl/Alt]"; "Open new tabs in the [foreground/background]" + "Key to open in the … instead: [None/Shift/Ctrl/Alt]"; labels flip with the default; migrate the old "Shift and Alt change how a new tab opens" checkbox (on → Shift/Alt, off → None/None); fix "at at" (#19).
- #20 Overlay moving: APPROVED — left-drag anywhere moves the overlay window when the picture fits; when zoomed, left-drag keeps panning and the window moves by dragging the bottom bar or Alt+drag. Optional right-click menu items (off by default, remembered): "Show drag strip" = option A, a thin 8 px dark strip with a small grip line along the top edge, shown only while the mouse is over the overlay, fading out when it leaves (NOT a full title bar); and "Lock position" (prevents moving by drag).
- #2 Sort list with method+order entries (Name ▲ / Name ▼ …, collapsed shows triangle): APPROVED.
- #3 Sort by image dimensions (width/height/total pixels, submenu OK; rename "Size" → "File size"): APPROVED.
- #29 Batch rename: WAIT for Vertigo's separate request.
- #30 Linux: assessment report only (feasibility, effort, blockers, size); NO porting work.
- #12 Drive bar: only RESIZABLE by dragging the divider line between the sidebar and the thumbnail grid (remember width). Tree navigation and middle-click NOT approved.

## Sort by dropdown (Explorer tab)
1. Remove the up/down arrows on the left (arrows on both sides confuse); make the order arrow larger or use a triangle (▲▼).
2. List each sort type twice (Name ▲ / Name ▼ …) so one pick sets method + order (2 clicks instead of 4); collapsed text unchanged, order shown by the triangle.
3. Add sort by image dimensions (rename current "Size" to "File size"); horizontal / vertical / total pixels, possibly in a submenu.

## Tab previews
4. Some previews are cropped to a small part of the image, others show it whole (bug).
5. Adjustable preview size: option, and/or modifier+wheel on the tab while the preview shows (user-choosable modifier).

## Explorer tab
6. Option to show extra info under thumbnails (date, resolution, size).
7. Multi-select shows total size, but select-all / large selections don't (bug).
8. Drag-select works, but no drag to deselect.
9. Escape doesn't clear the selection.
10. Shift+Home/End selects to folder start/end (like Shift+Ctrl+Home/End) instead of row start/end (bug).
11. Optional enlarged hover preview of an image, with a user-chosen modifier key.
12. Drive bar: resizable, tree navigation, middle-click opens drive/folder in a new tab.
13. Recent locations dropdown: full paths, ordered most → least recent.
14. Delete dialog: Delete button focused so Enter confirms (bug/standard behaviour).
15. Copy/paste and delete make the whole pane flicker black (bug).
16. Right-click with no selection: "Properties" → "Properties of {current folder}".
17. Opening a folder / going back briefly shows "No folders or images in this folder." before contents load (bug).
18. New-tab options redesign: replace "Shift and Alt change how a new tab opens" with per-option modifier choices:
    "New tabs open {at the end / next to current}" + "Modifier key to toggle position" {Shift/Ctrl/Alt or press-a-key};
    "Open new tabs in the {foreground/background}" + "Modifier key to toggle…" (label adapts).
19. Label bug: "New tabs open at at the end" (double "at").

## Overlay
20. Overlay mode window can't be moved (no title bar / drag area).
21. Opacity slider should respond to the mouse wheel.
22. Opacity slider drag doesn't track the mouse going down (stops ~40%), jumps when clicked, often needs several clicks to grab (bug).
23. Blue line across the top from top-right to midpoint then down, when hovering the right half (bug?).
24. Bottom-bar right-click → Size submenu sometimes doesn't highlight the item under the cursor until the mouse moves (bug).
25. Size menu lacks a checkmark for the current size.
26. Large sizes (>100%) push the bottom bar behind the taskbar; overlay can open with the bottom bar fully hidden; blue line and slider positions go wrong then (bug).
27. "Interaction: Overlay mode" in the bottom-bar menu has a checkmark but clicking does nothing (bug).
28. Switching to overlay mode and back sometimes keeps the overlay size for the normal window (bug).

## Renaming
29. Batch rename and custom presets/tags with buttons (large feature; Vertigo will file a separate issue).

## Platform
30. Linux port (long-term; feasibility only).
