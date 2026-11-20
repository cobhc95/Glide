# Glide 5.1.4

Released 2026-11-20. A small follow-up to 5.1.3 with more text overlay polish and a hotkey fix, from vertigo220's feedback. Settings from 5.1.x load with the one-time migration noted below. The full list is in [CHANGELOG.md](../../CHANGELOG.md).

## Highlights

- **Custom text overlay position**: both placement cards now have *Custom*. Alt+drag the overlay anywhere (also out of the title bar or vertical tab bar) and the card in use switches to Custom at exactly that spot; it follows window resizes. Picking any preset, even the same one again, puts the overlay exactly back at that preset. The offset boxes now edit the Custom position.

## Fixes

- **Overlay behind the status bar**: bottom positions now sit above the status bar while it shows and drop to the window bottom when it hides, following it live.
- **Title bar flicker**: an overlay at *Title bar – right* or *center* no longer flickers.
- **Reassigning Space or Enter**: assigning a key that another action already uses now shows the Replace / Cancel question properly instead of cancelling itself.

## Defaults and migration

- Per-position offsets are replaced by the Custom position (settings schema 23). If you had moved the overlay away from its current preset, that card becomes Custom at the same spot.

## Under the hood

- A captured hotkey is applied on key release (`HotkeyCaptureRelease`), so the key never reaches the confirmation dialog.
- Title-bar overlay layout works in whole pixels and is tested to be stable when its own result is fed back.

Thanks to vertigo220 for the reports.

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.1.4-Portable.zip`: portable build
- `Glide.Setup.sha256`: checksum (SHA256 `8daad3dcecfeebee98d10e6656c4ae4bfee38255fe345f445edbaad8078a1cb2`)
