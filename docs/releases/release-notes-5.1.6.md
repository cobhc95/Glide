# Glide 5.1.6

Released 2026-11-22. A small follow-up to 5.1.5: reopening a closed tab now brings it back as if it was never closed. Settings from 5.1.x load unchanged. The full list is in [CHANGELOG.md](../../CHANGELOG.md).

## Fixes

- **Reopen closed tab** (Ctrl+Shift+T) restores the tab at its old place in the tab strip (or the end if fewer tabs remain) and makes it active, with its full Back / Forward history and current place in it. Picture tabs keep their picture, zoom, pan and fit; Explorer tabs keep their folder, selection, scroll and folder history. Several closed tabs reopen most recent first. Previously the tab reopened at the end with no history.

## Under the hood

- Closed tabs keep a full snapshot of the tab (reusing the state carried when a tab is dragged to another window) together with their original index.

Thanks to vertigo220 for the report.

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.1.6-Portable.zip`: portable build
- `Glide.Setup.sha256`: checksum (SHA256 `445f99394a75e14206f2f4e0100fd38eb0b6e6fabc942836623cc78657918518`)
