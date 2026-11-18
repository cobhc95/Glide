# Release 5.0.0: publish checklist and hand checks

For the owner. Nothing here has been published or run by the preparation work: the release notes, changelog and README were prepared as documents only. The exact ordered commands are in `docs/history/PUBLISH-COMMANDS-5.0.0.md`. Tick each box as you go. Paths are relative to the project folder unless noted.

Release material to review first: `CHANGELOG.md` ([5.0.0]), `docs/releases/release-notes-5.0.0.md` (the GitHub release body), `README.md` ("What changed since 4.7.1" and the feature tour), `docs/releases/5.0.0-community-feedback.md`, `docs/GITHUB_ABOUT.txt` and `docs/GITHUB_DESCRIPTION.txt`.

Release title (no version number): **Glide Image Viewer: faster, safer, with a built-in Explorer**

---

## 1. Before publishing

- [ ] Read the release notes and README once. Check the performance table against `perf-report.md`, the print-preview numbers against `print-perf-report.md`, and the wording of every Security bullet. vertigo220 is deliberately thanked in the release notes, the changelog, the README and the feedback map (this replaces the earlier no-thanks rule); check that the wording is right and that no title contains a version number. The release notes show images through absolute `raw.githubusercontent.com` URLs, so **the images in `docs/screenshots/5.0/` must be on `main` before the release is created** (otherwise the release page shows broken pictures).
- [ ] 4.8.0 was never published: confirm there is no `v4.8.0` tag or release on GitHub (`gh release list --repo cobhc95/Glide`, `git tag -l "v4.8*"`). If one exists, delete it before 5.0.0.
- [ ] Confirm the artifacts were built from the final source. From the project folder in PowerShell, the newest source file must be older than the artifacts:
  `Get-ChildItem src,native,installer,tools,Directory.Build.props -Recurse -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1 FullName,LastWriteTime`
  then compare with `Glide-5.0.0-Portable.zip`, `Glide-5.0.0.exe`, `dist-installer\Glide Setup.exe` and the three files in `dist-installerelease-5.0.0\`. If any source file is newer, run `build.cmd` again.
- [ ] Confirm the native DLLs in the build are the rebuilt ones (the performance numbers and the 512 MP cap depend on them). In a Developer PowerShell, in `dist\`:
  `dumpbin /exports Glide.Native.dll` lists `GlideDecodeImageIntoW` and `GlideGetMaxDecodePixels`;
  `dumpbin /headers Glide.Native.dll` and `dumpbin /headers Glide.ShellThumbnail.dll` show "Control Flow Guard" and "CET compatible".
- [ ] Check the version: right-click `dist\Glide.exe` > Properties > Details shows 5.0.0, and Settings > About shows 5.0.0.
- [ ] Confirm the final test run on the final source: Glide.Core.Tests 1258, Glide.Input.Tests 21 and Glide.Explorer.Tests 125 all pass, `python tools/audit-settings-search.py` (208 editors, 214 settings, 39 cards, 8 pages) and `python tools/audit-action-registry.py` (98 commands) pass, and the settings catalogue has 269 entries (the figures written into the changelog and release notes; the test counts were supplied by the lead and are not re-run by the documentation job).
- [ ] Decide whether to sign the release. The release is currently not code-signed (no certificate has been obtained), so SmartScreen may warn. See `docs/code-signing.md`. If you sign, rebuild with `GLIDE_SIGN_CERT_THUMBPRINT` or `GLIDE_SIGN_PFX` set so the checksum is written after signing.
- [ ] Do the hand checks in section 3: groups A (performance), B (security) and C (install and upgrade) before publishing, the rest as time allows. Remember that the release material changed after the first draft (many more changes landed), so read `CHANGELOG.md` [5.0.0] and the release notes again, not from memory.

## 2. Publish on GitHub

Assets (already named correctly in `dist-installer\release-5.0.0\`):

| File in `dist-installer\release-5.0.0\` | GitHub asset |
|---|---|
| `Glide.Setup.exe` | `Glide.Setup.exe` |
| `Glide-5.0.0-Portable.zip` | `Glide-5.0.0-Portable.zip` |
| `Glide.Setup.sha256` | `Glide.Setup.sha256` |

Do not upload `Glide-5.0.0.exe` or `Glide-Zero-Context-Handover.zip`. The `Glide-5.0.0-Portable.zip` in the project root is byte-identical to the one in `release-5.0.0` (same SHA-256 when checked); use the `release-5.0.0` copies. The old `dist-installer\Glide Setup.exe` / `Glide Setup.sha256` pair is the build output name, not a GitHub asset name.

- [ ] Verify the checksum file in `dist-installer\release-5.0.0\`: its single line must read `<hash>  Glide.Setup.exe` (the file name must match the asset) and `<hash>` must equal `(Get-FileHash dist-installer\release-5.0.0\Glide.Setup.exe -Algorithm SHA256).Hash`. The release notes quote the hash (`FDD05A27...8C7E`); if the installer is rebuilt, update the SHA256 line in `docs/releases/release-notes-5.0.0.md` to the new value before publishing.
- [ ] Sync the source to a **clean clone** of `cobhc95/Glide` exactly as written in `docs/history/PUBLISH-COMMANDS-5.0.0.md` (robocopy with the exclusions listed there: `Archive\`, `.artifacts\`, `artifacts\`, `dist\`, `dist-installer\`, `build-output.txt`, `installer-output.txt`, `Glide-*.exe`, `*.zip`, `.vs`, `bin`, `obj`, native `build` folders). Then check `git status` in the clone: nothing under `Archive\` and no `.zip` / `.exe` may be staged.
- [ ] In the clone, check `git status` and `git diff --stat`: the changes should be source, tests, tools, docs, `README.md`, `CHANGELOG.md`, `Directory.Build.props`, the new `docs/screenshots/5.0/` images and the moved `docs/releases/` files; `docs/releases/release-notes-4.8.0.md` must not exist, and `docs/screenshots/4.6/` should be gone.
- [ ] Commit and push to `main` (**images first, release second**): the release notes load their pictures from `https://raw.githubusercontent.com/cobhc95/Glide/main/docs/screenshots/5.0/<name>`, so those files must be on `main` before `gh release create`. Open one of the raw URLs in a browser after pushing to confirm it loads.
- [ ] Tag the release commit and push the tag (`git tag -a v5.0.0 -m "Glide 5.0.0"`, `git push origin v5.0.0`).
- [ ] Create the release with `gh release create v5.0.0 --repo cobhc95/Glide --title "Glide Image Viewer: faster, safer, with a built-in Explorer" --notes-file docs/releases/release-notes-5.0.0.md --latest` and the three assets from `dist-installer\release-5.0.0\` (full command in `PUBLISH-COMMANDS-5.0.0.md`). To review on GitHub before it is public, add `--draft`, then publish the draft from the web page.
- [ ] Update the repository About box on GitHub: paste the sentence from `docs/GITHUB_ABOUT.txt` into *Description*, add the suggested topics, and (optional) set the website to the releases page. `docs/GITHUB_DESCRIPTION.txt` holds a longer description you can use elsewhere.
- [ ] After publishing, download the assets from the release page into a clean folder and verify them: `certutil -hashfile Glide.Setup.exe SHA256` equals the line in `Glide.Setup.sha256` and the SHA256 quoted in the release notes; run the downloaded installer on a test PC or VM if you can.
- [ ] Open the release page and the repository front page: the title has no version number, the notes render (tables, bar columns, lists) with all images visible, the README renders its screenshots and tables, the README links to `docs/releases/release-notes-5.0.0.md` and the assets work.
- [ ] Reply on (or close) issues #1, #2 and #3 when you are ready: they are still open and everything in them shipped (see `docs/releases/5.0.0-community-feedback.md`). Nothing has been posted to them.
- [ ] Missing README screenshots (optional, not a blocker): Welcome page, stacked status bar close-up, title-bar customiser, gesture dropdown, hotkey conflict prompt, Reset Glide dialog, GIF controls, a Print window from a 5.0 build.

---

## 3. Hand checks on real hardware

One consolidated, de-duplicated list, numbered so you can tick through it. These could not be done by the build and test runs (agents never drove the UI, the installer, a real printer, a second monitor or a real mouse). Everything was merged from the performance, security (two passes), hygiene, hotkey / action registry audit, menu-cursor, print-performance and window-inventory reports and from the changelog notes of each piece of work. Groups A to C are the ones to do before publishing; the rest can follow in any order, and a failure in any of them is a note for a follow-up release, not necessarily a blocker. Test Reset Glide (group J) on a portable copy with its own settings folder, not on the Glide you use every day.

### A. Performance and large images

1. [ ] Open 24 MP and 100 MP JPEGs, a 24 MP and a 100 MP PNG / TIFF / BMP and a WebP at Fit: the picture appears quickly and settles to a sharp image with no visible blur-to-sharp flicker.
2. [ ] Zoom and pan a 100 MP image at Fit and at 400 % on the real GPU renderer (the benchmark only measured the CPU renderer: 43 ms per frame at Fit, 39 ms at 400 %). It should feel smooth.
3. [ ] Hold the arrow key through a folder of 4K or 24 MP images, then release: it settles to the full-quality image. Browse at a relaxed pace and check that Task Manager shows low CPU use.
4. [ ] Jump around a large folder with random jumps (click far in the Explorer tab, Home / End): no stalls, and memory in Task Manager stays reasonable.
5. [ ] Play a 1920x1080 animated GIF with about 60 frames: it animates (it used to stay static).
6. [ ] Launch Glide cold (no Glide running) by double-clicking a 100 MP JPEG in Windows Explorer: the first frame appears promptly.
7. [ ] On a PC with the Windows WebP extension installed (the benchmark PC has none), open a few WebP files: they open and look right.
8. [ ] Open and close several windows and tabs, and browse a large folder for a few minutes while watching memory in Task Manager: memory returns to a steady level (checks the new bitmap lease logic and the refactors).
9. [ ] Open a 300-500 MP image (for example a stitched panorama or a 20000 x 20000 JPEG) with plenty of free RAM: it reaches full resolution. Repeat with little free RAM (many apps open): it stays on a sharp preview, with no crash.
10. [ ] Open an image over 512 MP (or a deliberately oversized test file): Glide stays on a reduced preview and does not crash.

### B. Security, SVG and profiles

11. [ ] With Glide running, double-click images in Windows Explorer: they open in the running Glide window (single-instance hand-off), also while Speed Boost has Glide in standby.
12. [ ] Start Glide as administrator, then double-click an image in Explorer: it opens in its own window (expected, no forwarding).
13. [ ] Run 4.7.1 (portable) and 5.0.0 side by side, then double-click an image: each version opens its own window (expected).
14. [ ] Open SVGs with embedded `data:` images and with absolute local linked images: they render as before. An SVG linking to `http://` or `\\server\share` images renders without those images, and a network monitor shows no connection. An SVG with a relative image link now renders (without that image).
15. [ ] Open a few SVGs in the NativeAOT build (`dist\Glide.exe`) after the Svg.Skia upgrade: they render (the security pass could not run the AOT exe).
16. [ ] Load a profile and import a settings file that name external programs: the status line says external programs were not imported and your existing programs remain. Load one containing `\\server\share` folders: the status line says network folder paths were not imported and the folders are not set. A file over 16 MB is refused.
17. [ ] Play a long or large animated GIF: playback is unchanged.
18. [ ] Play a video that needs the FFmpeg download, restart Glide and play it again: the second start is fast and the video plays.
19. [ ] Modify one FFmpeg DLL in `%LOCALAPPDATA%\Glide\ffmpeg-9.0.2` on a test PC: Glide refuses to load it and offers to download a clean copy.
20. [ ] In the Explorer tab, copy files in Windows Explorer and paste in Glide: works; cut and paste works.

