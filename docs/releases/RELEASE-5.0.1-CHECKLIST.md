# Release 5.0.1: publish checklist and hand checks

For the owner. Nothing here has been run by the preparation work: the release material was prepared as documents only, and the hand checks need a real PC, the installer and the UI (agents never drive the UI). The ordered publish commands are in `docs/releases/PUBLISH-COMMANDS-5.0.1.md`. Tick each box as you go.

Release material to review: `CHANGELOG.md` ([5.0.1]), `docs/releases/release-notes-5.0.1.md` (the GitHub release body), `README.md` (banner, "What changed since 4.7.1", the Update check section, privacy, requirements), `docs/NET10-MIGRATION-PLAN.md` ("Outcome in 5.0.1").

Release title (no version number): **Glide Image Viewer: .NET 10, safer file operations and update alerts**

Use a fresh 5.0.1 build and throw-away copies of pictures for every check below. For the update-check items use a portable copy in its own folder (or set `GLIDE_SETTINGS_DIRECTORY` to an empty scratch folder), never the installed copy and never your real settings.

---

## 1. Before publishing

- [ ] Run `build.cmd --no-pause` end to end (it wipes `dist\` and builds the installer). Expect no `NU1903` lines, only the 3 known IL warnings (IL2104 ExCSS, IL2104 Svg.Custom, IL3053 Svg.Custom), `Glide.exe` about 36.5 MB, and 5.0.1 in the installer, the window title and Settings > About.
- [ ] Confirm the artifacts were built from the final source (newest source file older than the artifacts), and that `dist-installer\release-5.0.1\` holds `Glide.Setup.exe`, `Glide-5.0.1-Portable.zip` and `Glide.Setup.sha256` (one line: `<hash>  Glide.Setup.exe`).
- [ ] Confirm the final test and audit run: Glide.Core.Tests 1539, Glide.Input.Tests 21, Glide.Explorer.Tests 125 pass; `python tools/audit-settings-search.py` (210 editors, 216 settings, 40 cards, 8 pages) and `python tools/audit-action-registry.py` (99 commands) pass.
- [ ] Replace the placeholder `SHA256_PLACEHOLDER` in `docs/releases/release-notes-5.0.1.md` (one place: the *SHA256 of `Glide.Setup.exe`* line) with the real hash from `Glide.Setup.sha256`; the same value must come from `(Get-FileHash dist-installer\release-5.0.1\Glide.Setup.exe -Algorithm SHA256).Hash`. Then `Select-String SHA256_PLACEHOLDER docs\releases\release-notes-5.0.1.md` must find nothing.
- [ ] Decide on code signing (the release is not signed unless you configured a certificate; see `docs/code-signing.md`).
- [ ] Do the hand checks in sections 3 to 5 (at least checks 1, 2, 5, 7, 8 and 11 to 13) before publishing.

## 2. Publish on GitHub

Follow `docs/releases/PUBLISH-COMMANDS-5.0.1.md` (clean clone, mirror with exclusions, commit `Glide 5.0.1`, tag `v5.0.1`, `gh release create` with the three assets from `dist-installer\release-5.0.1\`, verify). Do not upload `Glide-5.0.1.exe` or `Glide-Zero-Context-Handover.zip`.

- [ ] After publishing, download the assets into a clean folder and check `certutil -hashfile Glide.Setup.exe SHA256` against `Glide.Setup.sha256` and the release notes; open the release page and the README on GitHub (tables, bar column, links and images render).
- [ ] Run check 11 again with a 5.0.1 build: it now reports *You're up to date (5.0.1)* because the latest GitHub release is 5.0.1.
- [ ] Repository About box: unchanged sentence in `docs/GITHUB_ABOUT.txt` is still correct.

---

## 3. Hand checks: .NET 10

1. [ ] **CET-capable PC interop pass** (Intel 11th gen or newer / AMD Zen 3 or newer, Windows 11; Windows Security > Device security shows *Kernel-mode Hardware-enforced Stack Protection*). Glide's exe keeps CET off, but confirm no `0xC0000409` / stack-buffer-overrun crash while you: (a) open the Explorer tab, right-click files with third-party context-menu handlers (7-Zip, Git, cloud-drive overlays), use the preview pane and thumbnail views; (b) open a video that forces the FFmpeg fallback (download the pack if asked); (c) open HEIC / AVIF / RAW / JPEG XL through WIC extensions and any `codecs\` provider; (d) use *Open with* from Windows Explorer on several formats. Optional, for a later release: rebuild with `<CETCompat>true</CETCompat>` and repeat to decide on enabling CET.
2. [ ] **Launch speed:** double-click a JPEG with Glide closed (Speed Boost off, then on) and compare the feel with 5.0.0. For numbers on an idle PC run the visible-window mode: `tools\perf\run-perf.ps1 -Modes app -AppExe <path to the 5.0.0 Glide.exe>` and again with the 5.0.1 exe (it opens visible windows).
3. [ ] **Explorer thumbnails:** after installing, refresh thumbnails in a folder with JPEG, PNG, WebP, PSD and SVG; Settings shows the provider as registered.
4. [ ] **Printing:** the print preview and *Microsoft Print to PDF* work (System.Drawing.Common 10).
5. [ ] **Installer upgrade:** install 5.0.1 over 5.0.0; settings, *Open with* entries and thumbnails survive; launch from the finish page (not elevated); then uninstall and confirm the *Open with* and thumbnail registrations are removed. On Windows older than 10 version 1607 the installer must refuse with a clear message (only if you have such a machine).
6. [ ] **Settings:** a round trip (change, Apply, restart) and a quick look at Settings search; the new Updates card is found by *update*.

## 4. Hand checks: file safety

7. [ ] **Cross-drive folder move with a late file.** On drive C: make a folder `MoveTest` with 300 or more large photos (a few GB, so the copy takes 20+ seconds) and an empty subfolder. In the Explorer tab, Cut it and Paste onto another drive (USB stick or D:).
   - While *Moving 1 of 1* shows, copy one new file `late.jpg` into the source `MoveTest` with Windows Explorer. Expected: the destination has all the original photos and the empty subfolder; the source `MoveTest` still exists and holds only `late.jpg`; an error dialog says *1 item stayed because it changed or arrived during the move*; nothing is missing anywhere.
   - Repeat, but re-save one source photo (open it in Paint and save) during the move: that photo stays in the source with your edit; the destination does not contain an older copy of it.
   - Repeat with a photo held open by a program that locks it: it is copied, the original stays, and the message says it could not be removed.
   - Sanity: an ordinary cross-drive move leaves no source folder behind; a same-drive move is instant as before.
8. [ ] **Multipage TIFF, rotate and save.** Open a multipage TIFF (a scanner or fax TIFF; the information panel shows *Frames/pages: N*), Rotate right, press Save (`Ctrl+S`). Expected: *This file has N pages. Saving would keep only the first. Use Save as to write a copy instead.*; cancelling leaves size and date unchanged. Choose *Save as...*: a warning says only the first page will be written and the original is not changed; confirm and the new file has one rotated page while the original still has N pages. Save as and pick the original file itself: refused, original unchanged. Sanity: a single-page TIFF and a JPEG still rotate and Save in place as before; an animated GIF goes to *Save as* with the first-frame warning.
9. [ ] **Slow network rename and Recycle.** Open an image from a network share (ideally over Wi-Fi or VPN, or a share on a sleeping NAS) and rename it in the viewer (`F2`), including a case-only change (`photo.jpg` to `Photo.jpg`). Expected: the window stays responsive during the rename (move the window, hover buttons, open menus) and the name really changes on the share. Delete an image to the Recycle Bin from the viewer on the share: the window stays responsive and the next image shows afterwards. Disconnect the network and try a rename: Glide does not freeze for the network time-out and nothing is renamed.
10. [ ] **Video decoder** (only if the FFmpeg pack is installed): open a video that needs FFmpeg; it plays as before and the first open in a session does not visibly stall; `%LOCALAPPDATA%\Glide\ffmpeg-9.0.2\.glide-verified` no longer exists afterwards.

## 5. Hand checks: update check (throw-away build, no real settings)

11. [ ] **Real check, up to date.** Before 5.0.1 is published the latest GitHub release is 5.0.0 (after publishing it is 5.0.1): start the build, Settings > General & startup > Updates > *Check now*. Expect *Checking...*, then *Last checked: today HH:mm* and *You're up to date (5.0.1)* (5.0.1 is newer than 5.0.0, and equal to the latest once published); no window appears and *View on GitHub* stays hidden. `glide.settings.json` gains only `UpdateLastCheckedUtc`, `UpdateLatestKnownVersion` and `UpdateLatestKnownUrl`.
12. [ ] **Update available against the live release.** Build a throw-away copy that reports an older version into a scratch folder (never `dist\`): `dotnet build src/Glide.App -c Debug -p:Version=4.9.0 -o "%TEMP%\glide-old"`, run it with `GLIDE_SETTINGS_DIRECTORY` pointing at an empty scratch folder. About 20 seconds after the window appears the prompt *A new version of Glide is available* shows over the Glide window (*You have 4.9.0*, the latest version, how long ago it was released, a *New* pill). *Not now* has the focus (Enter or Esc = *Not now*); it must not return within that day. Delete `UpdateLastPromptedUtc` from the scratch settings file: it returns. Tick *Don't remind me again* and press either button: Settings > Updates shows *Don't remind me about updates* ticked; after a restart no prompt appears, but the card still says an update is available and *View on GitHub* opens the release page in your browser.
13. [ ] **Prompt without the network.** Close Glide. In the scratch settings set `"UpdateLatestKnownVersion": "9.9.9"`, `"UpdateLatestKnownUrl": "https://github.com/cobhc95/Glide/releases/tag/v9.9.9"`, `UpdateLastCheckedUtc` to the current UTC time, `"UpdateLastPromptedVersion": ""` and `"UpdateLastPromptedUtc": null`. Start Glide: about 20 seconds later the prompt appears with no network request; *View on GitHub* opens the release URL (a 404 for 9.9.9 is expected). Set `UpdateLatestKnownUrl` to `https://evil.example/x`: *View on GitHub* must open `https://github.com/cobhc95/Glide/releases/latest` instead.
14. [ ] **When the prompt must wait** (set the state as in check 13 each time): fullscreen (F11), a running or paused slideshow, Settings open, a confirmation dialog, typing in the Explorer address bar or filter box: no prompt, and it appears within about a minute after you leave that state. Another application in front: no prompt until you return to Glide. Two Glide windows: only one prompt, over the active one.
15. [ ] **Offline or failure.** Disconnect the network (or block Glide in the firewall) and start the scratch copy with an empty settings folder: nothing visible happens. *Check now* shows *Couldn't reach GitHub*; no retry loop (about 6 hours until the next attempt). Reconnect and press *Check now*: the status becomes up to date or available.
16. [ ] **Settings behaviour.** The Updates card is collapsed by default, between *History and Open locations* and *Settings window*; searching *update*, *new version*, *upgrade*, *github* and *remind* finds both check boxes. The status line, *View on GitHub* and *Check now* never light up *Apply* or the *N changed* badge. Untick *Check for updates once a day* and Apply: no more requests, *Check now* still works; tick it again: a check runs within seconds. Assign a key to *Check for updates now* (Settings > Mouse & keyboard > Keyboard shortcuts, search *update*) and press it with Settings closed: *Glide is up to date* or the prompt.
17. [ ] **Privacy spot check** (optional, with a proxy such as Fiddler or Wireshark): exactly one HTTPS GET to `api.github.com` per check, with only `User-Agent: Glide-UpdateCheck` and `Accept: application/vnd.github+json`, no cookies, no `Authorization`, no body.
18. [ ] **Speed Boost standby.** With Speed Boost on, close the window to the tray and leave Glide running past a day (or set `UpdateLastCheckedUtc` to two days ago before closing): the check runs in the background without a window; the prompt appears the next time you bring Glide up and it is the active window.
