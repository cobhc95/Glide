# Vertigo220 feedback, round 4 (GitHub issue #1, received 2026-11-22..25)

Rules (user, 2026-11-26): implement every item; reproduce bugs before and verify after with our own headless builds/tests (never the installed Glide, never drive the user's desktop); report behaviour changes to the user.

## A. Mouse back/forward
A1. Back/forward mouse buttons navigate back/forward in Explorer tabs BY DEFAULT (new installs, and existing users whose binding is "nothing"). User approved.
A2. Image view: back button returns to the Explorer tab the image was opened from (tab history). Do NOT override a user who bound back/forward to previous/next image. Option: "Back/forward buttons browse images, and Back on the first opened image returns to the folder".
A3. Find out what changed in a recent release that broke back/forward in Explorer/image tabs (regression) and fix it.

## B. Text overlay (in-image info overlay)
B1. Tooltip on hover: "Alt+drag to move".
B2. Overlay over an auto-hidden status bar can't be dragged (status bar shows/hides under it). Fix: while Alt is held / dragging, freeze status-bar auto-hide.
B3. Alt+click to move makes the overlay jump far right, even off-screen (bug). Clamp to the visible area.
B4. Settings layout: horizontal/vertical offset help text wraps over 4 lines; narrow the number boxes, help text spans both offsets; move offsets to the top next to the other position options, inside the per-position sections (tabs at top / side).
B5. Per-position settings: font size, colour, border, template etc. can be set per position (in image / tab bar / title bar). Title bar forces single line.
B6. Separator dropdown for the template (•, ▪, |, –, etc.) and an optional separator colour.
B7. Token list in help: arrange in columns, not a paragraph. Add short aliases (ext, MP, filesizeb, w, h, folderpath); keep old tokens working (silent aliases) but show only the short set.

## C. Explorer thumbnails info
C1. Info under thumbnails uses the overlay token template (reuse the code); date format customisable (DDD, YYYY/MM/DD HH:mm, h:mm tt…, standard .NET/Excel-style); date modified vs date taken tokens; resolution and size on one line.
C2. Sort by date taken.
C3. Modifier+mouse click options in Explorer, e.g. Alt+double-click = Properties.

## D. New tabs from Explorer
D1. Remove the separate middle-click new-tab options; middle-click uses the general new-tab options with modifiers. Repurpose those settings as "Duplicated tabs open [next to current/at end] in [foreground/background]" (default next to current).
D2. Both modifier keys set to the same key: allowed, warn that it inverts both.
D3. Modifier label "Key to open in the background instead" runs into the combo box: fix layout.
D4. Unsaved-settings prompt shows "Open new tabs in the: Off -> On": show real choice labels (applies to every Choice setting).
D5. Context menu "Open in new tab" shows the modifier hint, e.g. "Open in new tab (Shift: foreground)".

## E. Settings layout
E1. "Hide cursor in normal window" moved out of the Fullscreen section to Mouse & keyboard (or Appearance), with a link to the fullscreen option.

## F. Tabs
F1. Duplicating a tab duplicates its history (Back on duplicate returns to Explorer).
F2. Duplicate position/foreground options (see D1).

## G. Delete
G1. Shift+Delete (permanent delete) in image view, with a confirmation that has "Don't ask again for this folder in this tab".
G2. Optional delayed permanent delete (needs user decision — see questions).

## H. Hotkeys / GIF
H1. Space opens Settings in image view even though Settings is Ctrl+, and Space = play/pause GIF (bug).
H2. Paused GIF stays paused on the same frame when switching tabs away and back.

## I. Icons
I1. File-type icons registered by Glide show the extension (like ImageGlass/PotPlayer) instead of only the Glide logo.
I2. App icon in the taskbar looks 40–50% smaller than others (too much padding in the .ico).
I3. Tab-bar icons for Explorer vs image tabs too small / hard to tell apart.

## J. Missing files
J1. An open image deleted/renamed/moved on disk: show an indicator ("File no longer on disk").
J2. Switching away and back to that tab shows the previous tab's image instead (bug). Should keep showing the image (cached) with the indicator.

## K. Overlay mode window (user request, not Vertigo)
K1. Overlay mode window should share structure and appearance with the picture-in-picture overlay (same window chrome, look, drag), plus the overlay-mode extras: handle (drag strip) option and its context-menu items.
