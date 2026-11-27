# Explorer tab vs Windows File Explorer: keyboard and mouse parity (5.1.7)

Scope: the managed Explorer tab (`MainWindow.ExplorerInput.cs`, `MainWindow.Browser.cs`, `MainWindow.ExplorerOps.cs`,
`MainWindow.ExplorerMenus.cs`, `Controls/BrowserSurface`). Determined from the code and the headless tests, not from a running
app. "Fixed" keys are registry entries of kind `FixedShortcut` in `AppActionRegistry.cs`; everything else goes through the hotkey map.

Effort: S = under an hour, M = a few hours, L = a day or more / new subsystem.
Status: **Done** = implemented with a test in this change (`ExplorerTypeAheadTests`, `ExplorerWindowsParityTests`).

## Decision: Windows wins in the Explorer list

While the Explorer list has the keyboard, unmodified (or Shift-only) letters, unshifted digits, number-pad digits and `- . ,` are
**type-to-select characters, not hotkeys**. Default single-key hotkeys affected: `F` (fullscreen), `O` (open file), `U` (refresh)
and the viewer-only `I L R H V P S 1-4`, `Shift+A/W/O`. They still work everywhere else (viewer, toolbar / filter focus,
`F11`, `Ctrl+O`, `F5`, `Ctrl+R`, ...). Exceptions kept as hotkeys: `Shift+1..3` (external programs, they work from an
Explorer tab), `Enter` (Explorer opens the item), `Space` (only joins a prefix typed within the last second; otherwise it is the
viewer's, still a no-op in Explorer), `+ =` (zoom, nothing in Explorer starts with them). The rule lives in
`IsExplorerTypeAheadKey` (called first in `RouteHotkeyAsync`); the registry entry `explorer.typeAhead` documents it.

## Keyboard

| Behaviour | Windows | Glide now | Gap | Effort |
|---|---|---|---|---|
| Type-to-select | Letter selects next name starting with it; same letter cycles; several letters within ~1 s match a prefix | **Done.** Existed (`ExplorerSurfaceTextInput`) but the first match was skipped with nothing selected, and every letter that is a default hotkey (F, O, U, R, 1-4 ...) never reached it. Now: Windows-wins rule above, `-` `.` `,` digits and Space-in-prefix too, ignored in text boxes and for Ctrl/Alt chords | none | done |
| F2 rename | Renames focused item (multi-select: batch rename) | F2 renames the focused/selected item in place (one item; with a multi-selection the focused one) | no batch rename | M |
| Tab / Shift+Tab inside rename box | Commits and starts renaming the next / previous item | Not handled | missing | S |
| Ctrl+A | Select all | Fixed `Ctrl+A` | none | - |
| Ctrl+Shift+N | New folder, rename box open | Fixed `Ctrl+Shift+N` (wins over "duplicate window" in Explorer) | none | - |
| Backspace / Alt+Left | Back | Hotkey map: `nav.backOrPrevious` / `nav.back` (tab history) | none (rebindable) | - |
| Alt+Up | Up one folder | Fixed `Alt+Up` | none | - |
| Alt+Right | Forward | Hotkey map `nav.forward` (`Shift+Backspace` too) | none | - |
| Alt+Home | Home folder | Not handled | missing | S |
| F5 / Ctrl+R | Refresh | Fixed `F5 / Ctrl+R` | none | - |
| Enter | Open selection (all selected items) | Opens when exactly one item is selected; with several, nothing happens | multi-open missing (Glide would open many tabs; deliberate caution) | S (needs a limit) |
| Ctrl+Enter / Shift+Enter | Open in new window / run as admin | Not handled (Glide has middle-click = other open action) | missing | S |
| Shift+F10 / Menu key | Context menu at the focused item | Fixed `Menu key / Shift+F10` | none | - |
| Alt+Enter | Properties (folder when nothing is selected) | Fixed `Alt+Enter` (selection or folder) | none | - |
| Ctrl+C / Ctrl+X / Ctrl+V | Clipboard file operations | Fixed, real file clipboard | none | - |
| Ctrl+Insert / Shift+Insert | Copy / paste | **Done.** Fixed `Ctrl+C / Ctrl+Insert`, `Ctrl+V / Shift+Insert` | none | done |
| Ctrl+Shift+C | Copy as path | **Done.** Copies the full path(s) as text (unquoted, one per line, like the context menu's "Copy path"; Windows adds quotes) | quoting differs | done |
| Ctrl+Z / Ctrl+Y | Undo / redo file operation (rename, move, copy, delete, new folder) | Not present (no operation history; delete asks first, permanent delete is permanent) | missing | L |
| Delete | Recycle Bin (no prompt by default; "Show recycle confirmation" optional) | Confirms ("Move 'x' to the Recycle Bin?") every time, then recycles; selects the next item | extra confirmation (deliberate, no setting to skip) | S (setting) |
| Shift+Delete | Permanent delete after confirmation | Fixed, confirms | none | - |
| Arrow keys | Move selection; Up on first row / Down on last row stay | **Done.** Up on the first row used to jump to item 0, Down on the last row to the last item; they now stay. Down from a full row onto a shorter last row lands on its last item (as in Windows) | none | done |
| Left / Right in Details | Scroll columns horizontally | Ignored in Details (one column) | n/a | - |
| Home / End | First / last item (Windows icon views: first / last item of the folder) | First / last item; Shift+Home / End extend to row start / end | none | - |
| Page Up / Page Down | First press moves to the top / bottom visible item, next press pages | Always pages by one screen | slightly different | S |
| Shift+arrows / Ctrl+arrows | Extend range / move focus only | Shift extends from the anchor; Ctrl moves the focus rectangle only. Ctrl+Shift+arrow extends (replaces) rather than adding to the old selection | minor | S |
| Ctrl+Space | Toggle focused item | Fixed `Ctrl+Space` | none | - |
| Plain Space | Selects the focused item | Not handled (Space belongs to the viewer's "next image", a no-op in Explorer) | missing; conflicts with the user-bindable Space hotkey, so left as a recommendation | S |
| Esc | Cancels rename / drag, does not clear selection | Esc clears the selection when something is selected (round 3 #9, deliberate) | different on purpose | - |
| F3 / Ctrl+F / Ctrl+E | Focus the search box | **Done.** `Ctrl+F` existed; `F3` added (fixed `Ctrl+F / F3`). `Ctrl+E` is Glide's "Explore/Browse containing folder" default, so it is not taken | Ctrl+E conflicts with a Glide hotkey | done / - |
| F4 / Ctrl+L / Alt+D | Address bar | Fixed `Ctrl+L / Alt+D / F4`, also from inside text boxes | none | - |
| F6 | Cycle focus (address bar, list, navigation pane) | Not handled; `F6` is the second default key of "Rename current file" (viewer-only) so it is a no-op in Explorer | missing; needs a decision about the F6 hotkey | M |
| Ctrl+Shift+1..8 | Extra large / large / medium / small icons, list, details, tiles, content | **Done** for the six views Glide has: `Ctrl+Shift+1..6` = Thumbnails XL, L, M, S, List, Details. No tiles / content views | tiles and content views do not exist | done |
| F11 | Fullscreen | Hotkey `F11` / `Enter` (viewer); F11 works from an Explorer tab | none | - |
| Ctrl+W / Ctrl+N / Ctrl+T | Close window / new window / (Win11) new tab | Hotkey map: close tab / new window with current picture / new tab | `Ctrl+N` is "same image", not "same folder" | S |
| Ctrl+Tab | Next tab (Win11) | Hotkey map | none | - |
| Alt+P, Alt+Shift+P | Preview / details pane | Info lines setting exists, no pane shortcut | missing | M |
| `*` on the numeric pad | Expand all in the tree | No tree (drive bar + Quick access instead) | n/a | - |

## Mouse

| Behaviour | Windows | Glide now | Gap | Effort |
|---|---|---|---|---|
| Click / Ctrl+click / Shift+click | Select / toggle / range | Same, anchor-based | Ctrl+Shift+click should add a range to the selection (Glide replaces it) | S |
| Press on a selected item of a multi-selection | Keeps the selection for a drag, reduces on release | Same | none | - |
| Rubber band | Drag on empty space; Ctrl toggles, Shift adds | Same | none | - |
| Double-click item | Open | Open (gesture table, `explorer.doubleClick`, default current tab) | none | - |
| Double-click empty area | Nothing (Win11) | Nothing | none | - |
| Click-to-rename | Slow second click on the name | Same, setting `explorer.clickToRename` | none | - |
| Ctrl+wheel | Steps the icon size | Same (XL ... Details), unless the wheel gesture row is reassigned | none | - |
| Shift+wheel | Horizontal scroll | Not handled (vertical list only) | n/a | - |
| Mouse back / forward buttons | Back / Forward | Gesture rows `explorer.backButton` / `explorer.forwardButton` default Back / Forward | none | - |
| Middle-click | (nothing on files) | Open in a new tab (Glide extension) | extension | - |
| Right-click | Selects item if unselected, menu on release; empty space clears the selection | Same | none | - |
| Drag and drop inside the list | Same drive: move; other drive: copy; Ctrl copy; Shift move; Alt shortcut | **Done.** Used to be move always, Ctrl copy. Now `Ctrl` copy, `Shift` move, default move on one drive and copy to another (`ExplorerDropIsCopy`). No Alt / Ctrl+Shift shortcut creation | no shortcut creation | done (shortcuts: M) |
| Right-button drag | Menu "Copy here / Move here / Create shortcuts here" | Not present (right-drag is a no-op in the list) | missing | M |
| Drop files from Windows Explorer onto the list or a folder tile | Copy / move into that folder | Not supported: an outside drop opens the first item as an image tab / folder tab (`DropReceived`) | missing; today's behaviour is "open", not "copy" | M |
| Drag out to other apps | File drag | Best effort via storage items (move / copy) | none | - |
| Drop on a folder tile or drive-bar button | Move / copy into it | Supported (`AttachExplorerDropTarget`) | none | - |
| Spring-loaded folders (hover while dragging) | Opens the folder | Not present | missing | M |

## Implemented in this change

1. Type-to-select (fix, Windows-wins rule, digits, `- . ,`, Space inside a prefix, first-item bug, ignored in text boxes / Ctrl / Alt chords).
2. `F3` focus filter box. 3. `Ctrl+Shift+C` copy path. 4. `Ctrl+Insert` / `Shift+Insert`. 5. `Ctrl+Shift+1..6` view modes.
6. Up on the first row / Down on the last row stay put. 7. Drop effect: cross-drive copy, Shift = move.

## Recommendations (not done)

1. Plain Space selecting the focused item (S) and Tab-in-rename-box moving to the next item (S).
2. External file drops into the list / folder tiles copying or moving (M), with right-button drag menu (M).
3. Ctrl+Z / Ctrl+Y undo of rename, move, copy and new folder (L); needs a small operation journal in `ExplorerFileOperations`.
4. `F6` focus cycling and `Ctrl+E` focus filter: both collide with Glide hotkey defaults (`file.rename` second key, `folder.exploreParent`); decide whether Explorer-context fixed keys should override them like `Ctrl+C` does.
5. Optional "confirm delete" setting for the Recycle Bin path (S); multi-item Enter with a safety limit (S).
6. Page Up / Page Down first move to the top / bottom visible item (S); Ctrl+Shift+arrow adds to the selection (S).