### C. Install, upgrade and regression pass

21. [ ] Fresh install on a clean PC or VM: installs, no .NET prompt, Open With entries appear, Explorer thumbnails register, uninstall removes them.
22. [ ] Upgrade over 4.7.1 (or the previous version): it finds the same folder, closes running Glide, settings and tabs survive, and "Launch Glide" at the end starts Glide **not elevated** (Task Manager > Details > Elevated column).
23. [ ] Upgrade with a real 4.7.1 settings file: every old mouse toggle behaves exactly as before (settings schema 7); the show / hide status bar key is now Ctrl + Shift + B and the zoom step is 15 % (schema 8); a title bar still on the previous default gains Back / Forward once and a customised one is unchanged (schema 9); import an old profile and check the same.
24. [ ] Install to `C:\Glide` (custom folder) and run `icacls C:\Glide`: SYSTEM and Administrators full control, Users read and execute, not inherited. Install to Program Files and to `%LOCALAPPDATA%\Programs\Glide`: both work as before.
25. [ ] The refactors changed no behaviour, but nothing exercised them in the real UI: drag a tab to reorder it and out to a new window; open Settings (no empty `%TEMP%\Glide2-UIEvidence-*` folder appears) and use *Export diagnostics*; switch through every theme and accent colour; record a hotkey with modifiers (it shows e.g. Ctrl+Shift+Left); overlays drift smoothly in both overlay renderers; command-line modes (`Glide.exe --diagnostics` and the file-association register / unregister actions) from the published build; *Open with* and *Show in Windows File Explorer* from the Explorer tab.
26. [ ] Tray icon and Speed Boost: with Speed Boost and its tray icon on, close all windows (Glide goes to standby), then use *Exit Glide* (viewer menu item or the hotkey action): Glide leaves Task Manager completely. *Close Glide window* (Ctrl + Shift + W) with two windows open closes only one.

