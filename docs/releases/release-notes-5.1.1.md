# Glide 5.1.1

Released 2026-11-18. A feature release that replaces 5.1.0, which was withdrawn because of a decoder error, now amended. Faster in the places you feel it: instant tab switching, faster preloading of large progressive JPEGs, a window that appears at once when opening from File Explorer, Explorer tabs that follow changes made outside Glide, an optional address bar over pictures, new tooltip and Overlay settings, and browser-style middle-click and new-tab behaviour. 5.0.8 was never published; its changes (new defaults for the mouse side buttons and the end of a folder) are included here. Settings from 5.0.x load unchanged apart from the one-time migrations listed under **Defaults and migration**. The full list is in [CHANGELOG.md](../../CHANGELOG.md).

## Highlights

- **Faster everyday browsing**: tab switching in about 15–40 ms instead of 50–110 ms, large progressive JPEGs preloaded about twice as fast (630 → 300 ms), and the window appears immediately when you open a picture from File Explorer.
- **Instant tab switching**: going back to a recent picture or Explorer tab shows it at once instead of loading it again.
- **Explorer tabs update live** when files are added, deleted or renamed outside Glide.
- **Address bar over pictures** (optional), **tooltip delays per area**, **Overlay mode can be moved** by dragging, with a drag strip, Lock position and **Alt+O**.
- **Middle-click opens in a background tab at the end**, Ctrl+T and + open at the far right, and more choices for which tab activates after closing one.

## Sharper and lighter

- **Pictures are always sharp**: a picture is never left at a reduced or partly decoded size; progressive JPEGs are decoded through their final scan, and every picture reached with next / previous ends up at full detail.
- **Lower memory use**: the pictures around the current one are prepared at screen size, a picture you leave shrinks to screen size, the decode cache keeps only the pictures around you, picture tabs kept ready are stored at screen size, and minimising frees everything but the current picture.

## Speed results

Measured on one development desktop; other hardware will differ.

### Tab switching

Headless test harness, synthetic 3000 x 2000 PNGs and a folder of 1,500 files. Ranges cover the repeated runs.

| Switch | 5.0.7 | 5.1.1 |
|---|---:|---:|
| Back to an Explorer tab from a picture tab | 65–101 ms | 13–42 ms |
| Back to a picture tab from an Explorer tab | 51–107 ms | 16–19 ms |

### Progressive JPEGs

Preparing the next and previous picture is about twice as fast for large progressive JPEGs (30 MP: about 630 → 300 ms), using libjpeg-turbo's reduced-size decoding for preloading only. Pictures on screen are decoded exactly as in 5.0.7.

### Opening from File Explorer with Speed Boost

No before/after figures were measured for this change. What changed is visible: the window is shown immediately instead of after the decode. Glide now writes timing marks to `startup-trace.log` (forward_send, broker_received, warm_window_shown, warm_first_composed_frame, warm_image_presented) so it can be measured later.

## New features

### Explorer tab
- **Live updates from outside Glide.** A file or folder added, deleted or renamed in Windows File Explorer (or by another program) shows up within a fraction of a second, updated in place with no reload or flicker.
- **In-place updates after your own file operations.** Copy, paste, delete, rename, new folder and Refresh update the list like Windows Explorer: other items keep their thumbnails, the selection and the scroll position.
- **Extra info under thumbnails**: Date, Resolution and File size, each optional (off by default), in the Extra large, Large and Medium views.
- **Sort by image dimensions** (Width, Height, Total pixels). The Sort list names every field with ▲ / ▼, so one pick sets field and direction; *Size* is now *File size*.
- **Resizable drive bar** (drag its edge, or *Drive bar width* in Settings, 120–600 px).
- **Ctrl+drag** a selection rectangle to deselect; **Escape** clears the selection; Shift+Home / End select to the start / end of the row (Ctrl+Shift+Home / End for the whole folder).
- **Recent locations** shows full paths, most recent first; the background menu says **Properties of {folder}**.

### Address bar while viewing pictures
Settings > Window & navigation > Explorer tab: *Show address bar while viewing pictures* (off by default; also on the right-click menu of the Explorer navigation row). Back, Forward, Recent locations, Up, Refresh and the address bar stay above pictures in picture tabs, showing the picture's folder, on their own row so the picture is never covered. Clicking a folder, Up or Refresh opens it in the Explorer view of the same tab. Hidden in fullscreen and Overlay mode; hotkey-bindable.

### Tooltips
Settings > Appearance & viewing > **Tooltips**: a delay (0–5000 ms) for the Settings window, the main interface (title bar, tabs, status bar), Overlay mode and Explorer tab items, plus *Show info tooltip when hovering items* for the Explorer tab. The Explorer item tooltip now waits 1.5 s like Windows Explorer; the others wait 0.6 s.

