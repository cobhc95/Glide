# Changelog

All notable changes to Glide Image Viewer are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project uses dotted feature releases with
hyphenated follow-up corrections (for example `4.2.0-1`).

## [5.0.0] - 2026-11-14

A major release: much faster opening of large images, a built-in Explorer tab, one panel for every mouse gesture, one master list of actions so almost everything (including 132 settings) can be put on a key or a mouse gesture, Settings in collapsible cards in a smaller window that remembers its size, a status bar that reveals exactly where it appears, a Reset Glide card, a print window that no longer lags, a window that remembers where every dialog was, animated-GIF controls, and a broad security and robustness pass. 4.8.0 was never published, so everything developed for it is part of this release.

### Added

- **Explorer tab.** Glide's own folder browser is back as a tab type: browse folders and supported images, go up / back / forward, type a path, and open images without leaving Glide. Open one from the new-tab (+) button's right-click menu, a tab's right-click menu, *New tab opens: Explorer tab*, or *Home page: Explorer page*. Opening a folder from Windows now shows it in an Explorer tab.
- **Explorer tab as a file browser.** Breadcrumb address bar (click a segment, click the empty part to type a path, inline error for a path that does not exist, Esc cancels), history dropdown, Back / Forward (Alt + Left / Right, Backspace = Back or Previous image) and Up (Alt + Up), F5, a navigation pane with Quick access and drives, a status line, a filter box (Ctrl + F), Extra large / Large / Medium / Small icon views, List and Details views with sortable, resizable columns, and *Sort by* / *Group folders first*. Folders show a folder icon and images show real thumbnails (loaded in the background, only for visible tiles, released when scrolled away). Selection works with click, Ctrl / Shift, rubber band, Ctrl + A, arrows, Home / End, PageUp / PageDown and type-to-jump; selection, scroll position, view, sort and filter survive switching tabs.
- **Explorer file operations.** Right-click menus for a file, a folder, several items and the empty background (Open, Open in new tab, Open with, Show in Windows File Explorer, Cut, Copy, Copy path, Paste, Rename, Delete, New folder, View, Sort by, Select all, Properties), inline rename (F2), Delete (Recycle Bin, with confirmation) and Shift + Delete (permanent, stronger confirmation), Ctrl + C / X / V with the real Windows file clipboard (name clashes become *name - Copy*, errors are reported, nothing blocks the UI), Ctrl + Shift + N for a new folder, and drag and drop between folders (move; Ctrl copies). Typing in the Explorer's text boxes no longer triggers viewer hotkeys.
- **Explorer open actions** (Settings > Mouse & keyboard > Explorer): double-click and Enter open an image in the current tab (default) or in a new tab; middle-click opens it in a new tab (default) or the current tab. Middle-clicking a folder opens it in a new Explorer tab.
- **Explorer tab session restore.** *Open last session* brings an Explorer tab back exactly as you left it, even after a crash. Each Explorer tab's folder, selected file and scroll position are saved about a second after every navigation, selection change or scroll (not only when Glide closes), using an atomic write so the session file is never left half-written. On the next launch the tab reopens in that folder with the file selected and scrolled into view. If the folder is gone or cannot be read, Glide opens the nearest parent that exists; if the file is gone, the folder opens with nothing selected. Older session files still load.
- **Explorer tab settings** (Settings > Tabs & status bar): *Explorer default view*, *Show hidden files in Explorer tab* and *Explorer tab lists only folders and images*.
- **Mouse gesture panel.** Settings > Mouse & keyboard now has one compact panel for every mouse input: pick an area on the left (Windowed image, Fullscreen image, Empty background, Selection, Slideshow, Window in Window, Tabs, Title bar, Explorer) and choose what each click, double click, drag and wheel does there. Unchanged rows say what they do as *Default (...)*; rows changed from their default are outlined in the accent colour, show a dot and are counted on the area. The panel has its own search, a *Changed only* filter and *Reset area*; *Reset all gestures* resets everything. Each row's action list is a dropdown field with *Find an action*, *Default* and *Do nothing*, *Suggested* actions for that input, your *Recent* picks and *All actions* grouped by category. A wheel row offers only direction-aware wheel actions, a drag row only continuous actions (pan, selection, move window), and no row offers destructive commands (Delete, Close Glide) or commands that open a dialog (Print, Rename, Save). The selected area's options (for example *Reverse mouse-wheel direction*, the drag behaviors, the selection buffer and the caption buttons) and its *Custom* block sit full width under the table.
- **New gesture areas and inputs.** Tabs (right / middle / double click, wheel over the tab strip), Title bar (right / middle / double click, wheel, plus the caption-button actions), Slideshow (left / right / middle click and wheel while a slideshow runs), Explorer, a fullscreen double-click row, a Window in Window right-drag row, and Shift + wheel and Alt + wheel in windowed and fullscreen (both default to behaving like the plain wheel, as before).
- **Mouse side buttons (back / forward).** *Mouse back button* and *Mouse forward button* are new gesture inputs in the Windowed image, Fullscreen image, Tabs and Explorer areas, and can be used by custom gestures (with an optional Ctrl, Shift or Alt). By default they go Back / Forward in the tab's history (the rows read "Default (back)" / "Default (forward)"; the Welcome page follows the Windowed image rows). Choose *Do nothing* or another action in Settings > Mouse & keyboard to change that. Settings search finds them by *side button*, *mouse back*, *XButton1* and *mouse 4 / mouse 5*. Older settings files load unchanged.
- **Back / Forward in every tab, like a web browser or Windows 11 File Explorer.** Open an image from the Explorer tab and press Back: the Explorer comes back in the same folder, scrolled as it was, with the image selected. Forward shows the image again. Home -> image in the same tab -> Back returns to Home. Each tab keeps up to 100 steps, and a tab dragged to another window or detached keeps its history. If a Back / Forward target is gone, Glide skips a deleted image or opens the nearest existing parent of a deleted folder, with a short message. See `docs/NAVIGATION-HISTORY.md`.
- **New actions Back (`nav.back`) and Forward (`nav.forward`)** in the master action list: hotkey rows, gesture actions for every click row, title-bar buttons (new arrow icons) and Settings search (*back*, *forward*, *history*, *previous page*, *navigate*, *undo navigation*). Default keys: Back = `Alt+Left`; Forward = `Shift+Backspace` and `Alt+Right`. A third new action, *Back, or Previous image* (`nav.backOrPrevious`), owns `Backspace`: it goes Back through the tab history when there is a step to go back to (an image opened from the Explorer returns to that folder with the image selected), otherwise in an image tab it shows the previous image, otherwise (an Explorer or Home tab without history) it does nothing; it never fires while typing in a text box. *Back* (`Alt+Left`, the mouse back button) is strict and never falls back to *Previous image*; *Previous image* no longer uses Backspace (Left arrow, Page Up unchanged).
- **Title bar:** Back and Forward are the first two buttons of the default title bar. They are dimmed when there is nothing to go back / forward to, and their tooltips show the shortcut you have assigned (for example *Back (Alt+Left)*). Previous / Next image remain available in *Customize title-bar buttons*. The Explorer history dropdown also lists an image opened from the tab (and Home) and jumps there.
- **Always stack the information on a separate line.** New status bar option (Settings > Tabs & status bar > Status bar, and the status bar's right-click menu, next to Auto-resize). On by default: the file info (name, position, size, zoom) sits on its own line at the top of the bar with the minimise and close buttons at its right, and the button groups sit on the row below, even in a wide window where everything would fit on one line. Switch it off to get the previous single line in wide windows. It is searchable in Settings (*stack*, *separate line*, *two rows*, *wrap*, *single line*) and, like every on/off setting, has a hotkey / gesture action (*Always stack the information on a separate line*). The Home page status bar follows it too.
- **New gesture actions**, offered only where they make sense for the input: rotate and flip, first / last image, jump 10 images, next / previous folder, zoom to 200%, new / close / reopen / duplicate / move tab, show or hide the status bar, always on top, maximize / restore and minimize (title bar), start / pause and stop slideshow, copy image, show in File Explorer, and direction-aware wheel actions: pan up / down, pan sideways, jump 10 images, previous / next folder, rotate and previous / next tab.
- **Custom gestures.** Up to 6 per area: an input, an optional key to hold (Ctrl, Shift or Alt) and an action. A custom gesture overrides the built-in row for the same input and key; the first match wins.
- **Settings search for gestures.** Every row and every custom gesture is its own search result with the area, a breadcrumb and the live dropdown (for example *scroll window* or *middle click tab*); *Open* jumps to the row.
- **One master list of actions.** Every function a user can trigger is now one action in a single registry (more than 260 actions), and the hotkey list, mouse gestures, title-bar buttons, status-bar buttons, context menus and Settings search are all built from it, so they can no longer disagree. See `docs/ACTIONS-AND-HOTKEYS.md`.
- **Settings can be put on a key or a gesture.** Every on/off switch and every choice setting (132 of them) is now an action you can bind in the hotkey list or the gesture panel (*Toggle: Auto-hide status bar*, *Cycle: Status bar size*, *Toggle: Shuffle*, ...). The change applies at once and is saved, exactly as in Settings. A few settings that need Settings itself (Windows integration, startup, title-bar button actions) are left out on purpose.
- **22 more commands have a hotkey row and a gesture.** These could only be clicked before: Maximize / restore, Minimize, Clear all overlays, Close the status bar, New Explorer tab, New Welcome tab, Open first image in folder, Enter / exit Overlay mode, Customize title-bar buttons, Show folder in Windows File Explorer, Reset window transparency, Clear recent history, Cycle Recent list size, Explorer back / forward / up, Export selected region, Reset / bring to front the selected overlay, Jump 10 images and Zoom to 200%.
- **Fixed keys are listed too.** The hotkey list and Settings search also show the keys you cannot change (Explorer's Ctrl + C / V / F2 / Delete / Alt + arrows..., Space pausing a slideshow, overlay keyboard zoom), marked as fixed, and *Search hotkey* finds them.
- **More title-bar buttons to choose from** (rotate, flip, fit width / height, status bar, save as, Overlay mode and others), and context menus show the current shortcut of every item.
- **Exit Glide action.** A hotkey action *Exit Glide* (Settings > Mouse & keyboard > Keyboard shortcuts > Application, no default key) quits the whole program (see Changed).
- **Reset Glide** (Settings > Windows & developer, a new card after Diagnostics). Wipe exactly the Glide data you choose and start fresh, for example before reporting a bug, after a bad settings experiment or as an uninstall clean-up. Ten tick boxes in three groups: *Settings* (all settings; hotkeys and mouse gestures including custom presets and custom gestures; saved profiles and presets; window sizes and positions), *History and data* (recent files and Home list; last session; image and thumbnail caches; downloaded video codec pack) and *Windows integration* (Glide's per-user Open With, file-type and Explorer thumbnail registry entries; startup and background entries). *Select all*, *Clear*, a live *N selected* count and a red *Reset N selected...* button. A confirmation window lists exactly what will be removed; Cancel has the focus, Esc cancels and Enter never starts the reset. *Save a backup of my settings first* is ticked by default and copies settings, profiles, last session, history and the small settings files to `Documents\Glide backups\Glide backup <date time>` before anything is deleted (if the backup cannot be written, nothing is reset). Items Glide holds in memory are wiped after Glide closes: Glide closes, a short windowless helper (the same `Glide.exe`) waits for it to exit, deletes the data and starts Glide again; the dialog says *Glide will close and restart* only when needed. A file that stays locked is reported by name with the reason, other items still run, and you are told what was not reset. It works for the installed and the portable copy and touches only Glide's own locations (settings folder, `%LOCALAPPDATA%\Glide`, Glide's temp files, `HKCU\Software\Glide` and Glide's own HKCU Open With / thumbnail / Run entries). Never touched: `HKEY_LOCAL_MACHINE` registrations, Windows default-app choices and Windows' thumbnail database, crash logs, the program folder, the installer, your pictures, exported settings, profile files and backups. Settings search finds the card and every item (*factory reset*, *wipe*, *restore defaults*, *uninstall cleanup*, ...). The *Restore defaults* button in the Settings footer is a different feature; it only resets the options shown in the window.
- **Every window remembers where you left it.** Resizable windows (Settings, Print, Customize title-bar buttons, Diagnostics results) remember size, position and maximized state; fixed-size dialogs (confirmations, the Reset dialogs, hotkey and profile prompts, rename, slideshow start, folder-boundary and close prompts, the decoder download) remember their position. Saved when the window closes by any route, restored before it appears, and validated against the monitors present now (DPI-aware: an unplugged or off-screen monitor centres the window on its owner, a window that is too large is clamped to the working area, too small becomes the minimum size). *Reset Glide > Window sizes and positions* forgets them. Older settings files load unchanged.
- **Animated GIF controls.** Pause / play (`P`) and step to the previous or next frame (`,` and `.`; stepping pauses first and wraps at either end). Editable in Hotkeys, with viewer right-click menu items and status-bar buttons, all shown only while an animated GIF is open.
- **Auto-hide status bar.** Works like the Windows taskbar's auto-hide: the status bar is fully hidden (no arrow, no button) and appears when the pointer enters exactly where it would appear (see *Status bar reveal area* below). Turn it on with *Auto-hide status bar* in the status bar's right-click menu or in Settings > Tabs & status bar > Status bar. Off by default.
- **Status bar reveal area.** A collapsed or auto-hidden status bar now reveals when the pointer enters exactly the box the bar occupies when it is shown: a thin strip in a wide window, a taller box when the bar wraps into several rows in a narrow window, following its Left / Center / Right position, on the viewer and on the Home page, windowed and fullscreen. The area is read from the live layout (or measured while the bar is hidden or collapsed to its corner button), so it follows resizing, wrapping and size. It is not drawn, does not block clicks or gestures on the image, and does not trigger while a mouse button is down. A revealed bar stays while the pointer is over it (plus a small margin), over the spot the collapsed button occupied, or while one of its menus is open, and hides about 0.6 seconds after the pointer leaves the bar (leaving the window hides it at once). New setting *Status bar reveal area adjustment* (Settings > Tabs & status bar > Status bar, -40 to +300 DIP in steps of 5, default 0 = exactly the bar's size): positive values grow the area outward on every side (never past the window), negative values shrink it (never below 6 DIP tall and 40 DIP wide).
- **Reserve status bar space when fitting** setting (off by default), also in the status-bar right-click menu: Fit, Fit to window, Fit width, Fit height and the default view size the image to the area above the visible status bar, and re-fit when the bar collapses, expands, hides or changes size.
- **Tab tooltip text size** setting (Small, Medium, Large). The tab tooltip now shows the file name on its own line in larger text, then the full path and the mouse hint.
- **Hotkey list sorting.** Click the Shortcut, Action or Category header in Settings > Mouse & keyboard > Keyboard shortcuts to sort the list; click the same header again to reverse it. An arrow shows the sorted column, and unassigned rows always stay at the bottom when sorting by shortcut.
- **Search hotkey.** In the same card, press *Search hotkey*, then press any key or combination: the list shows the action it is assigned to, or says that it is not assigned. Esc cancels, and the keys you press never run a command. *Show all hotkeys* returns to the full list.
- **Custom hotkey presets.** *Save current as preset...* saves your keyboard shortcuts under a name. Your presets appear after the built-in ones in the hotkey preset list and can be applied, renamed (*Rename preset...*) and deleted (*Delete preset*); the built-in presets cannot be renamed or deleted. Custom presets are part of your settings, so they are included when you export or import a profile. Older settings files load unchanged.
- **Larger images.** Images up to 512 megapixels (for example 32000 x 16000 or 22600 x 22600) now open at full resolution, subject to free memory (see Security).

### Changed

- **Settings are organised into collapsible cards.** Every Settings page is now a set of titled cards (for example *Startup and new tabs*, *Text overlay*, *Status bar look*) that open and close with a click or the keyboard; every card starts collapsed. *Expand all* and *Collapse all* act on the current page, cards keep their open or closed state while Settings stays open, and a card shows an *N changed* badge (with the list on hover) when settings in it differ from their defaults. Search results now say where a setting lives (*Page > Card*), and every result has *Open*, which switches to the page, opens the card, scrolls to the setting, highlights it briefly and focuses it.
- **The Settings window is smaller, tighter and remembers its size.** It now opens at 930 x 630 (was 1040 x 760) with a minimum of 720 x 480 (was 760 x 560), so it fits small and high-DPI displays; on a screen where that would not fit it opens no larger than 90 percent of the usable area, fully on screen and centred over the Glide window. The empty space is gone (navigation column 224 wide instead of 250, a normal 20 DIP margin at the right of the page instead of 38 to 48, shorter header and footer rows, 44 DIP category buttons), and narrow windows work: label and editor rows shrink the label first and then stack, hint texts move under the editor, button groups wrap, the gesture table switches to a narrower area list and nothing runs past the page at 720 wide. Its size, position and maximized state are saved when it closes and restored next time (checked against the monitors present now, never below the minimum); the compact default applies only when nothing is saved, and sizes saved by older versions are used as they are.
- **Settings moved to where they belong.** The folder position line (*Show folder position line under the text overlay*, *Include the parent folder's position*) moved from the navigation rate limit to the *Text overlay* card, together with the overlay's switch, template and style; all status bar settings are together on *Tabs & status bar* (when it shows, its buttons, its information and its look); the tab settings moved from *Interface* to *Tabs*; *Remember window position* and the fullscreen cursor settings are under *Window & navigation*; the Escape and Tab key options are under *Mouse & keyboard*; *Speed Boost* and its tray icon are under *Windows & developer > Background and Windows startup*; the startup timing log is under *Diagnostics*; *Reverse selection-to-zoom-out scale* is in the gesture panel's *Selection* area; overlay opacity, selection highlight and absolute overlay coordinates are with the other *Window in Window* settings; and *Home page* is under *Startup and new tabs*. Setting values and saved files are unchanged.
- **The page *Interface & window* is now *Window & navigation*.** Settings search filters by the same page names as the navigation.
- Clearer wording for several settings, for example *Show status bar* (was *Show status surface*; the old words still find it), *Show text overlay* and *Text overlay font size*, and *Open the first image when entering a sibling folder (off: the last image)*.
- **Mouse toggles are now gesture rows.** *Double-click enters / exits fullscreen*, *Fullscreen clicks navigate*, *Windowed wheel zoom*, *Ctrl + wheel zooms*, *Left-drag action*, *Middle-drag pans*, the two selection zoom toggles, the overlay wheel-zoom and right-drag toggles, *Left-click during slideshow*, *Right-click stops slideshow*, the two tab-closing toggles and *Explorer: open images with* moved into the matching rows. Settings are migrated once on upgrade (settings schema 7), so everything behaves exactly as before; imported profiles and settings files from earlier versions are migrated the same way. *Reverse mouse-wheel direction*, *Left-drag window behavior*, *Right-drag behavior*, the selection-buffer options, *Drag the Home page background to move the window* and the caption-button actions remain options, now shown in their area of the panel.
- **New installs default to Maximum quality browsing** (existing settings are kept).
- **The title-bar *Show in File Explorer* button now opens Glide's own Explorer tab by default** (the image's folder with the image selected); right-click it to switch back to Windows File Explorer. The choice is also in Settings > Window & navigation (*Show in Explorer button opens*).
- **Assigning a shortcut that is already in use now asks first.** Glide names the action that currently owns the shortcut (and says whether it works in every tab or only while viewing an image) and asks *Replace* or *Cancel*. Replace removes the shortcut from the old action; Cancel changes nothing. Assigning a shortcut an action already has does nothing.
- **The hotkey action *Explore containing folder* is now *Explore/Browse containing folder*** and can be found in Settings search with *explore*, *browse*, *folder*, *reveal*, *show in explorer*, *containing* and *locate*.
- **The collapsed status bar's expand button is a very small, unobtrusive arrow** when *Collapsed button in lower-right corner* is off. It rests on a subtle rounded 26 x 20 backing (about 30 percent dark grey with a thin white border) with a slightly larger, bolder arrow, so it reads on dark and bright images; hovering shows the full framed button. It stays exactly where the collapse button was.
- **Hovering where a collapsed or auto-hidden status bar would appear reveals it by default.** *Reveal the collapsed status bar when the pointer enters where it would appear* is now on by default (a settings file that already saved the option keeps its value). It lives only in Settings > Tabs & status bar > Status bar, next to *Auto-hide* and the reveal area adjustment, and is no longer in the status bar's right-click menu (*Auto-hide status bar* stays there).
- **Viewport zoom step now works.** *Viewport zoom step* (Settings > Appearance & viewing, 1-100%, default 15%) sets how much each Zoom in / Zoom out key, menu item, title-bar or status-bar button zooms, and takes effect immediately. The mouse wheel (and Zoom in/out gestures and whole-app overlay mode) uses 80% of the step, which is exactly the previous 15% (keys) and 12% (wheel) at the default. Minimum and maximum zoom (5% to 3200%) are unchanged. The old default of 10% never had an effect, so a stored 10% moves to 15% once (settings schema 8); any other value you set is kept.
- **Fit hotkeys follow *Fit control scope*.** Fit image (Shift + W), Fit to window (4), Fit width (2), Fit height (1) and Actual size / 100% (3, Ctrl + H) now behave exactly like the status bar buttons: depending on *Fit control scope* they apply to the current image only, to every image this session, or to this session and future launches (the default, which also updates *Default view for new images*). The same applies to the viewer menu items, title-bar buttons and gestures that run these commands.
- **Exit Glide really exits.** The viewer menu item (now *Exit Glide*, was *Exit program*, which only closed the current window) and the *Exit Glide* hotkey action close every Glide window and end the process; the tray icon and Speed Boost standby cannot keep it alive. With several image tabs open the usual *Close Glide tabs?* confirmation applies (close current tab / close all / cancel, or your remembered choice). *Close Glide window* (Ctrl + Shift + W) still closes only the current window.
- **Explore/Browse containing folder now has the default shortcut Ctrl + E.** It shows the current image in Glide's Explorer tab or in Windows File Explorer, following *Show in Explorer button opens*. A settings file where the action has no key gets Ctrl + E once (settings schema 10) unless another action already uses it; a key you assigned is kept, and the title-bar *Show in Explorer* tooltip shows your current key.
- **Show / hide status bar is now Ctrl + Shift + B** (it was Alt + Shift + S, which is also Windows' input-language switch). A settings file whose binding is still the old default moves to the new key once (settings schema 8); a shortcut you changed to anything else, or removed, is kept. If you had deliberately chosen exactly Alt + Shift + S before this version, it also moves and can be set again in Hotkeys. Tooltips show the key you currently have assigned.
- **Removed three settings that had no effect** (the back and forward tab-bar buttons and the folder *Explore* button). They could still be found in Settings search but changed nothing; saved settings files still load.
- **The status bar is taller by default.** Because the info line now has its own row, the default bar is about one row taller than before in wide windows (two rows instead of one; narrow windows already wrapped this way and look the same). Existing settings files that have no saved value for *Always stack the information on a separate line* get the new default (on); turn it off to get the old look back. The collapsed button, *Reserve status bar space when fitting* (the reserved area is now the taller bar) and the auto-hide / collapsed hover reveal area (exactly the taller bar box) all follow it and re-measure the moment the option is toggled. A file name longer than the bar wraps at most onto a second line and ends in an ellipsis when the info line is stacked, instead of pushing the bar wider.
- **Explorer keys and history.** `Alt+Left` / `Alt+Right` are no longer fixed Explorer keys but the editable defaults of Back / Forward, and they now also step across an image opened from the tab; `Alt+Up` stays *Up one folder*. The Explorer toolbar Back / Forward buttons, the history dropdown and the *Explorer: back / forward* actions use the same tab history as the title-bar buttons.
- **Settings schema 9.** A title bar still on the previous default layout gains Back / Forward once; customised layouts are kept exactly as they are. Saved shortcut maps get the new Back / Forward keys unless another action already uses them (your binding wins).
- **Behaviour changes to know about** (details under Fixed and Security):
  - **Ctrl + Shift + B** replaces Alt + Shift + S for show / hide status bar (schema 8 moves an unmodified old default).
  - The **zoom step** setting works; its default is **15%** and a stored 10% moves to 15% once, so the default zoom feel is unchanged.
  - The Fit hotkeys (1, 2, 3, 4, Shift + W, Ctrl + H), the menu items and gestures, and the *default size* commands now **follow *Fit control scope*** like the status bar buttons; with the default scope they also update *Default view for new images*.
  - **Exit Glide** quits the whole app (all windows, tray icon and Speed Boost included); **Close Glide window** closes only one window.
  - The **status bar reveal area is exactly the bar's own box** (it was a thin strip along the bottom edge), and **hover reveal is on by default** and no longer in the status bar's right-click menu.
  - **Image shortcuts only work while a picture is shown** (they used to act on the picture of another tab from an Explorer or Welcome tab).
  - **Settings opens smaller** (930 x 630) and every window now **remembers its size and position**; a window that was saved on a monitor you no longer have opens centred.
  - **Mouse side buttons go Back / Forward by default** (they used to do nothing until bound). Choose *Do nothing* in Settings > Mouse & keyboard to get the previous behaviour back; rows you bound and custom gestures are unchanged.
  - **Backspace now goes Back instead of Up in the Explorer list, and is no longer plain *Previous image* in an image tab** (use `Alt+Up` or the Up button for the parent folder). `Backspace` is *Back, or Previous image* (`nav.backOrPrevious`): it goes Back through the tab history when there is a step to go back to (an image opened from the Explorer returns to that folder with the image selected), otherwise in an image tab it shows the previous image, otherwise (an Explorer or Home tab without history) it does nothing; it never fires while typing in a text box. *Back* (`Alt+Left`, the mouse back button) is strict and never falls back to *Previous image*; *Previous image* no longer uses Backspace (Left arrow, Page Up unchanged). The old *Previous image (Backspace)* row keeps its id (`nav.previousBack`) and is now labelled *Previous image (extra key)* with no default key. The IrfanView, nomacs, FastStone and XnView MP shortcut presets keep Backspace on *Previous image*. Settings schema 11 moves an untouched old Backspace default once (never a binding you changed or removed, never a key another action uses).
  - Moving from image to image (arrow keys, wheel, slideshow, sibling folders, Open file in the same tab) is not a history step; an image opened in a new tab starts without history (Back is dimmed). Back / Forward never fire while typing in the Explorer address bar, filter box or rename box. History is kept while Glide runs; *Open last session* restores Explorer folder history as before (images and Home steps are not saved).
  - **The status bar is taller by default** (the info line has its own row; *Always stack the information on a separate line* is on). Turn it off for the old single line.
  - Shortcuts that were saved with their modifiers in another order (for example *Alt+Shift+...*) now work; a shortcut that silently did nothing before may start working.
  - With *When Glide opens = Custom* or *New tab opens = Custom*, a **folder** path now opens that folder in an Explorer tab (it used to open the Welcome page).
  - A Glide started as administrator no longer hands images to a normal (non-elevated) Glide; it opens its own window.
  - A running 4.x and a 5.x Glide no longer hand images to each other (the single-instance connection is now per user and per version); each opens its own window. The installer closes all Glide processes before upgrading, so this only matters for side-by-side portable copies.
  - SVG files no longer load images linked from a web address or a network share.
  - Animated GIF frames that are larger than both the animation's canvas and 1 MP are skipped, and animations longer than 10,000 frames play only their first 10,000 frames.
  - The decoded-pixel limit rose from 256 MP to **512 MP**, with a free-memory check before very large full-resolution decodes; a very large image can stay on its sharp preview when memory is low.
  - Importing a profile or settings file keeps this PC's *Open in external program* commands and drops network folder paths (\\server\share); import files over 16 MB are refused.
  - A custom install folder outside Program Files and your own profile (for example `C:\Glide`) becomes writable by administrators only.

### Fixed

- **Right-clicking the empty area around an image showed the context menu immediately,** so right-click + wheel zoom popped the menu up. It now appears on button release, like on the image, and not after a wheel turn or a drag.
- **Shortcuts saved in a different modifier order did nothing.** Glide writes a pressed key as Ctrl + Shift + Alt in that order, but a binding stored as *Alt+Shift+S* was compared letter for letter, so it could never fire, *Search hotkey* said it was not assigned and assigning the same keys elsewhere showed no conflict. Shortcuts are now compared in a canonical form everywhere (key handling, the conflict prompt, *Search hotkey* and presets), so shortcuts saved in any modifier order work, including the old *Alt+Shift+S* of show / hide status bar before it moved to Ctrl + Shift + B.
- **Explorer tab: *Show in Windows File Explorer* opened the Documents folder.** The right-click item (and the Explorer tab's file actions that use it) now opens the file's own folder in Windows File Explorer with the file selected, including paths with spaces, commas and brackets.
- **Open in external program 1 / 2 / 3 used the picture of another tab.** Pressed in an Explorer or Welcome tab (keys, gestures, title-bar buttons), they opened the last picture from another tab. In an Explorer tab they now open the image selected in the list; with nothing selected, or only a folder or non-image file, nothing opens and the Explorer says "Select an image first". In a Welcome tab they do nothing; picture tabs are unchanged.
- **Image shortcuts could act on the picture of another tab.** In an Explorer or Welcome tab, with focus outside the Explorer list, Delete recycled the hidden picture, F2 renamed it, and R, Ctrl + C, Ctrl + P, the arrows and some 30 other image commands acted on it. Image shortcuts, buttons and gestures now only run while a picture is shown.
- **Space and Enter clicked a toolbar button again.** After clicking a toolbar or status-bar button (for example Fit), Space clicked that button again instead of going to the next image, and Enter did not toggle fullscreen. With *Tab key button navigation* off (the default) they now go through the hotkey map first; with it on, the focused button still gets the keyboard click.
- **Ctrl + Tab and Ctrl + Shift + Tab ignored your own key assignments.** They are now resolved through the hotkey map (the tab-cycling by most recent use is unchanged).
- **Settings could not record Tab or arrow-key combinations as a shortcut** (for example Ctrl + Shift + Left), and *Search hotkey* could not look them up. Both now work.
- **The mouse pointer disappeared over right-click menus in fullscreen.** With the fullscreen cursor auto-hide on, menus, sub-menus, drop-downs and flyouts (for example the status bar menu with its Size and Position sub-menus, tab and title-bar menus, the Explorer's View and Sort by sub-menus and its view-mode drop-down) showed no pointer, and the idle timer could hide it while a menu was open. They now always show the normal arrow, idle hiding is paused while any menu is open, and the 3-second idle hiding over the image resumes after the menu closes.
- **Custom startup paths now honour folders.** With *When Glide opens = Custom*, a startup path that is a **folder** opens that folder in an Explorer tab and an image **file** opens that image (before, only image files worked and a folder silently opened the Welcome page). *New tab opens = Custom* behaves the same way. With *New tab opens = Explorer tab*, a new Explorer tab opens exactly the new-tab custom path (a folder, or the folder of an image file); the order is: new-tab custom path, then *New Explorer tab default directory*, then the last Explorer-tab location, then Pictures / your profile. A missing, unreadable or non-image custom path opens the Welcome page (or, in Explorer mode, the usual start folder) and says why on the page or in the Explorer message area, without a dialog. The *File...* picker for the custom paths filters to supported images (with an *All files* choice) and pasted paths may include quotes or `%VARIABLES%`. *Open last session* still wins over a stored custom path and *Welcome tab* ignores it.
- **Print window: Portrait / Landscape and the other layout options lagged or looked broken.** Every orientation click re-read the printer driver (about 0.55 s locally, several seconds for network and Remote Desktop printers). Orientation, scaling, custom scale, keep aspect, auto-rotate, alignment, margins, colour and copies now update the preview instantly (see Performance). Switching back to Portrait after Landscape sometimes did not update the preview, and *Custom scale %* and *Keep aspect ratio* could be enabled for scaling modes that ignore them after the printer settings loaded; both are fixed. Printing itself is unchanged.
- **Explore/Browse containing folder did nothing.** Its hotkey was wired to an empty placeholder. It now does what the title-bar *Show in Explorer* button does: it opens the current image's folder with the image selected in Glide's Explorer tab (default) or in Windows File Explorer, following *Show in Explorer button opens*. The action has no default key; assign one in Settings > Mouse & keyboard > Keyboard shortcuts.
- **Scrolling the hotkey list (and other scrolling lists) could stop working when a tooltip appeared under the pointer.** Turning the mouse wheel now closes any open tooltip first, tooltips for list rows open beside the pointer instead of under it, and tooltip popups no longer take mouse input.
- **Hover-revealed status bar no longer hides while you reach for it.** It stays while the pointer is over the bar, over the spot the collapsed button occupied, or while one of its menus is open; if you linger inside a grown reveal area away from the bar it hides and does not pop back until you leave and re-enter the area.
- **Auto-hide reveal area was about 40 DIP wider than the bar that appears.** It was sized as if the bar still had its collapse button, which an Auto-hide bar does not have. The area is now exactly the revealed bar's box (viewer and Home page, wide or wrapped, Left / Center / Right), and the bar's wrap decision no longer reserves room for that button while Auto-hide is on.
- **Collapsed expand button stays exactly where the collapse button was**, including multi-row status bars and after resizing (when *Collapsed button in lower-right corner* is off).
- **Leaving fullscreen no longer flashes the window at the wrong place** before it moves back to its remembered size and position.
- **Settings remembers each page's scroll position** while the window stays open, so switching pages and back returns to where you were. A newly opened Settings window still starts every page at the top, and search still jumps to the setting.
- **Explorer thumbnail quality settings now work.** *Thumbnail quality*, *Maximum source decode size* and *Prefer embedded previews* were saved but never reached the thumbnail provider, which always behaved the same. *Apply thumbnail settings* now stores them where the provider reads them (`HKCU\Software\Glide\ExplorerThumbnails`) and each preset changes how thumbnails are made: **Fast** uses bilinear scaling, reuses small camera previews and shows the first GIF frame; **Balanced** (default) uses area-averaging scaling and only uses a preview that is at least thumbnail size; **High** decodes at twice the thumbnail size and scales with high-quality cubic filtering. *Maximum source decode size* caps the intermediate decode. Windows keeps thumbnails it has already made, so use *Refresh thumbnails* after changing them.
- **Explorer thumbnails of photos with an embedded preview are no longer sideways or blurry.** The preview is rotated like the photo, letterbox bars are cropped, and a preview smaller than the thumbnail is no longer stretched (except with *Fast*).
- **GIF thumbnails.** A first frame that covers only part of the animation's canvas is drawn in its place instead of filling the whole thumbnail, and an animation whose first frame is blank shows its first visible frame (Balanced and High).
- **Explorer thumbnail status line is accurate.** It reported *Provider: missing* whenever the running Glide.exe had no provider DLL beside it (portable or standalone exe, developer builds) even though Explorer was using the installed provider, and showed Glide's own version as the provider version. It now shows the provider Explorer actually loads, its version, per-user and all-users registration, and how many formats are routed to Glide. The provider DLL now carries a version resource.
- **Custom thumbnail mode no longer turns Explorer thumbnails off.** Choosing *Custom* registered zero formats; it now keeps the formats currently routed to Glide (or starts from Recommended).
- **"Reveal the collapsed status bar" can be turned off again.** The setting had two checkboxes on different pages and was on while either was ticked, so unticking one did nothing. There is now one checkbox, on *Tabs & status bar > Status bar* (the duplicate *Show status bar* checkbox is gone too).
- **The navigation rate-limit note showed the wrong default.** It said *0 = unlimited (default)*; the default is 80 ms.
- **Image cache and bitmap lifetime bugs** found while making bitmap lifetime deterministic (see Performance); bitmaps the viewer still held used to be left to the garbage collector instead of being disposed, which could retain native memory:
  - a frame the cache rejected (for example a 100 MP full frame over a 256 MB budget) was disposed and then still returned to the viewer;
  - stale uncached rapid-browse previews were never disposed;
  - a prepared-cache hit could be evicted and disposed between being returned and being attached to the viewer;
  - a failed startup-preload bitmap creation freed its native buffer twice.
- **Settings no longer creates an empty `%TEMP%\Glide2-UIEvidence-<guid>` folder** every time it opens.

### Performance

Image opening and browsing are much faster for large images, and browsing uses far less CPU and memory. Measured with a headless benchmark harness on an AMD Ryzen 7 9700X (16 logical CPUs, 61.6 GB RAM, Windows 11 24H2) with the factory defaults (Maximum quality, 2400 px preview cap, prefetch depth 15, 999 MB caches), a 2560x1369 viewport, synthetic test images and the median of 5 warm runs (3 for 90 MP and above). "Before" is the untouched 4.8-development source, "after" is this release. The synthetic JPEGs are about half the size of real camera JPEGs, so absolute JPEG times are optimistic; the comparisons are like-for-like. "Settled" is the frame the viewer shows at Fit once refinement finishes.

| Metric | Before | After | Change |
|---|---:|---:|---:|
| Settled Fit frame: TIFF LZW 24 MP (ms) | 585 | 177 | -70 % |
| Settled Fit frame: TIFF LZW 100 MP (ms) | 2333 | 756 | -68 % |
| Settled Fit frame: BMP 24 MP (ms) | 123 | 27 | -78 % |
| Settled Fit frame: WebP 24 MP (ms) | 378 | 184 | -51 % |
| Settled Fit frame: progressive JPEG 50 MP (ms) | 978 | 107 | -89 % |
| Settled Fit frame: JPEG 24 MP (ms) | 83 | 46 | -45 % |
| First frame (warm): JPEG 24 MP / 100 MP (ms) | 36.7 / 118.6 | 37.2 / 117.4 | unchanged (codec-bound) |
| First frame (warm): PNG 24 MP (ms) | 153.6 | 127.3 | -17 % |
| First frame (warm): WebP 800x600 (ms) | 7.9 | 2.6 | -67 % |
| Cold first frame: WebP 24 MP / TIFF 4K / BMP 24 MP (ms) | 237 / 147 / 104 | 129 / 96 / 55 | -46 / -35 / -47 % |
| Full-resolution decode: JPEG 24 MP / 100 MP (ms) | 58.6 / 240 | 47.7 / 193 | -19 / -20 % |
| Peak memory of a full decode, 24 MP / 100 MP (MB) | 184 / 770 | 93 / 385 | -50 % |
| Next / previous step with prefetch, all folders (ms) | 0.13-0.17 | 0.14-0.16 | unchanged (cache hits) |
| Next step without prefetch: 24 MP / 4K / small (ms) | 39.1 / 21.4 / 2.8 | 39.2 / 21.5 / 2.4 | codec-bound |
| CPU to browse 21 x 24 MP images at 500 ms per step (s) | 11.5 | 4.8 | -58 % |
| CPU to browse 21 x 4K images at 500 ms per step (s) | 9.9 | 1.0 | -90 % |
| Held-key burst, then settle to full (24 MP) (ms) | 293 | 282 | -4 % |
| Explorer, 5000 files: visible thumbnails / after a jump-scroll (ms) | 87 / 58 | 68 / 31 | -22 / -46 % |
| App launch to first frame, 100 MP JPEG (ms) | 810 | 744 | -8 % |
| Zoom / pan frame cost (CPU raster, ms) | 5.8-43 | 5.8-44 | unchanged |
| Settings open, context menus, main-window start-up | | | unchanged within noise |

- **Decode pipeline.** PNG, TIFF, BMP, GIF and ICO now decode at source resolution straight away when the frame fits half the decoded-image budget (the first frame used to be a full decode plus a resample anyway, and Fit refinement then decoded it twice more); pixels are identical. WebP keeps its scaled first frame, which is 10-18 % faster. Refinement is skipped when the first frame already covers the viewport at Fit (aspect-aware), and otherwise decodes the full frame directly instead of an intermediate one that was thrown away.
- **Direct native decode.** A new native entry point (`GlideDecodeImageIntoW`) decodes full-resolution, orientation-1 images straight into the bitmap the viewer shows, instead of a temporary buffer plus a copy. Peak memory per full decode halves and the full decode is 6-34 % faster for JPEG, PNG, TIFF and BMP. Rotated images and older native DLLs fall back automatically.
- **Missing Windows codecs are detected.** After three files of one extension fail in the Windows decoder and then decode through the fallback, Glide stops trying the native route first (about 4 ms per file); on the test PC this made 800x600 WebP 7.9 -> 2.5 ms.
- **Startup preloader.** Opening a large JPEG from Explorer decodes the screen-sized frame instead of the full image (100 MP JPEG: first frame about 8 % sooner); refinement still runs afterwards.
- **Prefetch and multi-threading.** Prefetch skips neighbours that are already prepared (it used to re-decode the whole +-15 neighbourhood on every step, about one CPU core of waste while browsing). The nearest neighbours decode in parallel (1 lane on constrained PCs, 2 on mainstream, up to 6 on high-end), the foreground joins a decode already in flight instead of repeating it, stale speculation is cancelled when you jump, and the nearest neighbours are protected from cache eviction by far ones.
- **Explorer thumbnails appear 20-45 % sooner.** Decode lanes scale with the CPU (2 to 6, was a fixed 4), a lane is released before the UI hop, and the per-tile file-information tooltip query is gone.
- **Animated GIFs** stream frames straight into their bitmaps: peak memory halves, and the animation budget is 512 MB, so a 1920x1080 x 60-frame GIF now animates (before it exceeded 384 MB and stayed static).
- **Deterministic bitmap lifetime.** Every cache-owned bitmap handed to the viewer carries a lease and is disposed exactly once when it is no longer shown; stale and rejected frames are released immediately.
- **Print preview is instant.** Printer capabilities are read once per printer, and paper geometry once per printer, paper and source, off the UI thread, and cached for the session (10 minutes; the driver Properties sheet refreshes its printer). Portrait / Landscape went from about 540-570 ms per click (several seconds on a network or Remote Desktop printer) to instant with no driver call; opening Print later in a session takes 35-61 ms (the first time in a session is bound by the driver, about 380-520 ms locally). Changing the paper draws the new sheet at once with an *Updating preview...* badge and refines when the driver answers; rapid printer and paper changes are debounced (120 ms) and the latest choice wins; while the driver is read the preview says *Reading printer settings...* instead of *No printer* and the layout options stay usable. The preview now draws a screen-sized proxy of the image (longest side 2048 px, averaged on up to 4 cores off the UI thread) instead of the full bitmap, so a grayscale preview of a 50 MP image takes about 11 ms instead of up to 256 ms. Print still re-reads the live driver geometry and prints the full-resolution image with the same layout engine; a test proves the preview layout equals the spool layout for every orientation, scaling mode and alignment.
- Not changed: single-image decode is codec-bound (Windows decoders are single-threaded), so first-frame times for JPEG are unchanged; zoom / pan frame cost is unchanged. Known remaining costs: opening the Settings window (50-260 ms, it builds every page at once), the main-window first layout (about 230 ms headless), and the first printer-driver read per printer per session (300-500 ms locally, seconds over Remote Desktop, up to 48 s for the first geometry call of one tested network IPP printer; this is the driver, not Glide).

### Security

- **SVG files can no longer make Glide contact other computers.** An SVG's linked images (`<image>`, `<feImage>`) pointing to a web address (http / https / ftp), a network share (UNC), a host-qualified `file://` address or a relative path are removed before rendering, so viewing or probing an SVG no longer reveals your IP address or sends your Windows credentials (NTLM) to a share. Embedded `data:` images (including nested SVGs, checked up to 3 levels deep), `#fragment` references and fully qualified local drive paths still display. An SVG with a relative image link used to fail to render; it now renders without that image. Updated the SVG rendering library (Svg.Skia 2.0.0.8, the newest line compatible with Glide's current graphics engine).
- **Imported profiles and settings files can no longer choose which programs Glide launches.** Imports keep this PC's *Open in external program* commands (Shift + 1 / 2 / 3), and the status line says *external programs were not imported* when the file named different ones. Import files larger than 16 MB are refused before they are read.
- **Imported profiles and settings files no longer bring network folders.** Startup, new-tab, Explorer-tab, last-used and default folders that point to a network share (`\\server\share`, `\\?\UNC\...`, `//host`, `file:` addresses) are replaced by this PC's local value, or cleared; the status line says *network folder paths were not imported*. Your own local folders are kept. This stops a shared profile from making Glide contact another computer when it starts.
- **Larger images, still safely bounded.** The decoded-pixel limit rose from 256 MP to **512 MP** (65,535 pixels per side), applied on every decode path: the native bridge (including a full decode of an image whose size can only be read by the decoder itself), the built-in decoders, the Skia fallback (size read from the file header before anything is allocated) and the startup preloader. The Explorer thumbnail provider keeps its own lower 256 MP limit, because it never needs a full-size image.
- **Memory-aware full-resolution decoding.** Before decoding a frame larger than 256 MiB (about 67 MP) at full size, Glide checks free memory (physical and commit). If the frame needs more than half of it, or Windows cannot supply the memory during the decode, Glide keeps showing the sharp preview instead of risking heavy paging or a crash. Images up to about 67 MP are never affected by this check.
- **Opening an image with extreme declared dimensions directly from Explorer** (a cold start through the startup preloader) gets the same size and memory protection as every other way of opening it: an image over the limit, or one that would not fit free memory, is never decoded at full size or handed over as raw bytes.
- **Photoshop (PSD/PSB) files** are checked for their pixel data before any memory is reserved for them, so a damaged or truncated file can no longer make Glide reserve a lot of memory for nothing.
- **Malformed animated GIFs can no longer exhaust memory or CPU.** A frame larger than both the animation's canvas and 1 MP is skipped, the way browsers clip frames to the canvas; very long animations play their first 10,000 frames. Previously a roughly 60-byte GIF could force a 2 GB allocation.
- **Single-instance hand-off is private to each Windows user.** The connection a new Glide window uses to hand an image to the running Glide accepts only your own user account, on both ends, and its name now includes a per-user identifier. Another account on the same PC can no longer block or intercept it. A running 4.x and a 5.x no longer hand images to each other, and an administrator-launched Glide opens its own window instead of forwarding.
- **Installer.** It no longer trusts a per-user registry value (HKCU) for the install folder, and the *Launch Glide* option at the end of Setup starts Glide without administrator rights. When Glide is installed to a custom folder outside Program Files and outside your own profile (for example `C:\Glide`), Setup gives the folder the same protection as Program Files: only administrators can change its files, everyone else can read and run them. Installs in Program Files and per-user installs in your own profile work exactly as before.
- **Glide no longer loads program libraries from the folder of the image being opened** (the current directory is removed from the DLL search order at startup).
- **Pasting files into the built-in Explorer** ignores incomplete (relative) file paths left on the clipboard by other programs, so a cut-and-paste can no longer move files you never selected.
- **Permanent delete** never follows a junction or symbolic link into its target when it clears read-only flags (hardening; not reachable in earlier versions).
- **The optional video decoder (FFmpeg) is checked every time it is loaded**, not only when it is downloaded. Its five DLLs are verified against pinned size and SHA-256 values, a cached check keeps later starts fast, and verified files are held open so they cannot be swapped after the check. If any file was changed or replaced, Glide does not load it and offers to download a clean copy. The download test override now accepts only https or a loopback address.
- **Explorer thumbnail provider hardening.** It limits the thumbnail size it will draw to 2048 px no matter what size Explorer requests, and both native components (the image bridge and the thumbnail provider) are now built with Control Flow Guard, CET shadow-stack compatibility, address-space randomization and the compiler's additional security checks.
- **Image shortcuts cannot reach a hidden picture** (see Fixed): an accidental Delete or rename from an Explorer or Welcome tab can no longer touch the picture of another tab.
- **Reset Glide only touches Glide's own data.** Its registry access is limited to `HKCU` by construction, it never touches `HKEY_LOCAL_MACHINE`, the install folder or other applications' data, and it makes a backup first unless you untick that box.
- **Security review.** The audit also found no issue in the native decoders' size checks, the IPC parser, Explorer file operations, file associations, metadata parsing and codec-provider loading, and that Glide still makes no network requests except the optional, confirmed video-decoder download.
- **Removed unused network-download and disk-cache code** (`SecureImageFetcher`, `SecureDiskCache`) that no part of Glide called.
- **Development signing script is safer.** `tools/sign-local.ps1` no longer adds its self-signed certificate to your trusted certificate stores by default; that is a separate, explicit opt-in with a warning, and `-RemoveLocalTrust` removes trust added by earlier versions. Release builds can be signed with a real code-signing certificate through environment variables (see `docs/code-signing.md`).

### Internal

- **Master action registry.** `Glide.Core/Commands/AppActionRegistry.cs` is the single list of actions (more than 260 actions: commands, generated *Toggle:* / *Cycle:* setting actions and read-only fixed shortcuts); hotkey rows, gesture actions, title-bar buttons, menu items, status-bar buttons and Settings search entries are projections of it, command handlers are registered in one place, and key text is compared in one canonical form. Old command ids, hotkey row ids (75) and gesture action ids (56) are persisted and unchanged. Guard tests cover every default shortcut through the real key handlers, every menu item, status-bar button and title-bar button, and every Toggle / Choice setting (bindable or excluded with a written reason); `tools/audit-action-registry.py` is the static audit. A test fixture bug that made 12 asynchronous UI tests stop at their first `await` (they passed without checking anything) is fixed, and those tests now run for real. See `docs/ACTIONS-AND-HOTKEYS.md`.
- **Window placement service.** `WindowPlacementService` (Glide.App) and the pure `WindowPlacementResolver` (Glide.Core) back the window memory; positions are stored per window key under `WindowPlacements` in `glide.settings.json` (at most 64 entries, least recently used dropped), and the Settings window's older saved placement is migrated and still read. A guard-rail test fails when a new window is added without registering it (or allow-listing it with a reason).
- **Reset logic** lives in `Glide.Core/Reset` (catalogue, planner, executor, backup, coordinator and helper) behind file-system, registry (`HKCU` only) and process interfaces, and is tested against scratch folders, an in-memory registry and a scratch `HKCU` key. The windowless helper is started as `Glide.exe --reset-glide --items <ids> --wait-pid <pid> --settings-dir <dir>` and is used only by Settings. Reset items are Settings-catalogue actions, not counted as changed settings, not part of Apply or profiles, and not bindable (destructive).
- **Popup cursor guard** (`PopupCursorGuard`, `FullscreenCursorAutoHide`) keeps the arrow pointer on every popup and pauses fullscreen idle hiding while one is open; 19 tests.
- **Settings schema 11.** Schema 7 migrates the old mouse toggles; schema 8 moves the *Show / hide status bar* default to Ctrl + Shift + B and a stored zoom step of 10% to 15%; schema 9 adds Back / Forward to a title bar still on the previous default layout and the new Back / Forward keys to saved shortcut maps (without taking a key another action uses); schema 10 gives *Explore/Browse containing folder* the key Ctrl + E where it had none and the key is free; schema 11 moves Backspace from *Previous image* to *Back, or Previous image* when it was still exactly the old default.
- **Navigation history.** `NavigationHistory` (`Glide.Core/Navigation`, pure and tested) holds up to 100 steps per tab; `MainWindow.History.cs` records and replays them across Home, image and Explorer tabs. Actions `nav.back` / `nav.forward`; the mouse side buttons run them through the gesture defaults (`GestureCatalog.DefaultActionFor`). See `docs/NAVIGATION-HISTORY.md`.
- **Code hygiene pass (behaviour unchanged).** Dead code was removed after analyzer and name scans (about 126 files touched, roughly 835 fewer lines net): unused fields, methods, parameters, constants and events across the viewer, main window, Settings window, platform helpers and Core (including the never-raised `PriorBitmapReleased` event, the unused `ViewerState` types and seven unreferenced click handlers), unused theme styles and resources, and about 350 unnecessary `using` directives.
- **Large files split and responsibilities separated.** `SettingsWindow` is split into partial files for diagnostics export, hotkeys, profiles and thumbnails (`SettingsWindow.axaml.cs` went from 3,635 to 2,385 lines); the headless command-line modes moved out of `Program.Main` into `CommandLineTools`; theme brushes are applied by `ThemePaletteApplier`; the Settings change diff is a UI-free `SettingsChangeSet`; the key-press text is one `KeyboardShortcutText` (11 new tests) instead of two copies; the overlay drift math is shared in `OverlayDriftMotion`; and shell launches ("open with", "show in Windows File Explorer") live in `ExplorerShell` instead of window code.
- **Dependency and duplication fixes.** Settings persistence no longer reads constants from `MainWindow` (it uses the same Core constants); Settings no longer re-implements thumbnail-control loading; no project-reference cycles.
- **Registry audit details.** The hotkey audit proved that all 78 historical default shortcuts reach their action through the real key handlers (6 historical actions had a direct defect, fixed above). The duplicate command pair `ExploreParentFolder` / `OpenContainingFolder` is one documented alias (`file.showInExplorer` -> `folder.exploreParent`); the `windows.alwaysOnTop` setting is excluded from bindable settings because the Overlay-aware `window.alwaysTop` command already is the action; the gesture picker, the title-bar catalogue (the same 22 buttons plus every registry command that has an icon), context menus and 28 status-bar buttons are read from the registry; the Diagnostics self-test checks that the 75 historical hotkey ids and 56 gesture action ids are present instead of hard-coding counts; showing a closed status bar with the hotkey now also clears the session-closed flag. Dormant code was documented rather than removed where a contract test requires it (the native Windows Explorer host).
- **Performance and security test harnesses.** New headless benchmark harness in `tools/perf` (`Glide.PerfBench`, `run-perf.ps1`, `compare-perf.ps1`, `generate_perf_data.py`); a print-preview harness (opt-in with `GLIDE_PRINT_PERF=1`); new security tests (SVG references, import hardening, GIF limits, pixel cap and memory guard, installer, pipe and FFmpeg contracts).
- **Native build.** Both native projects build with Control Flow Guard, CET compatibility, `/sdl` and (when the Spectre-mitigated libraries are installed) `/Qspectre`; the native bridge exports `GlideGetMaxDecodePixels` for tests.
- **Signing hooks.** `build.cmd` signs `dist\Glide*.exe/dll` and `tools\build-installer.cmd` signs the installer before writing its checksum, only when a signing certificate is configured through environment variables.
- **Repository tidy-up** (`docs/CLEANUP-5.0.md`): release notes moved to `docs/releases/`, helper scripts to `tools/`, session backups and legacy documents consolidated into archives, duplicate and generated files removed.
- **Tests and audits.** Glide.Core.Tests 1258 passed, Glide.Input.Tests 21 passed, Glide.Explorer.Tests 125 passed; the settings catalogue has 269 entries (214 declared settings plus 54 generated gesture rows and the custom-gesture entry) and the settings-search audit passes (208 editors, 214 settings in 39 cards on 8 pages, 54 generated gesture rows); the action-registry audit passes (98 commands registered and dispatched, 146 setting accessors, 31 documented exclusions, 28 status-bar buttons tied to the registry).
- **Coming next.** .NET 8 reached end of support on 2026-11-10; a migration to .NET 10 LTS is planned for the next release (`docs/NET10-MIGRATION-PLAN.md`).
- Version advanced to 5.0.0 throughout (assembly, installer, window title, About page, README and diagnostics).

### Thanks

Thanks to [vertigo220](https://github.com/vertigo220) for the many detailed bug reports and feature ideas that shaped this release: the numbered follow-ups to [issue #1](https://github.com/cobhc95/Glide/issues/1) (reserve status bar space when fitting, tab tooltip text size, the collapsed expand button and its reveal, Settings scroll memory per page, the fullscreen-exit flicker, the shortcut conflict prompt, *Explore/Browse containing folder*, custom hotkey presets, sortable shortcut columns, *Search hotkey*, the tooltip that blocked list scrolling, the right-click menu timing and the auto-hide status bar), the [Explorer tab](https://github.com/cobhc95/Glide/issues/2) request with session restore, and the [GIF controls](https://github.com/cobhc95/Glide/issues/3). Every reported item and where it shipped is listed in [docs/releases/5.0.0-community-feedback.md](docs/releases/5.0.0-community-feedback.md).

## [4.7.1] - 2026-11-12

Polish from further community testing of 4.7.0.

### Changed

- **A collapsed status bar shows only its expand button**, with no panel, border or shadow around it.
- **Title-bar buttons nearest the window controls win when space runs out.** Narrow windows now hide the leftmost title-bar buttons first, so Settings (the last button) stays visible longest.

### Added

- **Always on top button menu.** Right-click the title-bar *Always on top* button to switch it on or off and choose *Hard* or *Soft* mode directly.

## [4.7.0] - 2026-11-12

Most of this release comes from a detailed 21-point community report ([issue #1](https://github.com/cobhc95/Glide/issues/1)). Community feedback like this is what makes Glide better.

### Added

- **Animated GIFs play.** A managed GIF decoder composites every frame (disposal, transparency, interlacing) off the UI thread and the viewer swaps frames without touching zoom or pan. New setting: *Play animated GIFs* (Viewing & Appearance, on by default).
- **Fit to window.** Enlarges or shrinks the image to the largest size that fits the window, next to the existing shrink-only *Fit image*. Status-bar button, context menu, hotkey `4`, and a *Default view for new images* choice.
- **Right button + wheel.** New gesture slot *Wheel while holding the right button*, with direction-aware *Zoom in/out* and *Previous/next image* actions. When it acts, releasing the right button no longer opens the context menu.
- **Middle-click closes tab** setting, and **Ctrl+Tab switching order** (*Tab order* or *Most recently used*; hold Ctrl and press Tab repeatedly to go further back).
- **File name in the status bar** (full path when *Show full path in title* is on), with the full path as its tooltip. Tabs show the full path when that option is on, and the tab tooltip always shows it.
- **Status bar position.** Right-click the status bar to place it at the *Center* (default), *Left* or *Right*; also in Settings (*Status bar position*). Right-clicking the Home status bar now opens the same menu.
- **Collapsed status button in the corner.** New setting, also in the status-bar right-click menu (on by default): the collapsed bar parks its expand button in the lower-right corner, away from the image. Turn it off to keep the button where the collapse button was.
- **Speed Boost tray icon** setting: *Always*, *Only while Glide is closed*, or *Never*. Standby no longer needs a visible icon; launching Glide again wakes it.

### Changed

- **Home** shows only the Recent list (with an empty state and an *Open image…* button). The feature cards, the *Home quick tips* setting and the *Move Recent* / *Reset layout* buttons are gone.
- A collapsed status bar is now only its expand button, with no empty strip left behind.
- The status-bar collapse button turns into the expand arrow on the hover-revealed bar; the separate corner arrow is now only used in fullscreen. With hover reveal off, collapse simply hides the bar (*View and present → Show status bar* brings it back, and is no longer ticked while collapsed).
- The title-bar *Navigate to folder* button is now labelled *Show in File Explorer (select current image)*, which is what it does.
- The gesture matrix spells out what *Default* does for each slot.
- Tooltips open beside their control (above the status bar, below the title bar) instead of under the pointer.
- Scrollbars expand and collapse faster (0.1 s / 0.6 s).

### Fixed

- A right-click menu could stay on screen as a ghost after clicking; owned menus now close after an item is chosen, on any click elsewhere in the window, when Glide loses focus and when the window moves.
- "End of folder — no next image" no longer replaces the status-bar image info permanently.
- Tooltips covered toolbar buttons and blocked clicks until the pointer left the toolbar.
- *New tab opens* and *Home page* reverted after choosing the hidden, removed *Explorer tab* / *Browser page* options; those options are gone.
- *Open last session* lost the session when Glide was closed with the caption X while Speed Boost was on, and the tray *Exit* could overwrite the session with an empty one.
- Tab tooltips listed middle/double-click to close regardless of the settings.
- Many tabs overlapped; tabs now stop at a usable width, the strip scrolls (arrows or wheel), and the active tab is kept in view.
- Rename: the name part is pre-selected (Right Arrow lands before the extension), Enter renames and Escape cancels.
- Settings *Cancel* closed noticeably slower than *OK*; it now closes at once and only rolls back a preview if one was applied.
- The *Mouse & keyboard* settings page opened scrolled down on first visit, and returning to Settings with Alt+Tab scrolled back to the last-clicked option.
- With *Double-click exits fullscreen* on, a fullscreen double-click also advanced to the next image; single clicks now wait out the double-click interval first.

## [4.6.3] - 2026-11-12

### Fixed

- **Crash after opening a second window.** The per-window taskbar identity used a source-generated COM wrapper whose finalizer threw `NullReferenceException` on the finalizer thread, which NativeAOT turns into a process fast-fail. It now calls the property store's vtable directly.
- **Menus buried under "Always on top: Hard".** The hard re-raise now backs off while any popup window is open or any Glide window is in a native move/size loop.
- Window placement is saved once at the end of a move instead of on every position change; the move handler can no longer throw.
- Unhandled exceptions are always written to `%LOCALAPPDATA%\Glide\crash.log`; the overlay worker thread logs per-frame errors instead of ending the process.

## [4.6.2] - 2026-11-05

### Changed

- **No .NET required.** Glide now ships as a NativeAOT build, so no separate runtime is needed. This also removes the .NET prompt that Setup could wait on invisibly on PCs without .NET.
- **Smaller downloads.** The installer goes from 54.7 MB to 17.3 MB and the portable archive from 70.9 MB to about 22.5 MB. Release builds use only the Windows Avalonia packages.
- **FFmpeg is an optional download.** Videos use Windows codecs first. If Windows can't play a video, Glide offers to download the decoder once (51 MB) to `%LOCALAPPDATA%\Glide\ffmpeg-9.0.2`, after checking a pinned SHA-256 hash.
- Diagnostic trace and event logs record extra data as plain text instead of structured JSON.

### Fixed

- **Mouse wheel direction.** Scroll down now goes to the next image and scroll up to the previous one; on default settings the wheel was backwards in 4.6.0 and 4.6.1. Settings schema 6 clears "Reverse mouse-wheel direction" once, which keeps the direction for anyone who had enabled it to compensate.
- **Window buttons and icons.** Caption buttons are drawn as vectors, and other icons fall back to Segoe MDL2 Assets, so they no longer depend on the Windows 11 icon font.
- **Diagnostics self-test.** Registered the two 4.6.1 folder-counter settings in the effect registry, which stops the crash, and updated the expected settings count to 204.

### Developer

- JSON persistence uses source generation, and COM interop uses generated interfaces.
- Building Glide now requires the Visual Studio C++ tools. `tools/package-ffmpeg-pack.ps1` builds the optional decoder pack.

## [4.6.1] - 2026-11-01

### Added

- **Folder position counter:** Optionally show the current folder's position among its siblings beneath the image counter. It is off by default and follows the configured sibling-folder order and hidden-folder setting. A second option adds the parent folder's position within the grandparent, for example `5/50/4`.
- Both folder-counter options can be found through Settings search. A search-coverage audit and project rule help keep future settings discoverable.

### Fixed

- **Navigate to folder:** Corrected the Windows Explorer command so it opens the current image's location instead of falling back to Documents.
- Centralized release version information so the app, diagnostics, build scripts and installer derive their version from `Directory.Build.props`.

## [4.6.0] - 2026-11-01

Everything since the published 4.5.0 release, including the 4.5.1 and 4.5.2 work that was never released separately.

### Added

- **Video in Window-in-Window overlays.** Drop or open a video as an overlay: MP4, M4V, MOV, MKV, WebM, AVI, WMV, ASF, 3GP/3G2, TS/MTS/M2TS, MPG/MPEG/M2V and VOB. Playback uses Windows Media Foundation with a bundled FFmpeg fallback decoder (loaded only when a video needs it). The overlay has its own transport bar and right-click Video menu: play/pause, previous/next video in the folder, restart, mute and volume, loop and auto-play next. Volume, loop and auto-play choices are remembered per overlay. Videos are overlay-only and never enter the main viewer's image navigation.
- **Custom animation area.** Draw a rectangle that an animated overlay is confined to; it is stored as fractions of the viewer so it keeps covering the same part of the view when the window is resized, maximised or made fullscreen.
- **Adaptive status bar.** Wide windows keep the stats, every icon group and the window buttons on one row. As the window narrows, the stats move to a header line beside the window buttons (wrapping only at the separators, never inside a value like "3744 × 5616") and whole icon groups spread over one or two balanced rows; groups never split and the "–" / "✕" buttons stay pinned top-right. When even that cannot fit, the lowest-priority groups (overlay, settings, save, info, fit) move into a "⋯" menu that runs the same actions. New **Tiny** size (smaller than Very small), and **Small** is now the default for new settings (saved choices are kept).
- **Save and Save as buttons in the status bar.** Two floppy-disk icons sit beside Settings. Save (also Ctrl+S and the context-menu item) now does nothing, and its button is greyed out, until the picture has been rotated or flipped; Save as always writes a copy.
- **Grouped menus with icons.** Every menu is regrouped by purpose and every item has an icon in the toolbar's own style; nothing was removed. The viewer right-click menu is now Copy pixels, Fit and zoom, Rotate and flip, Copy and export, File, Navigate, View and present, Overlay mode, Settings, Exit. The overlay window menu is grouped into Video, Zoom and size, Opacity and order, Animate, Open and Close; the status-bar menu keeps its six sizes in a Size submenu; tab, title-bar, fullscreen and file-browser menus gained icons; the title-bar customiser lists its 22 actions under category headings.
- About twenty new icons (rotate, flip, copy, file, rename, delete, eye, bring to front, open with, volume, crop, check, navigate, present, move) and redrawn Refresh, Opacity, Appearance, Performance, Developer and Tabs icons.
- **Selection buffer around the picture, now configurable.** A left-press in the empty area around a picture starts a selection clamped to the picture's pixels. When maximized the whole empty area is the buffer; in a normal window about one third of the empty space on each side is. Settings > Mouse & keyboard: turn it off, set the windowed and maximized sizes (0-100 %), or use the maximized size in every window state. Fullscreen is unaffected.
- **Choose the image order for next / previous.** New settings in Settings > Interface & window: Name (like Explorer, the default), Name (plain characters, the old behaviour), Type, Size, Date modified or Date created, with an option to reverse the order. The open folder is re-sorted in place when you change it, keeping the current image selected.
- **Slideshow left-click action.** Settings > Slideshow & overlays and the Start slideshow prompt choose what a left-click on the picture does during a slideshow: pause / resume (default), next image, previous image, stop, or nothing. Space now always pauses or resumes an active slideshow.
- **Shortcut search.** The Keyboard shortcuts list has its own filter box; every word must match the action, its category or one of its keys.
- **Settings search covers more of what is on screen.** Search also matches a row's other text and tooltips, drop-down option text, the note line under a row and each section's title and description. The thumbnail "Maximum source decode size" drop-down is now a catalogued, searchable setting. A new test fails if any setting's tooltip, option or row text cannot find its setting.
- `tools/sign-local.ps1` signs a locally built Glide with a self-signed certificate and clears the downloaded-from-the-internet mark, and the executable now carries product and company version details.

### Changed

- **Settings reorganised from 14 categories into 8**: General & startup, Appearance & viewing, Interface & window, Tabs & status bar, Mouse & keyboard, Slideshow & overlays, Performance, Windows & developer. Every control is still there, now under headed sections. The search category filter follows the same eight groups.
- **Settings search ranks title matches first.** Results whose title (or the on-screen label of the control) matches your words are listed first; settings that match only through a description, tooltip or search term follow under a "Related settings" heading. A new setting (Settings > General, on by default) hides the related group. Search also stopped indexing a whole page's text into every setting on pages without section headings, which had made about thirty Performance settings match every query.
- **Settings feel faster.** A control change no longer applies the whole live preview before the control repaints (it is deferred and coalesced), and search results are built a few at a time and abandoned as soon as you type again.
- The status bar's width is no longer capped by its size setting, so smaller icons genuinely fit on one row instead of shrinking and still wrapping.
- Version advanced to 4.6.0 throughout: assembly, installer, window title, About page, README and diagnostics.

### Fixed

- **Mouse wheel direction.** Scroll down now goes to the next image and scroll up to the previous one. The wheel was reversed on the reported setup. The setting "Reverse mouse-wheel direction" (Settings > Mouse & keyboard) flips it back, and it also reverses wheel zoom when wheel zoom is on.
- **Next / previous now follow Windows Explorer's order.** Glide sorted a folder by plain character order, so a name such as `3m90fxiwwfo61.png` sorted after every file starting with `0`, `1` or `2`, and stepping through a folder jumped around compared with Explorer. The default order is now Explorer's natural name order (numbers compare by value: `3m90…`, `5sh7…`, `8ptx…`, then `03325…`, `04001…`, `106481…`). The windowed first-open list, the full folder index, live folder refresh, the folder browser, the "first image in folder" action and sibling-folder navigation all use the same shared ordering, so they can no longer disagree.
- **Double-clicking the title bar now maximises and restores the window.** The press no longer enters Windows' native move loop immediately (it swallowed the release, so the second click was never seen as a double-click); a drag starts only after the pointer moves, and the maximise or restore command is sent after the press completes.
- **Explorer opens no longer stall after closing to the tray.** The internal Speed Boost standby close can no longer be cancelled by the home-suppression window or the fullscreen close handling, which previously left a hidden window owning the launch broker with nothing to receive new files.
- **Drag and drop into Glide.** The window now accepts file drags, so dropping images and folders works instead of showing the "not allowed" cursor.
- **Normal window size survives fullscreen.** Leaving fullscreen into maximized (unchanged) no longer overwrites the remembered normal bounds with the maximized size, so un-maximizing returns to your real window size and position.
- **Buttons are centred.** Labels on dialog and Settings buttons (Cancel, Apply, OK, Go Back, Close Anyway) hugged the top-left; the theme now centres button content.
- Source-contract tests read every `MainWindow.*.cs` partial, not only `MainWindow.axaml.cs`.

### Security

- Bounded decompression: PNG text chunks are inflated within a 1 MB budget, and SVGZ (gzip) documents are rejected past 128 MB, instead of copying unbounded output and checking afterwards.
- Native codec surfaces are validated (stride, size, dimensions) before use, so a malformed provider falls back instead of faulting.
- The built-in raster decoder opens each file once and reads it with a capped pooled-buffer loop, closing a check-then-read window and avoiding large-object-heap spikes.
- The single-instance launch pipe checks that the connecting peer is the same user (it fails open and records mismatches, so a legitimate Explorer double-click is never dropped).
- "Show in Explorer" passes its path as an argument rather than a hand-quoted string and rejects quote and line-break characters.
- The secure disk cache writes through to stable storage before the entry becomes visible, so a crash cannot leave a torn file.
- Obscure numeric IP spellings (for example `2130706433`, `0x7f.0.0.1`) have a deterministic parser with tests that pin the existing loopback and private-address blocking.

### Performance

- The startup image preloader is awaited instead of blocking the calling thread for up to 500 ms, and the speculative-cache memory limit is cached for two seconds instead of queried on every warm.
- Print preview reuses its brushes and pens instead of allocating them on every repaint.
- The native overlay rasteriser uses integer bilinear sampling with per-column tables, so video overlays can rasterise every frame.

### Internal

- `MainWindow` is split from one ~8,700-line file into 15 focused partial files, and the scattered literals now have single sources: `UiMetrics`, `ThemePalette`, `AppVersion` and `ProtocolNames`.
- New tooling and tests: `tools/fetch-ffmpeg.ps1`, `tools/create-handover-zip.ps1`, video player and overlay-chrome tests, overlay animation-area tests, status-bar layout tests for the wide, medium and narrow layouts, and the search-coverage tests above.
- A new test fails if any enabled interactive Settings control is not mapped to a catalogued (so searchable and editable) setting. Settings catalogue now holds 202 entries; the build-script fallback versions and `app.manifest` identity are aligned to 4.6.0.


## [4.5.0] - 2026-10-27

### Added

- **Photoshop PSD / PSB / PDD support.** The files open in the viewer and get thumbnails in Glide and in Windows Explorer. Glide renders the flattened composite stored in the file; layers are not re-composited. Supported: RGB, CMYK, grayscale, indexed, bitmap, duotone and Lab colour modes; 1/8/16/32-bit depth; raw and RLE (PackBits) compression; composite transparency. A managed decoder was added to `BuiltInRasterDecoder` and a matching native decoder to the Explorer thumbnail provider. Both reject malformed or truncated input without throwing.

### Changed

- Glide now claims `.psd`, `.psb` and `.pdd` when registering Explorer thumbnails. Re-register thumbnails after upgrading.
- Version advanced to 4.5.0 throughout: assembly, installer, window title, About page and diagnostics.

## [4.4.0] - 2026-10-26

### Added

- **Live folder refresh.** Images that are downloaded, copied, moved or renamed into the open folder appear in next/previous navigation and the image counter without reopening it. Deleted files drop out. Partial downloads and temp files (`.crdownload`, `.part`, `.tmp`, `.download`, `~*`) are ignored until they finish. A file is added once it can be opened. Events are batched, and a full rescan only happens if the watcher overflows. The current image stays selected.
- **Save (Ctrl+S) and Save As (Ctrl+Alt+S)**, available from the context menu. Rotation and flips are applied to the pixels. The file is written to a temporary file and then swapped in, so a failed save cannot damage the original. Save keeps the original format and asks before overwriting. Save As writes JPEG, PNG, BMP or TIFF. The native encoder can now write TIFF and takes a quality setting; JPEG is saved at 92%.
- **Fullscreen cursor auto-hide.** The cursor hides after 3 s without movement and only comes back after about 1 s of continuous movement, so small bumps, clicks and key presses don't show it. It stays visible over controls, menus and overlays, and is restored when you leave fullscreen or switch away. It follows the existing "hide cursor in fullscreen" setting.

### Security

- **Explorer thumbnail provider:** fixed a stack buffer overflow in the TGA decoder. A crafted bits-per-pixel value could write past a 4-byte buffer while Explorer was only *showing* a folder. Bit depth is now validated.
- Fixed an integer overflow in the SGI RLE bounds check that allowed out-of-bounds reads.
- TGA colour-mapped images could read past the end of the file. The colour-map entry size and index range are now validated.

### Fixed

- Explorer thumbnails for PNM/PBM/PGM/PPM/PAM files: the format digit was compared as a number instead of a character, so almost every file in this family decoded wrongly. P1–P7 now decode correctly, including padded P4 rows, unseparated P1 digits and the whitespace after the header.
- Explorer thumbnails for 24-bit PCX images now decode in colour, and use the bytes-per-line value from the header.
- Explorer thumbnails for compressed SGI images: the scanline tables were read 12 bytes early and with the wrong layout.
- TGA colour maps are now read using the header's first-entry and length fields.

### Changed

- Version advanced to 4.4.0 throughout: assembly, installer, window title, About page and diagnostics.
- Hotkey catalogue: 69 → 71 commands.

## [4.3.5] - 2026-09-29

### Fixed

- **Maximize no longer gets stuck at the old size after rotating the display** (portrait ↔ landscape) or moving the taskbar. Avalonia sizes a maximized borderless window from a cached monitor work area that it only refreshes on WM_DISPLAYCHANGE, often before Explorer has re-docked the taskbar. Glide now re-fits a maximized window to the live work area, refreshes Avalonia's monitor cache on display, DPI and work-area changes, and re-checks after the taskbar settles.
- Double-clicking the empty title/tab bar now toggles maximize/restore from the real Windows state, like the Maximize button, instead of a possibly stale cached state.
- **Missing taskbar icon.** Avalonia rewrote the window style on every maximize, restore and taskbar change, which stripped `WS_SYSMENU`/`WS_THICKFRAME` from Glide's custom frame. The shell then stopped treating Glide as a normal app window (missing taskbar icon, unreliable Snap Layouts). The frame styles are now reapplied on every rewrite, and Glide attaches DPI-correct icons from `Glide.exe` to each window and its window class, including after Speed Boost restore.
- Fixed a possible hard crash or corrupted image when the compressed-image cache evicted a file (trim, purge on minimize, or refresh) while it was still being decoded. Cached buffers are now reference-counted for as long as a decoder reads them.
- An unexpected error inside a UI event handler is now logged to the diagnostic trace (`dispatcher_unhandled_exception`) instead of closing Glide.

### Changed

- Application, diagnostic, title-bar and installer versions advanced to 4.3.5. The earlier 4.3.4 build still showed 4.3.3 in the window title and About page.

## [4.3.4] - 2026-09-29

### Fixed

- Use the actual Windows HWND maximized state for caption maximize/restore and resize-edge hit testing. This prevents a stale cached window state after snapping or warm restoration from making maximize restore a half-width window or disabling resizing.

## [4.3.3] - 2026-09-28

### Added

- Size presets (25%, 50%, 75%, 100%, 125%, 150%, 200%) in both image-overlay and whole-app Overlay context menus. The image and frame resize together and stay within the available viewport or screen.

### Changed

- Application, diagnostic, and installer versions advanced to 4.3.3.

## [4.3.2] - 2026-09-26

Follow-up to 4.3.1 that releases a viewed image as soon as it is closed.

### Fixed

- 🔓 **A viewed image is never locked by Glide after it is closed.** Windows reported
  *"the file is open in Glide Image Viewer"* when deleting or moving an image that had already been
  closed (including after a Speed Boost standby close). Glide's asynchronous readers — the EXIF/metadata
  reader, the print DPI reader and the SVG loader — opened the user's file without `FileShare.Delete`,
  so any read that overlapped the delete/move denied the rename. All user-image reads now open with
  `FileShare.ReadWrite | FileShare.Delete`, and the SVG loader reads the document into memory with
  full sharing instead of handing the file path to the third-party `Svg.Skia` loader (which could
  retain the handle). `FileHandleReleaseTests` pins both contracts.

### Changed

- Version, assembly, installer, window titles and the Settings footer advanced to **4.3.2**. The
  Settings footer now derives its label from assembly metadata so it cannot drift again.

## [4.3.1] - 2026-09-22

Follow-up to 4.3.0 that fixes WebP thumbnails in Windows Explorer.

### Fixed

- 🖼️ **WebP files now show real thumbnails in Explorer.** Windows ships no in-box WebP codec — it
  is delivered only by the optional *Webp Image Extensions* Store package — and the in-box
  "Photo Thumbnail Provider" registered for `.webp` fails without it
  (`WINCODEC_ERR_COMPONENTINITIALIZEFAILURE`), leaving a generic icon. Glide now:
  - links a **vendored, decode-only libwebp** (`native/third_party/libwebp`, BSD-3-Clause) into
    `Glide.ShellThumbnail.dll`, decoding lossy VP8, lossless VP8L and alpha WebP directly; and
  - treats `.webp` as a Glide-native format in **Recommended** thumbnail mode, so Glide claims it
    even though Windows registers a weak handler for it.

  Both changes are self-contained: no Store package and no runtime download is required.

### Changed

- Version, assembly, installer and UI metadata advanced to **4.3.1**.

## [4.3.0] - 2026-09-22

Explorer integration and window-management release. Glide now draws real thumbnails in Windows
Explorer, multiple Glide windows each get their own taskbar button, and minimizing can no longer
lose a window.

### Added

- 🖼️ **Windows Explorer thumbnail provider.** Explorer now shows real thumbnails for the formats
  Glide can open instead of a generic icon. A small native COM shim
  (`Glide.ShellThumbnail.dll`) decodes inside Explorer's isolated thumbnail host — it never loads
  .NET, Avalonia, Glide.exe or any Glide UI, never creates a window, and never shows a dialog.
  - Format coverage is **derived from Glide's format registry**, so a newly supported format becomes
    thumbnail-capable automatically (196 extensions, 210 when counting every suffix Glide opens).
  - Decode strategy: embedded thumbnail/preview first, then decoder-native reduced decode (WIC), then
    a bounded full decode; SVG is rasterised with Direct2D at the size Explorer asked for.
  - **Measured:** median **1 ms**, p95 **13 ms**, 100% under 20 ms across a 300-image corpus
    (640×480 up to 8000×6000) plus TGA/SVG/QOI/PPM, counting COM activation per item.
    See `docs/explorer-thumbnails.md` and `tools/thumbnail-benchmark.ps1`.
  - Windows keeps owning the thumbnail cache; Glide adds no second database.
- ⚙️ **Settings → Windows Integration → Explorer thumbnails**: master toggle, provider mode
  (Recommended / unsupported-only / all / custom), per-group and per-extension selection, thumbnail
  quality (Fast/Balanced/High), embedded-preview preference, maximum source decode size, a
  diagnostics readout (provider present, registered, architecture, formats, mode, version) and
  actions to re-register, restore recommended defaults and refresh the Windows thumbnail cache.
- 🗂️ **Each Glide window is its own taskbar button.** Secondary windows now keep their own taskbar
  entry and receive a distinct AppUserModelID (verified on real windows), so multiple Glide windows
  no longer collapse into a single button.
- 🔧 **Installer integration** for the thumbnail provider (machine-wide `HKLM`) with clean uninstall,
  including stopping the warm host before removal.

### Fixed

- 🪟 **Multiple Glide windows now appear as separate taskbar entries.** The single-taskbar
  representative suppression was removed; the registry no longer toggles `ShowInTaskbar`.
- 🗕 **Minimizing can no longer make a window vanish.** Because every window now keeps its own
  taskbar button and `ExitStandby` restores it after Speed Boost standby, a minimized window can
  always be restored from the taskbar.

## [4.2.7] - 2026-09-22

Automatic Windows integration release. Glide now associates itself with every supported format when
it is installed, with no trip to Options.

### Added

- 🔗 **Automatic Open With integration.** Glide adds itself to Windows' **Open With** menu for all
  **196** supported extensions without the user having to configure anything:
  - The **installer** registers it **machine-wide** (`HKLM`) for every user of the PC, immediately
    after the files are copied — including in silent installs.
  - A **portable** copy (and any first launch) registers it **per-user** (`HKCU`) automatically.
  - Registration is idempotent, runs at most once per version, is deferred to background priority so
    it can never affect first paint, and never touches Windows' protected default-app (`UserChoice`)
    selection.
- 🚫 **Opt-out is respected.** Removing Glide from Open With in **Settings → Windows Integration**
  records the choice, so automatic integration does not silently re-add it. Re-adding clears it.
- 🧹 **Clean uninstall.** The uninstaller removes the machine-wide registration it created.

### Changed

- The **File associations / Open With** setting now describes the automatic behaviour and is used to
  re-add or remove the per-user registration rather than to enable integration in the first place.
- The installer no longer hard-codes a short list of common formats; it invokes Glide's own
  registration so the set can never drift from `ImageFormatRegistry`.
- Diagnostic and benchmark harnesses (`--capture-print-dialog`, benchmark/first-frame flags and
  diagnostics export) never mutate the real Open With registration.

## [4.2.6] - 2026-09-22

Print correctness release plus the first major expansion of native format decoding. Every recognized
suffix now has a defined, non-crashing route, and a new family of bundled decoders removes the
optional-codec dependency for vector and legacy raster formats. See `docs/format-coverage.md` for the
full route map and the IrfanView comparison.

### Added

- 🖼️ **Native SVG / SVGZ (vector) support.** `.svg` and gzip-compressed `.svgz` are now rasterised
  in-process with Svg.Skia at exactly the resolution needed — a bounded render for browse previews
  (so vectors stay crisp at any zoom) and an intrinsic-size render, capped at 4096 px, for the full
  1:1 and print source. Transparency is preserved.
- 🧩 **Bundled built-in raster decoders** for the long tail of simple formats that neither Skia nor
  Windows WIC can be relied on for, so they work on a clean PC with no optional codec installed:
  **TGA/TARGA/ICB/VDA/VST**, **PCX**, **PNM (PBM/PGM/PPM/PAM)**, **QOI**, **Radiance HDR/RGBE**,
  **WBMP**, **XBM**, **XPM**, and **SGI/RGB/RGBA/BW**. All entry points are total: malformed or
  hostile input returns "cannot decode" instead of throwing, with strict allocation caps.
- 🪟 **Windows Shell last-resort decode.** When Skia, WIC and the built-in decoders all decline a
  recognized suffix, Glide now accepts a genuine Windows thumbnail/preview handler for *any*
  recognized format (never a generic file icon), so PDF, EPS/AI, Office, CAD and camera-RAW files
  render whenever the platform has a handler. Alpha may be flattened on this route.
- 📊 **Format coverage documentation** (`docs/format-coverage.md`): the full decode chain, a
  per-group coverage matrix, and a side-by-side comparison with IrfanView's format/plug-in list.

### Fixed

- 🔄 **Auto-rotate now rotates instead of stretching.** "Automatically rotate for best fit" performs a
  real 90° rotation in both the live preview and the spooled output.
- 🖥️ **Actual-size / Fill-page no longer overflow the window.** Oversized renders are clipped to the
  content box, so the preview keeps its buttons and margins and gains a scrollbar instead of pushing
  the controls off-screen.
- 🖨️ **Changing printer settings to landscape no longer crashes** and is now reflected in the preview
  and orientation radios. The driver DEVMODE is seeded/read through `DocumentProperties` instead of a
  fragile handle copy.
- ⬛ **Grayscale is truthful.** Colour/grayscale now changes the preview and is forced onto the spooled
  bitmap with a Rec.601 colour matrix, so drivers that ignore `PageSettings.Color` (such as Microsoft
  Print to PDF) still print grayscale.
- 📄 **Paper Source is populated** from the driver's real trays and the row is hidden (rather than
  shown empty) when the driver reports none.
- 📜 **The Print window scrolls** instead of cutting options off on short screens; the left options
  panel is a scrollable grid.

### Changed

- The decode failure message now names the suffix and points at the optional-codec requirement
  (e.g. `No decoder for .cr2 (optional codec required)`) instead of a generic error.

## [4.2.5] - 2026-09-22

Image-printing release. Glide now has a complete, WYSIWYG image print workflow modelled on the mature
viewers (FastStone Image Viewer, IrfanView, XnView). See `docs/print-research.md` for the Phase-1
reference research and `docs/screenshots/print-research/` for the FastStone 8.5 reference capture.

### Added

- 🖨️ **Print the current image** from `Ctrl+P`, the viewer right-click menu, and the browser item
  Context menu. It prints the original full-resolution decoded image — never a screenshot of the
  viewport, so Glide's zoom, pan, selection, overlay borders and other chrome are never baked in.
- 🪟 **Glide-native Print window** with a live WYSIWYG page preview that updates immediately on any
  paper, orientation, scaling, margin or alignment change:
  - Printer selector and native **Properties…** (driver preferences) button
  - Paper size and paper source (when the driver reports trays)
  - Portrait / Landscape, Copies (1–99), Colour / Grayscale (when supported)
  - Scaling: **Best fit**, **Fill page** (crop to fill), **Actual size (100%)**, **Custom scale %**,
    **Stretch**; with **Keep aspect ratio** and **Automatically rotate for best fit**
  - Alignment: Left / Centre / Right and Top / Centre / Bottom (all nine positions)
  - Four independent margins in the locale's physical unit (mm/in)
- **Embedded-DPI Actual size.** JFIF density, EXIF X/YResolution + unit, PNG `pHYs`, TIFF and BMP
  resolution fields are read; absent or nonsensical values fall back to a documented **96 DPI**.
- **Layout preferences persist** between sessions (scaling, margins, auto-rotate, alignment,
  orientation, copies) in `print.settings.json`. Printer-specific capabilities are never persisted.
- Print command is user-bindable through the hotkey catalogue; the **Print** title-bar button is
  available in the title-bar customizer with a new vector glyph.

### Notes

- **Printing is high quality:** the source image's full resolution is spooled, aspect ratio is
  preserved except in the explicit Stretch mode, the printer's real printable rectangle is respected
  (zero margins mean "to the printable edge", not over it — avoiding the historical XnView
  preview/print divergence), alpha is composited against white, and EXIF orientation is honoured.
- **No print code runs at startup.** Printer enumeration and the GDI+/System.Drawing pipeline load
  only when Print is invoked, so normal navigation, overlay mode, fullscreen, zoom/pan, tabs and
  startup performance are unaffected.
- Single-image printing is complete. Multi-image / contact-sheet printing is intentionally deferred
  to a follow-up (the layout engine is page-shaped so it can be added without disturbing v1).

## [4.2.4] - 2026-09-22

Polish and correctness release. See the GitHub release notes for the same list.

### Fixed

- 🪟 **Minimizing no longer makes the window vanish.** With Speed Boost on, minimizing the last window
  also entered the hidden standby path, so it disappeared from the taskbar leaving only the tray icon.
  Minimize is a real Windows minimize again; standby stays reserved for closing the last window.
- 🖼️ **Aspect ratio is preserved for EXIF-rotated photos.** Pictures with an EXIF orientation tag
  (portrait phone/camera shots, orientation 5–8) were stretched because the reported source size
  ignored the rotation the decoder applied. Source orientation is now aligned to the decoded bitmap
  on every decode route, so these images fit correctly.
- 🖱️ **Fullscreen right-click (backwards) now tolerates small pointer movement.** Left-click forward
  fired on press, but backwards navigation required a near-motionless pointer; both now share a
  forgiving click-versus-drag tolerance.
- 🧰 **Title bar and status bar resizing.** Title-bar icons now yield one at a time as the window
  narrows instead of all vanishing at once. The status bar now sizes to its real wrapped content — a
  single line hugs its buttons (no reserved phantom second row or extra width) and it only grows when
  a row is genuinely added.

### Added

- 📐 **Fit image button + Fit control scope.** The status bar has a new **Fit image** control beside
  Fit width / Fit height (same Shift+W behavior), and all three follow the new **Fit control scope**
  setting (`status.fitScope`, default **This session and future sessions**): Only the current image /
  This session / This session and future sessions.
- 🧲 **Auto-resize status bar** (`status.autoResize`, default on): shrink the buttons only when the
  window is too narrow to fit them within two rows, restoring the configured size when it grows.
  Toggle in Settings → Status or the status bar right-click menu.
- 📌 **Always on top in the context menus** — a tickable toggle is directly visible in the windowed
  viewer menu and the fullscreen chrome menu.

### Changed

- **Always-on-top strength now defaults to Soft** (Settings → Interface & Behavior). The choice remains
  Soft / Hard; Hard still reasserts topmost after competing Z-order changes.
- **Settings: the Animation tab is folded into Window in Window.** Overlay animation controls (region,
  speed, turn interval/angle, pause, avoid-overlap, refresh target) now sit with the other overlay
  settings, since animation only applies to Window-in-Window overlays.

## [4.2.3-3] - 2026-09-21

### Fixed

- **Some photos were stretched/distorted (EXIF-rotated images).** Glide's lightweight header probe
  reports the raw stored pixel dimensions of a JPEG (e.g. 6016×4016), but the native WIC decoder
  honours the EXIF orientation tag and returns the rotated bitmap (e.g. 4016×6016). The viewport sized
  its destination rectangle from the reported source size and stretched the bitmap into it, so any
  photo with EXIF orientation 5–8 (portrait phone/camera shots, the reported `0089.jpg`) was drawn at
  the wrong aspect ratio. The loader now aligns the reported source orientation with the bitmap the
  decoder actually produced, for every decode route (startup preload, path preview, full/preview
  decode and neighbour prefetch), so rotation is respected without distortion for JPEG and any other
  eXIf-carrying format. Raw/native decode of `0089.jpg` was confirmed as 6016×4016 in and
  4016×6016 out.

## [4.2.3-2] - 2026-09-21

### Added

- **Fit control scope (`status.fitScope`, default "This session and future sessions").** The status bar
  Fit Width / Fit Height controls now carry the chosen fit mode forward instead of snapping back on the
  next image. The new Settings → Status option chooses the scope: **Only the current image** (previous
  behavior), **This session** (every image until Glide closes), or **This session and future sessions**
  (also becomes the persisted Default view for new images).
- **Direct "Always on top" toggle in the viewer context menus.** Right-clicking the image in the
  ordinary window now shows a tickable **Always on top** item at the top level, and the fullscreen
  chrome context menu exposes the same item, so topmost can be toggled without leaving fullscreen or
  opening Settings. The tick reflects the active state (ordinary window or Overlay mode).

### Changed

- Settings search coverage is now **189/189** catalogued settings searchable (178 editable + 11
  actionable) after adding the Fit control scope entry.

## [4.2.3-1] - 2026-09-19

### Fixed

- **Minimizing could make the window disappear.** With Speed Boost enabled (the default), minimizing
  the last Glide window also entered the Speed Boost *standby* path, which removed the window from the
  taskbar and hid it, leaving only the tray icon. A minimized window therefore looked like it had
  closed, and the behavior depended on how many Glide windows were open (only the last window entered
  standby) — matching the intermittent "I minimized it and the window vanished" report. Minimize is
  now a genuine Windows minimize that keeps the window in the taskbar and restorable; Speed Boost
  standby stays reserved for *closing* the last window. Standby additionally requires an
  actually-visible tray icon, so it can never hide the last window with no way back.

## [4.2.3] - 2026-09-19

Search-coverage, small-screen and keyboard-input release.

### Fixed

- **Folder-boundary prompt clipped its buttons on small/scaled screens.** The prompt used a fixed
  245 px height; long folder paths wrapped past it and pushed Continue/Cancel out of view. Prompts
  now size to content, clamp to the owner's working area and scroll long messages. Applied to the
  boundary, Escape-close, rename and profile-name dialogs.
- **One arrow-key press could skip several images on some laptop keyboards.** The existing
  **Navigation minimum interval** setting now ships an 80 ms stock default (instead of a hard-coded
  guard) so a duplicated key event inside the interval is ignored; it remains fully user-configurable
  (0 = unlimited). A schema-5 migration upgrades the historical stock 0 to 80 while preserving
  explicit non-zero values. Losing window activation also stops any held navigation producer so a
  missed KeyUp cannot continue browsing.
- **No automatic diagnostic files in Downloads.** The always-on crash breadcrumb previously wrote
  `Glide-Diagnostic-Latest.txt` (and an abnormal-exit copy) to the user's Downloads folder on every
  run. It is now opt-in only (`--diagnostic-trace` or `GLIDE_DIAGNOSTIC_TRACE=1`); normal installs
  write nothing there.
- **Settings search is now genuinely global.** A deeper audit of the live Settings window found
  **10 settings wired to controls but missing from the catalogue** (invisible to search, profiles and
  diagnostics) — including "Always-on-top strength" (`windows.alwaysOnTopMode`). Six user-facing ones
  were declared in the catalogue; the four removed-Explorer settings stay hidden by contract. Search
  additionally indexes each editor's visible row label, not just its `Content`, so combo/number/text
  settings are found by the wording shown on screen.

### Added

- `SettingsSearchCoverageTests`: asserts every UI-wired setting is declared in the catalogue
  (excluding the intentionally hidden Explorer controls) and that every setting is findable by label,
  terms and visible on-screen wording. Audit: **188/188 searchable (177 editable, 11 actionable)**.

### Changed

- Superseded release notes, the previous full handoff and the audit report moved to `Archive/`; the
  authoritative handoff is now a lean current-state document.
- Version metadata advanced to 4.2.3.

## [4.2.2] - 2026-09-19

Stability and responsiveness release. Fixes a default-configuration crash, removes Settings-search
typing lag, and cleans up release metadata. See [release-notes-4.2.2.md](docs/releases/release-notes-4.2.2.md)
for full detail and evidence.

### Fixed

- **Crash when closing/minimizing with multiple image tabs (default configuration).** Speed Boost
  standby hid the window and then posted `Close()`; the re-entrant closing handler tried to show the
  multi-tab close confirmation against the now hidden owner, which Avalonia rejects. The async-void
  handler rethrew the exception and terminated the process — matching the reported
  `startup-crash.log`. The confirmation is now skipped for standby/hidden owners, owned dialogs are
  refused safely when the owner is not visible, and the confirmation path can no longer escape as an
  unhandled exception.
- **Settings search typing lag.** Search rebuilt a live editor for every match on every keystroke
  (measured 23–39 ms and up to 200 controls per keystroke for common letters). Input is now
  debounced (140 ms), the final query is always rendered, and a per-hotkey list allocation was
  removed.
- **Duplicate "Open" buttons in Settings search results** for action entries such as the Central
  command registry and the gesture matrix. Action rows now render only their labelled companion
  button(s).
- **Maximum-quality performance profile** used the same preview side as Balanced (1800), failing the
  product's own `profile_coordination` diagnostic so every diagnostic export reported `OVERALL FAIL`.
  Maximum quality now uses a larger preview (2400).
- **Self-audit false positive:** `escape.resetRememberedClose` was reported as "unexpectedly
  disabled" even though it is legitimately disabled while the remembered Escape choice is "Ask".

### Changed

- Build banners, deliverable names and diagnostic fixture identities are now derived from a single
  version source (`Directory.Build.props`) instead of hard-coded strings, and all prior versioned
  portable outputs are cleaned generically. This removes the 4.2.0-vs-4.2.1 naming drift.
- Version, assembly, installer and UI metadata advanced to **4.2.2**.
- Stale "Glide 3.0" strings in the crash log, diagnostic bundle header and the native overlay window
  class name were corrected.
- The headless test harness now blocks until a dispatched UI action completes, so UI assertions can
  no longer silently no-op.

### Documentation

- Added this changelog and `release-notes-4.2.2.md`.
- Added the missing `docs/screenshots/` gallery referenced by the README.

## [4.2.1] - 2026-09-17

### Added

- Settings-search completeness: audited the catalogue against the live Settings controls and added
  searchable/editable entries for always-on-top strength, fullscreen exit behaviour, same-image
  behaviour, embedded Explorer theme, new Explorer-tab location controls, folder-navigation
  behaviour, left-window-drag behaviour, Overlay interaction behaviour, and the two slideshow toggles
  previously missing from the editable search map.
- Normalized Overlay/Profile catalogue categories so category-filtered search finds them under
  "Window in Window" and "Profiles & Presets".
- Hotkey/interaction presets can be selected and applied directly from search; reset-hotkeys is
  exposed as a search action.

### Changed

- Always-on-top wording replaced with a technical Soft/Hard explanation.

## [4.2.0] - 2026-09-17

### Added

- Built-in Explorer overhaul with Back/Forward history that preserves the selected file per folder,
  Up navigation that highlights the exited child, and view-mode changes that retire the previous
  `ItemsRepeater` generation.

### Changed

- Folder traversal uses the native Windows logical comparator (`StrCmpLogicalW`), so numeric
  sequences order like Explorer.
- Removed smooth image transitions everywhere; frame changes are immediate.
- Overlay resize behaves like a normal cover-fit resize (no exponential zoom, no letterbox gaps);
  overlay controls auto-hide with the chrome.
- Title-bar Previous/Next Folder buttons route through the canonical serialized folder-navigation
  path.

### Fixed

- Sibling scan-ahead could skip folders; direct sibling navigation now advances exactly one sibling
  at a time, and reparse/link folders are no longer silently removed (with cycle protection).
- Overlay crash when opening multiple instances of the same overlay image.

## [4.1.9] - 2026-09-16

- Audited folder-boundary traversal so nested picture branches are not skipped, with deterministic
  tie-breaking and stale-navigation protection.
- Restored native overlay zoom/enlarge and the on-overlay transparency slider.
- Added independent width-only and height-only overlay resizing.
- Improved high-refresh overlay motion timing and reduced zig-zag after manual repositioning.

## [4.1.7] - 2026-09-16

- Per-overlay animation by region, speed, turn behaviour and interaction pausing.
- Overlay layouts retain each overlay's animation profile and running state; save/load via
  `Ctrl+Shift+S` / `Ctrl+Shift+L`.

## [4.1.6] - 2026-09-16

- Fixed an instant crash opening multiple instances of the same overlay image.
- Added defensive rendering boundaries for Window-in-Window overlays.

## [4.1.5] - 2026-09-15

- Window-placement stability on warm restores; no hidden Alt-Tab windows or duplicate taskbar icons.
- Fullscreen left-click navigates backward; slideshow right-click stop and play-quality indicator.

## [4.1.4] - 2026-09-15

- Navigation crash diagnostic/stability release: transactional Home/End navigation, atomic viewport
  presentation, and expanded diagnostics.

## [4.1.1] - 2026-09-14

- Launch Speed Boost warm engine with a ~3–4 MB standby footprint, Snap preservation, a theme-matched
  privacy curtain, single warm instance, and an installer running-instance safety check.
