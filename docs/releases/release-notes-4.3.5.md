# Glide 4.3.5

Window-management, taskbar and stability release. Built and tested on Windows x64 (417/417 tests passing).

## Fixes

### 1. Maximize stuck at the previous size after rotating the display

**Symptom:** after switching the display between portrait and landscape, or moving the taskbar, clicking Maximize sometimes left the window at roughly half size. Clicking again restored it instead of maximizing.

**Cause:** Glide uses a borderless window. For those, Avalonia overrides Windows' own maximize size with the monitor work area it has *cached*. It refreshes that cache only on `WM_DISPLAYCHANGE`, which usually arrives before Explorer has moved the taskbar, so the cache keeps a stale area. Because the window is still flagged as maximized, the next click sends Restore.

**Fix:** after every maximize, Glide compares the window with the live monitor work area and corrects any difference. On display, DPI and work-area changes, it refreshes Avalonia's monitor cache and re-checks at 0 ms, 300 ms and 1.2 s. Each correction is logged as `maximized_refit`. A title-bar double-click now uses the real Windows maximized state.

### 2. Taskbar icon missing

Avalonia rebuilds the window style on every maximize, restore and taskbar change. For borderless windows that strips `WS_SYSMENU` and `WS_THICKFRAME`, which Glide's browser-style frame depends on. The shell then no longer treats Glide as a normal app window. Glide now reapplies these styles through Avalonia's supported style hook on every rewrite, and attaches system-sized icons from `Glide.exe` to each window and its window class, including after Speed Boost restore. Snap Layouts also stay reliable.

### 3. Possible crash while browsing with the cache active

A cached compressed image could be freed (by cache trim, purge on minimize, or refresh) while a decoder was still reading it. That could crash the process or produce a garbled image. Buffers are now reference-counted for as long as a read is in progress; if an entry has already been evicted, Glide reads from disk instead.

### 4. UI errors no longer close the viewer

Unexpected exceptions in UI event handlers are logged as `dispatcher_unhandled_exception` and Glide keeps running. Out-of-memory and stack exhaustion still terminate the process.

## Versioning

Moved to 4.3.5 everywhere: assembly, installer, window title, About page and diagnostics. The earlier 4.3.4 build still showed 4.3.3 in its title and About page.