### Tabs
- **Middle-click opens in a background tab at the end**, like a web browser. Two new settings for middle-click only: foreground / background and at the end / next to the current tab / same as new tabs. The *Key to open … instead* keys still flip one middle-click.
- **Middle-click a Recent item on the Welcome page** opens it in a new tab (a folder as an Explorer tab).
- **Ctrl+T and the + button** always open the new tab at the far right and switch to it.
- **New-tab options redesigned**: *New tabs open [at the end / next to the current tab]* and *Open new tabs in the [foreground / background]*, each with its own *Key to open … instead* (None / Shift / Ctrl / Alt). They replace the old *Shift and Alt change how a new tab opens* checkbox.
- **After closing a tab, activate** can now also be **First tab**, **Last tab** or **Custom…** (Home, a new empty Explorer tab, or an Explorer tab with the closed picture's folder).
- **Tab preview size** (50–250 %), also with Ctrl + wheel over the tab (key configurable). The hover card's mouse hint is a row of key chips that follow your bindings.

### Overlay mode
- **Move the window by dragging** the picture while it fits; when zoomed in, drag pans and the bottom bar or **Alt+drag** moves the window.
- **Show drag strip** (a small rounded handle at the top) and **Lock position**, in the Overlay right-click menu and in Settings > Slideshow & overlays.
- **Alt+O** enters and leaves Overlay mode; **Exit Overlay mode** sits under Exit Glide in the right-click menu; the opacity slider follows the mouse wheel (5 % per notch).

## Fixes

- **Glide sometimes did not open after using Overlay mode** (or right after an update). Shutdown now stops the background part, a new launch waits up to 8 s for an older Glide and then opens its own window.
- **Move tab to new window** no longer leaves a stale tab behind, and the new window opens fully on screen.
- **Overlay**: dragging no longer stops after a few pixels; Use windowed interactions and Lock position apply instantly; the drag handle no longer disappears; sizes above 100 % stay above the taskbar; the Size menu ticks the current size; the opacity slider follows the pointer down to 35 %; leaving Overlay restores the normal window size reliably; the Interaction item is now one clear on/off item.
- **Names with an underscore** display correctly in the Welcome page Recent list, the address bar and Explorer menus.
- **Tab previews** show the whole picture for tall portraits; Ctrl + wheel resizes the preview instead of closing it.
- **Explorer**: the selection size includes files when folders are selected; Enter confirms the Delete prompt; "No folders or images in this folder." no longer flashes while a folder loads; the pane no longer flashes dark after copy / paste / delete.
- Sub-menus highlight the item under a still pointer as soon as they open; the Settings footer shows plain "Glide 5.1.1".

## Defaults and migration

- **Mouse side buttons are no longer claimed by Glide by default.** The *Mouse back / forward button* rows in every gesture area default to *Default (not used by Glide)*, so Windows and your mouse software keep them. Rows still on the old default become pass-through on upgrade; explicit choices are kept.
- **When the last image is reached** now defaults to **Ask me each time**. Settings still on the old default (*Move to the next folder*) switch to Ask once (settings schema 17); other choices are kept.
- **Alt+O** is added for Overlay mode once if that row is unassigned and Alt+O is free (settings schema 19).
- The old *Shift and Alt change how a new tab opens* checkbox becomes Shift (position) and Alt (foreground / background) when on, None / None when off.

## Under the hood

- **Settings have one owner.** `SettingsService` owns the live settings; a `SettingsDelta` says which settings changed and only their owners re-apply (a toggle no longer re-applies the whole app). One id-to-property table (`SettingAccessors`), effect rows derived from the settings catalogue, option slices in Glide.Core, Windows-only settings marked per id.
- **Smaller window classes.** The main window was split into focused parts with their own tests: status bar policy and formatter, gesture resolver, tab strip controller, Explorer layout, tab history store, navigation coordinator. Native Windows calls are gathered in one place (`User32`, loaded from System32 only).
- **Launch hand-off.** A launch election waits briefly for an older Glide and otherwise opens its own window (the cause of Glide sometimes not opening after Overlay mode); `startup-trace.log` next to the settings records each launch, hand-off and window/first-frame timing. `GLIDE_SETTINGS_DIRECTORY` isolates settings and instance names for testing.
- **Explorer listing diff.** Folder reloads compare old and new listings by path and apply only removals, inserts and moves to the existing list, so tiles and thumbnails stay; the folder watcher has an Explorer mode (folders, all file types, renames) with a 200 ms debounce.
- **Warm tab cache.** The last 5 picture tabs keep their shown frame (leased so the decode cache cannot drop it) and the last 3 Explorer tabs keep their listing; tab activation no longer saves settings.
- **Master lists instead of literals.** Tooltip areas and delays (`TooltipAreas`), the tab hover-card key chips (built from the live gesture bindings), new-tab and middle-click policies, after-close activation and detached-window placement are each one table or pure policy with tests.
- **Overlay window drag** is computed in screen coordinates (`OverlayWindowDragPolicy`); the drag strip's show/linger/fade is one pure policy.
- **Settings schema 19** (migrations 17–19: end-of-folder default, new-tab keys, Alt+O for Overlay mode only where free).
- **Tooling.** `tools/quick-check.ps1` (fast compile + audits + filtered tests), `tools/audit-setting-wiring.py`, a scripted, dry-run-by-default `tools/release.ps1` (now reads and writes release notes as UTF-8), architecture notes. Test-setup and build-script hardening; the version string no longer carries a source-control suffix.
- A Linux port was assessed (report only, no porting work).

Thanks to vertigo220 for round 3 of reports and ideas.

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.1.1-Portable.zip`: portable build
- `Glide.Setup.sha256`: checksum (SHA256 `b9a45f4afc80a358cfec32b6ed7c48467fa3982b403b3000b580c5c0e960f3a1`)
