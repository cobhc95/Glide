# Glide Image Viewer: .NET 10, safer file operations and update alerts

🚀 **A polish release on top of 5.0.0:** Glide now runs on **.NET 10 LTS**, file operations are safer, and a **daily update check** tells you when a new version is out (it never installs anything).

## 🆕 What's new in 5.0.1

**🧱 .NET 10 LTS** · .NET 8 support ended 2026-11-10
- 📦 `Glide.exe` **−9.7 %** (40.5 → 36.6 MB) · ⚡ opening, decoding, browsing and memory unchanged · 🛡️ high-severity compiler warning (NU1903) gone · still one self-contained exe, no runtime needed
- 🔒 CET shadow-stack protection stays off, as in 5.0.0 (third-party shell extensions, codecs and the video pack load into Glide)

**🗂️ Safer file operations** · fixes from an external code audit
- 🚚 Moving a folder to another drive **no longer deletes files that arrive or change mid-move**: only verified, unchanged originals are removed, folders only once empty, the rest stays with a message · junctions never followed
- 🖼️ A multipage TIFF (or animated GIF, APNG, WebP, multi-size icon) **can't be overwritten with just its first page**: *Save* refuses, *Save as* warns
- 🧊 Rename and Recycle **no longer freeze the viewer** on slow disks or shares · one rename rule for viewer and Explorer, case-only renames included
- 🎬 The optional video decoder is **re-verified against pinned SHA-256 hashes before every load**; the forgeable "verified" stamp file is gone

**🔔 Daily update check**
- One small HTTPS request to GitHub once a day: no personal data, no account · a prompt offers **Not now** / **View on GitHub** (it only opens the release page) · *Don't remind me again*
- New **Updates** card in Settings > General & startup · *Check now* · *Check for updates now* hotkey action · **off with one switch**

🧾 Also: 271 searchable settings in 40 cards · 5.0.0 settings load unchanged · installer requires Windows 10 1607+ · tests: Core 1539, Input 21, Explorer 125.

## ⭐ The big highlight is still 5.0.0

Large images open and settle **up to 89 % faster** with **half the memory**, plus a built-in **Explorer tab** with Back / Forward, one panel for every **mouse gesture**, **132 settings on a key**, collapsible Settings cards, **Reset Glide**, instant print preview and a full security review.

👉 **[Read the full 5.0.0 release notes](https://github.com/cobhc95/Glide/releases/tag/v5.0.0)** · speed tables, v4 vs v5 comparisons and every fix.

## 📦 Downloads

| File | What it is |
|---|---|
| `Glide.Setup.exe` | Installer (administrator rights; installs to Program Files, adds Open With and Explorer thumbnails) |
| `Glide-5.0.1-Portable.zip` | Portable: extract anywhere, run `Glide.exe` |

- 🔼 Install over 5.0.0 or any 4.x: settings carry over, your own key bindings and title bar are kept · 💻 Windows 10 1607+ (x64) · 🔐 no telemetry; the only network use is the daily update check (off in Settings) and the optional video-decoder download

🙏 Thanks to **vertigo220** for the many detailed bug reports and feature ideas behind the 5.0 line ([what was reported and where it shipped](https://github.com/cobhc95/Glide/blob/main/docs/releases/5.0.0-community-feedback.md)).

📜 [Full changelog](https://github.com/cobhc95/Glide/blob/main/CHANGELOG.md) · 🔮 Next: stabilisation (brittle tests, empty `catch` blocks, `MainWindow` decomposition, remaining UI-thread file work).
