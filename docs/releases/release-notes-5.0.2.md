# Glide Image Viewer 5.0.2: window and full-screen polish

A polish release on top of [5.0.1](https://github.com/cobhc95/Glide/releases/tag/v5.0.1). It fixes how Glide remembers its window size and makes full screen switch cleanly. Settings from 5.0.0 and 5.0.1 load unchanged.

## 🪟 What's fixed

**Window size and position are remembered correctly**
- Windows can mark a window as maximised before Glide's own state catches up. In that gap, the maximised rectangle could be saved as the window's "normal" size, so restoring went to the wrong size.
- Glide now ignores natively maximised windows when it tracks the normal size, and pins the normal size and position when the window leaves the normal state.
- Small window, then maximise, then un-maximise: you get the same small window back.
- Small window, then full screen, then exit: you get the same size and position back.
- Maximised, then full screen, then exit: you get maximised back, and un-maximising still gives your small window.

**Leaving full screen no longer looks like Glide reloading**
- Since 5.0.0 the window was made fully transparent while it left full screen, so the desktop showed through for several frames before the window came back.
- Maximised to full screen and back is now a single window-state change. It used to be three resizes, and the window is no longer hidden.
- Full screen to a normal window is hidden only from the resize until the first frame at the new size is drawn, usually one or two frames. This uses a Windows DWM cloak, so your window-transparency setting is never touched.
- Entering full screen from a normal window gets the same treatment, so the old picture no longer jumps to the top-left corner.
- The title bar, status bar and tab strip are now laid out once for the final size instead of on every step. The diagnostics trace records each switch with its timings.

## 🧭 What's new

**Launch log**
- Every launch that is forwarded to a running Glide now writes a `launch/open_decision` entry to the diagnostics log. It says why Glide opened a new window, a new tab, replaced the Home tab, overwrote a tab or refreshed an image that was already open.
- It records the open-behaviour settings in effect and the file extension (for example `.gif`). It never records the file path.

## 🔧 Under the hood

- About 150 silent `catch` blocks were reviewed. Eight now write to the diagnostics log (launch-policy cache, legacy placement cleanup, diagnostics export, thumbnail cache clear, Explorer open and reveal). The rest carry a one-line reason for swallowing the error. No behaviour change.
- Two source-text tests became behavioural tests, and the 30 that remain are labelled with why.
- Tests: Core 1552, Input 21, Explorer 125.

## 📦 Downloads

| File | What it is |
|---|---|
| `Glide.Setup.exe` | Installer (administrator rights; installs to Program Files, adds Open With and Explorer thumbnails) |
| `Glide-5.0.2-Portable.zip` | Portable: extract anywhere, run `Glide.exe` |

Install over 5.0.1, 5.0.0 or any 4.x: settings carry over. Windows 10 1607+ (x64). No telemetry; the only network use is the daily update check (off in Settings) and the optional video-decoder download.

For the speed tables and everything in the 5.0 line, see the [5.0.0 release notes](https://github.com/cobhc95/Glide/releases/tag/v5.0.0). Full list of changes: [CHANGELOG](https://github.com/cobhc95/Glide/blob/main/CHANGELOG.md).