### D. Explorer tab and thumbnails

27. [ ] Right-click the new-tab (+) button and a tab: *New Explorer tab* opens an Explorer tab. Set *New tab opens* and *Home page* to Explorer and check both. Open a folder from Windows (right-click a folder > open with Glide, if registered): it shows in an Explorer tab.
28. [ ] Click, Ctrl / Shift click, rubber band, Ctrl + A, arrows, Home / End, PageUp / PageDown, type to jump, double-click, Enter, middle-click on an image and on a folder, right-click menus (file, folder, several items, background), the mouse wheel.
29. [ ] Breadcrumb bar: click a segment, click the empty part and type a path, a bad path shows an error, Esc cancels; Back / Forward / Up, Alt + Left / Right (Back / Forward), Alt + Up (Up), Backspace (Back when there is a step back; with no history nothing happens in the Explorer), F5, navigation pane, filter (Ctrl + F), every icon size, List and Details with sorting and column resizing.
30. [ ] Thumbnails load for visible tiles; scrolling a 5000-file folder stays responsive; switching between two Explorer tabs keeps selection, scroll position, view, sort and filter.
31. [ ] File operations in a test folder: F2 rename, new folder (Ctrl + Shift + N), Delete (Recycle Bin, confirmation), Shift + Delete (permanent, stronger confirmation), Ctrl + C / X / V (a name clash becomes *name - Copy*), drag to move, Ctrl + drag to copy, pasting into Windows File Explorer and back.
32. [ ] Session restore after a kill: leave an Explorer tab in a folder with a file selected and scrolled down; wait a few seconds; kill Glide in Task Manager; relaunch with *Open last session*: the tab comes back in that folder, with the file selected and in view. Repeat after renaming the folder (it opens the nearest parent that exists) and after deleting the file (the folder opens with nothing selected).
33. [ ] Explorer settings: default view, show hidden files, only folders and images each take effect.
34. [ ] Windows Explorer thumbnails: Settings > Windows & developer > Explorer thumbnails: click *Apply thumbnail settings* then *Refresh thumbnails* for Fast, Balanced and High; the quality visibly differs; rotated photos are upright; GIF thumbnails show the first visible frame; the status line shows the provider and its version instead of *Provider: missing*; *Custom* mode keeps thumbnails on.
35. [ ] Title bar: the *Show in File Explorer* button opens Glide's Explorer tab with the image selected; right-click it and switch to Windows File Explorer; the choice sticks.
36. [ ] Image keys in other tabs: in an Explorer tab, click the tab header (focus leaves the list) and press Delete, F2 and R: nothing happens to the image of another tab. With the Explorer list focused, Explorer's own Delete and F2 still work.

