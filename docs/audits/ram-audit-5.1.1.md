# RAM audit (5.1.1, read-only)

Scope: lower RAM use without losing navigation speed and without making frames undersized (blurry).
Figures are rough estimates. Nothing here was measured; measure before you act on any row.
Reference folders: a 30 MP JPEG (6720x4480, BGRA = ~120 MB per full frame) and a 4K wallpaper JPEG
(3840x2160 = ~33 MB per full frame). Fit box at 175% when maximized: about 4480x2300 physical.

Current holders (from code):
- Decoded prepared cache (`ImageLoadCoordinator._prepared`), budget `DecodedCacheMegabytes` (default 256, Maximum quality 999).
  It holds the full frames of visited images and the fit-box neighbour frames (`FullNeighbourPredecodeCount = 0`;
  since b2c2901 a neighbour whose scaled decode comes back below the fit box is decoded at full size, up to half the budget).
- Compressed cache (`CompressedCacheMegabytes`, 384-999 MB): file bytes.
- Warm tab cache: 5 image tabs and 3 explorer tabs (`MainWindow.TabWarmCache.cs`), each frame up to about 64 MB.
- Explorer thumbnail caches, tab folder previews (4 x 96 px), hover previews.
- Retired/leased bitmaps that wait for the compositor fence.
- GC: the App sets no GC settings (workstation, concurrent by default). Only ThumbnailCodecs pins non-concurrent.

| # | Idea | 30 MP folder saving | 4K folder saving | Speed risk | Blur risk | Effort |
|---|------|--------------------|------------------|-----------|-----------|--------|
| 1 | Downscale a full frame to the fit box (or to 2x fit) once you leave the image; keep full only for the current image and while zoomed | 60-100 MB per visited image kept (120 MB down to ~25-40 MB) | small (33 MB down to ~25 MB; the frame is already close to fit) | low: going back shows a fit-sized frame at once, full only on zoom | none at Fit if the frame is at least the physical fit size (guard + `DisplayFrameSufficiency` already checks); zoom needs the existing manual-zoom refine | M |
| 2 | Cap the prepared-cache budget by real need: about (2 x depth + 2) x fit-frame bytes, not a flat 256/999 MB | 100-600 MB on Maximum quality | 100-500 MB | low if the cap still covers the prefetch window | none (frame sizes are unchanged) | S |
| 3 | Shrink the warm tab cache to fit-sized frames (no full frames), or 3 tabs instead of 5 | up to ~250 MB (5 x 64 MB down to 5 x ~15 MB) | ~100 MB | tab switch shows a fit frame at once; full decode only on zoom | low: the `warm_tab` guard re-check covers it | S |
| 4 | Compressed cache: lower the default to about 128-192 MB, or limit it to the prefetch window | 200-800 MB (Maximum quality) | 100-300 MB | small re-read cost on SSD; check HDD/network folders | none | S |
| 5 | Trim when idle or minimised: on minimise, or after 30-60 s with no input, drop the neighbour and warm caches, then `GC.Collect(LOH compact)` and `SetProcessWorkingSetSize(-1,-1)`. `PurgeCacheOnMinimize` exists; make a softer default that keeps the current frame | 300-1000 MB while in the background | 150-400 MB | first step after restore pays a re-decode of the neighbours | none: the current frame is kept | S |
| 6 | GC: `DOTNET_GCConserveMemory=5..7` plus `<ConcurrentGarbageCollection>` left on; consider `GCHeapHardLimit` off. Bitmaps are mostly native (Skia), so the managed gain is LOH fragmentation from byte[] buffers (compressed cache, preload bytes) | 20-150 MB | 20-80 MB | slight GC CPU rise; measure p95 navigation | none | XS (runtimeconfig) |
| 7 | Pool or rent compressed and preload `byte[]` buffers (ArrayPool or native memory) to stop LOH fragmentation | 50-200 MB peak | 30-100 MB | none to slight gain | none | M |
| 8 | Check for duplicate copies: the native decode buffer is freed after the copy into the Skia bitmap (`TryCreateBitmapFromNative` frees it). Check that the GPU texture upload does not keep a second CPU copy in Skia's resource cache; cap `SKGraphics.SetResourceCacheTotalByteLimit` | 0-120 MB (one frame) | 0-33 MB | low | none | S (to investigate) |
| 9 | Store neighbour JPEG frames as YUV 4:2:0 (1.5 B/px instead of 4) and convert to BGRA on present | ~60% of prefetch bytes (about 150 MB at depth 3) | ~60% (about 60 MB) | conversion costs ~5-15 ms on present: only worth it if done on the GPU | none | L (renderer change) |
| 10 | Explorer thumbnail and hover caches: one shared LRU with a byte budget (for example 64 MB) | 20-100 MB in large folders | same | none | none (thumbnails) | S |
| 11 | Release retired bitmaps sooner: check the fence path so cancelled presentations (for example the old refinement-only `Cancelled` path fixed in b2c2901) do not strand leased frames | one frame per occurrence (up to 120 MB) | up to 33 MB | none | none | XS-S (audit plus a test) |

## Top 5 (best gain for the risk)
1. **#1 Downscale visited full frames to the fit size**: the biggest win on large photos, with no blur at Fit.
2. **#5 Trim when idle or minimised** while keeping the current frame: big background saving, zero blur risk.
3. **#2 Size the decoded budget from the fit-frame size and the prefetch depth** instead of a flat 256/999 MB.
4. **#3 Fit-sized warm tab frames**: up to ~250 MB, covered by the existing `warm_tab` guard.
5. **#6 + #7 GC conserve-memory plus pooled byte buffers**: cheap fixes for LOH fragmentation.

Guard rule for every change: a frame you keep for presentation must stay at least the physical fit box
(`DisplayFrameSufficiency.FitNeeded`, 0.97 tolerance). Never keep a frame that is merely "half of needed".
