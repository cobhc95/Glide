# Glide 5.2.1

Released 2026-11-28. A follow-up to 5.2.0 that fixes all three of its known issues (tab tear-off, overlay resize flicker, missing file-type icons) and adds the opt-in delayed permanent delete with undo from the community round 4 report. Settings from 5.2.0 load unchanged. The full list is in [CHANGELOG.md](https://github.com/cobhc95/Glide/blob/main/CHANGELOG.md).

## ✅ The three known issues from 5.2.0 are fixed

| 5.2.0 known issue | 5.2.1 |
|---|---|
| 🗂️ Tearing a tab off takes a long pull | ✅ **Fixed.** A tab detaches into its own window once it is pulled about 36 DIPs beyond the tab strip's edge (horizontal or vertical strip), like Chrome and Edge, instead of 64 px or more. Reordering along the strip is unchanged and never detaches. |
| 🪟 Flickers when resizing in overlay mode | ✅ **Fixed.** While you drag a handle, the Overlay window stays at a fixed, transparent size and only the picture and its chrome resize inside it. The window itself is resized once, when you release. No flicker, no ghost or double frame, no re-render on every step. |
| 🖼️ Not every file type shows its own icon | ✅ **Fixed.** All 191 single-suffix types Glide supports (up from 39) now show their own document icon with the extension label, and PNG no longer shows the plain Glide icon. |

## 🙏 Thank you, Vertigo220

Thank you to Vertigo220 for round 4 of detailed testing and suggestions in [issue #1](https://github.com/cobhc95/Glide/issues/1). Item G2, the delayed permanent delete with undo, was the one item left over from 5.2.0, and it is in this release. With it, every numbered item of that report is done.

| # | Item | Result |
|---|---|---|
| G1 | 🗑️ Shift+Delete deletes permanently, with a confirmation | ✅ done in 5.2.0 |
| G2 | ⏳ Delayed permanent delete with undo | ✅ **done in 5.2.1**, as an opt-in setting |

### ⏳ Delayed permanent delete (G2)

- Turn it on in **Settings > Window & navigation > Deleting files > "Delayed permanent delete (Shift+Delete with undo)"**. It is **off by default**; with it off, Shift+Delete deletes immediately, as in 5.2.0.
- Shift+Delete still asks first, with the same "Don't ask again for this folder in this tab" box.
- With the option on, after you confirm, Glide shows the next picture at once, hides the file from the picture list, the Explorer tab and the other lists, and deletes it permanently only after about 5 seconds.
- A notice "Deleting <name> permanently... Undo" stays meanwhile. **Undo** brings the file back and shows it again; nothing is deleted.
- Several pending deletes each have their own notice and Undo. Pending deletes are carried out when the window closes. A file that changed or disappeared during the delay is never deleted.

## 🪟 Overlay mode

- **Smooth resize.** A fixed transparent stage window during the drag: only the picture and the chrome (frame, handles, buttons) resize inside it, the window is resized once on release. The picture on screen is scaled with cheap sampling while you drag and redrawn once at full quality on release.
- **Crop and stretch handles work as before.** Right handle: width only. Top handle: height only (bottom edge fixed). Corner: keeps the picture's ratio, or stretches with **Preserve aspect ratio** off. With Preserve aspect ratio on, edge handles crop the picture, and a cropped picture can grow back only to its full size. The window keeps its minimum size and stays within the work area.
- **Click-through outside the picture.** The transparent part of the window no longer swallows clicks meant for other applications. When the picture is smaller than the window, the window is cut to what is visible (picture, frame, handles, buttons, drag strip, and picture-in-picture overlays inside it); everything else passes the click to the window underneath. A picture that fills the window uses no cut.
- **Left-drag always moves the window**, also when the picture is zoomed in or cropped. **Right-drag pans** a zoomed or cropped picture. "Lock the Overlay window position" still stops every left-drag, and "Use normal windowed image interactions while in Overlay mode" still uses your own gesture.
- **Chrome that fits small sizes.** The controls that do not fit are left out instead of overlapping (the opacity pill and zoom buttons below 150 x 132, the side and top handles when they would touch the close button or the corner grip); close and the corner grip always stay, and every control stays inside the window.
- **Restored size is clamped.** Leaving Overlay mode and entering it again brings the window back at its previous size, never larger than the work area of its screen. Sizes seen during a resize drag or while the window settles are no longer remembered.
- Picture-in-picture overlays are unchanged.

## 🖼️ File-type icons

- **Every supported extension has its own icon** with the extension label (JPG, PNG, WEBP, HEIC, CR2 and so on) in File Explorer and Properties: 191 types, up from 39.
- The icons got smaller, not bigger: 0.73 MB in total instead of 1.40 MB for the 39 of 5.2.0. Common formats keep 16 - 256 px frames, rarer ones ship 16 / 32 / 48 px with a palette-compressed image.
- **PNG icon fix.** PNG's association is registered with its own icon like every other type. Files opened through "Open with" (Windows records `Applications\Glide.exe`) get a plain document icon instead of the Glide app icon.
- **For existing users:** to get the labelled PNG icon after installing, **re-pick Glide for .png in Windows Settings > Default apps**. Windows keeps the association it already has until you choose again. After installing, Windows Explorer may also need a restart (or a thumbnail / icon cache clear) before it shows the new icons.

## 📚 Docs

- The README, the format coverage list and the GitHub About text now say **131 of 196 extensions decode out of the box** on a clean Windows install (it said "about 112").

## 💾 Setup size

`Glide Setup.exe` is **20,549,632 bytes (19.6 MB)**, against 21,293,497 bytes for 5.2.0: **-743,865 bytes (-3.5 %)**, even though it carries icons for 152 more file types. The portable zip is 28,578,757 bytes, against 29,302,056 for 5.2.0.

## 🐞 Known issues

None known at the time of release.

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.2.1-Portable.zip`: portable build (SHA256 `C083322FD2547C0F1383CA86D83AED76324C527497B1454596371C51D62CD470`)
- `Glide.Setup.sha256`: checksum (SHA256 `A91207FB547A67108FFC8226BC7EE8F59FA7F26A5AA0A391783F78F36632CF92`); it holds the installer's SHA256, `B577D2C4C6CE11D41ED68080B83386EDA3F55DB5ABCFC4A5715F0D5DF7DD9769`