### E. Mouse gestures and real mouse hardware

37. [ ] Gesture panel: every area (Windowed, Fullscreen, Empty background, Selection, Slideshow, Window in Window, Tabs, Title bar, Explorer): default rows say what they do, a changed row shows the outline and dot, the dropdown looks like a field with a down arrow, *Changed only*, *Reset area*, *Reset all gestures*, search, area options under the table, and 6 custom gestures per area (the first match wins).
38. [ ] Try the new inputs: Shift + wheel, Alt + wheel, tab-strip wheel, title-bar right / middle / double click, slideshow clicks, fullscreen double-click, Window in Window right-drag.
39. [ ] Mouse side buttons on a real mouse with back / forward buttons: by default they go Back / Forward (see checks 81 to 89); set Windowed image > Mouse back button to *Do nothing* and it stays inert while other areas still go Back; bind back = Previous image and forward = Next image in Fullscreen image, bind them in Tabs (previous / next tab) and in Explorer (Explorer: back / forward / up) and check they run; add a custom gesture with Ctrl held. Settings search finds them with *side button*, *mouse back* and *mouse 4*.
40. [ ] Bind *Toggle: Auto-hide status bar* to a key and to a middle-click gesture: the change applies at once and persists after a restart (also try one *Cycle:* action, for example *Cycle: Status bar size*).
40a. [ ] Right-click the empty background around an image (the letterbox, not the picture): the menu appears when you release the button, and a plain right-click there opens it. Hold the right button and turn the wheel over the empty background: the image zooms and no menu appears on release. Right-drag from the empty background: no menu. Repeat on the image itself and in fullscreen: same behaviour.

### F. Keyboard shortcuts and actions

41. [ ] Hotkey list: click the Shortcut, Action and Category headers (sort, then reverse; the arrow shows the column; unassigned stay at the bottom when sorting by shortcut).
42. [ ] *Search hotkey*: press a combination: the list shows its action or says it is not assigned; Esc cancels; the pressed keys never run a command; *Show all hotkeys* restores the list. Ctrl + F finds the fixed Explorer entry; Tab and Ctrl + Shift + Left can be recorded and looked up. Fixed rows are greyed and not editable.
43. [ ] Custom presets: *Save current as preset...*, apply it, rename it, delete it; built-in presets cannot be renamed or deleted; presets survive export and import of a profile.
44. [ ] Assign a shortcut that is already in use: Glide names the owning action and asks *Replace* or *Cancel*; Replace removes it from the old action; Cancel changes nothing.
45. [ ] *Explore/Browse containing folder*: assign a key; it opens the image's folder with the image selected (Glide Explorer tab or Windows File Explorer per *Show in Explorer button opens*). Search for it with *browse*, *reveal*, *locate*.
45a. [ ] Press **Ctrl + E** on an image: Glide's Explorer tab or Windows File Explorer opens (per *Show in Explorer button opens*) with the image selected. The title-bar *Show in Explorer* tooltip shows Ctrl+E (and your key if you rebind it). In an Explorer tab it behaves as before.
46. [ ] Scroll the hotkey list with the mouse wheel while a tooltip is showing under the pointer: scrolling keeps working.
47. [ ] **Ctrl + Shift + B** with two or more input languages installed: the status bar collapses / expands and the keyboard layout does not switch. Alt + Shift + S no longer does anything. A settings file that still has the old default moves to Ctrl + Shift + B; a binding you changed is kept; tooltips show the key you have assigned.
48. [ ] Ctrl + Tab and Ctrl + Shift + Tab (also after rebinding them) and the most-recently-used cycle while holding Ctrl.
49. [ ] Click a status-bar or toolbar button (for example Fit), then press Space (next image) and Enter (fullscreen). Repeat with *Tab key button navigation* on: there Enter and Space click the focused button.
50. [ ] Keys that Windows or other programs commonly intercept: Ctrl + Alt + S (Save as), F11, Alt + Space, Win-key combinations, media keys, screenshot-tool hooks (Snipping Tool, Lightshot), IME toggles (Shift + Space in CJK IMEs) and AltGr layouts (AltGr is Ctrl + Alt: Ctrl + Alt + S may type a character).
51. [ ] Zoom step: set *Viewport zoom step* (Settings > Appearance & viewing) to a different value such as 25 %: the Zoom in / out keys, menu items and status-bar buttons use it at once and the wheel uses 80 % of it. At the default (15 %) the feel matches the old behaviour (15 % keys, 12 % wheel).
52. [ ] Fit hotkeys follow *Fit control scope*: try each scope (current image, this session, this session and future); press 1, 2, 3, 4, Shift + W and Ctrl + H and check what applies (and that *Default view for new images* changes with the last scope).
53. [ ] Spot-check a few of the commands that now have a hotkey row and gesture (Maximize / restore, Minimize, New Explorer tab, Jump 10 images, Zoom to 200 %); the new title-bar buttons (rotate, flip, fit width / height, Overlay mode) in *Customize title-bar buttons*; and that context menus show the shortcut you have assigned.

### G. Status bar

54. [ ] Auto-hide status bar: turn it on (right-click menu and Settings): the bar is fully hidden (no arrow, no button) and appears when the pointer enters where it would appear, on the image viewer and on the Home page, windowed and fullscreen; it stays while the pointer is on the bar or one of its menus, hides about half a second after you leave it, and hides at once when the pointer leaves the window.
55. [ ] Reveal area equals the bar's own box: with a fresh settings file (hover reveal is on by default) collapse the bar and move the pointer to where it would appear: it reveals. Check a wide window, a narrow window where the bar wraps into rows, and Left / Center / Right; with Auto-hide on the area is not wider than the revealed bar. Change *Status bar reveal area adjustment* (-40, 0, +300): the area shrinks and grows. It does not block clicks or gestures on the image, does not trigger while a mouse button is held down, and lingering inside a grown area away from the bar does not make it pop back until you leave and re-enter.
56. [ ] The *Reveal the collapsed status bar when the pointer enters where it would appear* checkbox is in Settings > Tabs & status bar > Status bar only (not in the bar's right-click menu) and can be turned off.
57. [ ] With *Collapsed button in lower-right corner* off, collapse the status bar (windowed, fullscreen, multi-row bar, after resizing): the expand button is a very small arrow on a subtle backing, readable on both a dark and a bright image, exactly where the collapse button was, and shows the full button only on hover.
58. [ ] *Reserve status bar space when fitting*: Fit, Fit to window, Fit width, Fit height size to the area above the bar and re-fit when the bar collapses, expands or hides.
59. [ ] Tab tooltip text size (Small, Medium, Large): the file name shows in larger text above the path.
60. [ ] Leave fullscreen: the window does not flash at the wrong position first.

### H. Menus and the fullscreen pointer

61. [ ] Fullscreen with the cursor auto-hide on: wait until the pointer hides, then right-click: the arrow appears and stays on the menu and on the Size / Position sub-menus for more than 3 seconds. Close the menu and wait 3 seconds over the image: it hides again. Repeat for the Explorer's View and Sort by sub-menus, tab and title-bar menus, the Explorer view-mode drop-down and the status bar overflow menu.

### I. Settings window and startup paths

62. [ ] The Settings window opens at about 930 x 630 on a normal screen and fits fully on screen (centred over Glide) on a small or high-DPI display (for example 1366 x 768 at 150 %); at 720 wide nothing runs off the page and the gesture table and shortcut list stay usable.
63. [ ] Settings remembers size, position and maximized state after OK, Cancel and the close button; after moving it to a monitor and unplugging that monitor (or when the saved position is off screen) it opens centred.
64. [ ] Settings pages: open every page; cards start collapsed; click and keyboard (Enter / Space) toggle a card; *Expand all* / *Collapse all*; an *N changed* badge and hover list appear after changing a setting and vanish after resetting it; open / close state survives switching pages; a page keeps its scroll position while Settings stays open.
65. [ ] Settings search: type a few settings (for example *auto-hide*, *tooltip*, *scroll window*, *middle click tab*, *side button*); results show *Page > Card*; *Open* switches page, opens the card, scrolls, highlights and focuses.
66. [ ] Custom startup path: with *When Glide opens = Custom*, a folder opens in an Explorer tab and an image file opens the image; a missing or non-image path opens the Welcome page and says why (no dialog). Repeat for *New tab opens = Custom*, and with *New tab opens = Explorer tab* (new Explorer tab opens exactly the custom path). *Open last session* still wins over a stored custom path; the *File...* picker filters to images; a pasted path with quotes or `%VARIABLES%` works.

### J. Reset Glide (use a portable copy or a throwaway settings folder)

67. [ ] Settings > Windows & developer > Reset Glide: the card is after Diagnostics; tick boxes, *Select all*, *Clear*, the *N selected* count; the red button is disabled until something is ticked.
68. [ ] The confirmation lists exactly the selected items; Cancel has the focus; Esc cancels; Enter never starts the reset.
69. [ ] *Save a backup of my settings first* (ticked by default) creates `Documents\Glide backups\Glide backup <date time>` with settings, profiles, last session, history and the small settings files; if the backup cannot be written, nothing is reset.
70. [ ] Reset *All settings* (or hotkeys, profiles, history, last session): Glide closes, the windowless helper wipes the data, Glide restarts with the wiped state and the dialog said *Glide will close and restart*. Resetting only caches or registry entries needs no restart and the dialog does not say so.
71. [ ] A file in use (another Glide window or program holds it): it is retried, then reported by name with the reason; the other items still run and you are told what was not reset.
72. [ ] Reset *Registry entries*: the amber warning appears; Glide disappears from Open With until you add it again and the next start does not register it silently; *Startup and background entries* switches off *Start with Windows in background* and Speed Boost; the thumbnail entries are removed.
73. [ ] What is not removed: machine-wide (HKLM) registrations, Windows default-app choices, `thumbcache_*.db`, `crash.log` / `startup-crash.log`, the program folder, your pictures, exported settings, profile files and backups. Works the same on the installed and the portable copy. Settings search finds the card (*factory reset*, *wipe*, *uninstall cleanup*), and the footer *Restore defaults* button still only resets the options shown.

### K. Window memory and multi-monitor DPI

74. [ ] Move and resize the Print window, *Customize title-bar buttons* and the Diagnostics results, move a confirmation dialog (for example *Rename image* or *Close Glide tabs?*), close them and open them again: the resizable windows return at the same size and position, the dialogs at the same position.
75. [ ] Multi-monitor and DPI: with two monitors of different scaling (for example 100 % and 150 %), leave Settings and Print on the second monitor, close and reopen them: right monitor, right size. Unplug that monitor (or reopen with the saved position off screen): the window opens centred on its owner. *Reset Glide > Window sizes and positions* makes every window open at its default again. The main window still follows *Remember window position* and restores correctly from fullscreen.

### L. Printing (real printer)

76. [ ] With a real local printer: open Print. The first open in a session briefly says *Reading printer settings...* and then shows the preview; Portrait / Landscape, scaling modes, custom scale, margins, alignment, copies and colour update the preview instantly; changing the paper shows the new sheet at once with *Updating preview...* and refines; switching printer keeps the window usable. Open Print a second time: the preview is there at once.
77. [ ] Print real pages (or to a PDF printer and inspect the file): portrait and landscape, Best fit, Fill page, Actual size, Custom scale and Stretch, auto-rotate, margins, nine alignments, copies and grayscale match the preview.
78. [ ] A network printer (for example a WSD / IPP printer) and a Remote Desktop redirected printer: the window stays usable while the driver is read (the first read can take seconds, and up to 48 s was measured on one WSD printer; that is the driver). The driver *Properties* sheet still opens and the preview refreshes after it.
79. [ ] Print preview of a 50 MP image, including grayscale: no lag.

### M. Animated GIFs

80. [ ] Open an animated GIF: `P` pauses and plays, `,` and `.` step back and forward (pausing first, wrapping at both ends); the right-click menu items and status-bar buttons appear only while a GIF is open; the keys can be changed in Hotkeys.

### N. Back / Forward (tab history)

81. [ ] Real mouse side buttons (XButton1 / XButton2): Explorer tab -> double-click an image -> press the mouse BACK button over the image: the Explorer returns in the same folder, image selected, same scroll position. Press the FORWARD button over the Explorer list: the image comes back. Repeat over the tab strip (acts on the active tab), in fullscreen, and over the Welcome page after Back to Home.
82. [ ] Settings > Mouse & keyboard > Windowed image > Mouse back button = *Do nothing*: the button does nothing there while the other areas still go Back. A mouse whose driver remaps the side buttons (Logitech Options and similar): Glide still receives XButton1 / XButton2.
83. [ ] Back / Forward must NEVER fire while typing: Explorer address bar (Ctrl + L), filter box (Ctrl + F), inline rename (F2), the Settings search box and hotkey capture, the image rename dialog (F2), the slideshow interval dialog and the print dialog. Backspace, Shift + Backspace, Alt + Left and Alt + Right only edit text (or do nothing); the folder and the tab stay as they are.
84. [ ] Real keyboard: Alt + Left / Alt + Right in an image tab and in an Explorer tab; Shift + Backspace = Forward; Backspace in an image tab opened from the Explorer tab = Back (the Explorer returns with the image selected, Shift + Backspace returns to the image); Backspace in an image tab with no history (an image opened directly from Windows Explorer, a fresh tab or window) = Previous image; Alt + Left there does nothing; Backspace in the Explorer list = Back, or nothing when there is no history; Left arrow is still Previous image; Alt + Up = Up one folder. Alt alone opens nothing unexpected (no menu flashing).
85. [ ] Title bar: on a fresh profile Back / Forward are the first two buttons, dimmed until there is history; the tooltip reads *Back (Alt+Left)*; clicking a dimmed button does nothing (no window drag or maximize). On your own profile with a customised title bar the layout is unchanged, and *Customize title-bar buttons* lists Back / Forward at the top of *Images and tabs*.
86. [ ] Long folder: scroll far down in the Explorer, open an image, arrow to a few other images, press Back: that last image is selected and visible; Forward shows it. Home -> open an image in the same tab -> Back returns to Home. An image opened in a new tab starts with Back dimmed.
87. [ ] Drag a tab with history to another Glide window and detach one: Back still works there. Delete the opened image in Windows Explorer while in the Glide Explorer, then press Forward: a short *no longer exists* message and nothing breaks.
88. [ ] *Open last session*: restart Glide with Explorer tabs open; folders, selection, scroll and folder Back still restore as before.
89. [ ] Narrow window: title-bar buttons drop one at a time from the left, so Back / Forward are the first to drop (they lead the list). Decide whether this is acceptable.

### O. Stacked status bar

90. [ ] Fresh settings file, wide window: the file info (name, position, size, zoom) is on its own line at the top of the status bar with the minimise and close buttons at its right and the button groups on the row below; the bar is about one row taller than in the previous version. Same on the Home page, windowed and fullscreen.
91. [ ] Turn *Always stack the information on a separate line* off (Settings > Tabs & status bar > Status bar, then also from the bar's right-click menu): the wide window shows the single line again at once, and the reserved fit space, the collapsed button and the auto-hide / hover reveal area re-measure immediately. Turn it back on; the state survives a restart.
92. [ ] Settings search finds the option with *stack*, *separate line*, *two rows*, *wrap* and *single line*, and it can be bound to a key and to a gesture (*Always stack the information on a separate line*). With a very long file name the name wraps onto at most a second line and ends in an ellipsis, and the bar does not grow wider.

---

## 4. Open decisions reported by the hotkey audit (behaviour unchanged, not release blockers)

Fixed since the audit and now in the release notes: the zoom step, mouse side buttons, *Exit Glide*, the Fit hotkeys following *Fit control scope*, and the status bar default key. Still open as far as the reports say (check, then decide):

- [ ] *Show / hide status bar* while *Auto-hide* is on only flips a remembered flag, so it has no visible effect (by design). Proposal from the audit: reveal the bar as if hovered until the pointer leaves, or pin it until pressed again.
- [ ] The status-bar 100 % button clears the selection-zoom demand but the *Actual size* command does not.
- [ ] *Open in external program 1-3* (Shift+1/2/3): in an Explorer tab with an image selected it opens that image; with nothing, a folder or a non-image selected it does nothing and the Explorer message line says "Select an image first" (it must never open the picture of another tab); in a Welcome tab it does nothing; in an image tab it opens the current image.

## 5. After release

- [ ] Open the next job: the .NET 10 migration (`docs/NET10-MIGRATION-PLAN.md`), which targets 5.1.0 and should start only once 5.0.0 is tagged.
- [ ] Note any hand-check failures as issues and fix them in a follow-up release.
