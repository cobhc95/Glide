# Explorer thumbnails on a slow first open, 5.2.0: I/O stage, Windows cache, per-tile trace

Report: on the installed 5.2.0, in a folder of 127 JPEGs (3744 x 5616, about 2.3 MB, no EXIF thumbnail) on a StableBit DrivePool disk
(CoveFsDisk over SATA SSDs, Malwarebytes and Defender installed), the Explorer tab's thumbnails "load slowly and appear one at a time, in
name order". A plain `ReadAllBytes` of uncached files in that folder took about 2 s per file (1.1 MB/s) while Glide had it open and about
10 ms per file minutes later.

Everything below was measured headless with `tools/perf/Glide.PerfBench` (`thumb-pipeline`, `thumb-read-scaling`, `thumb-wincache`); no Glide
window was started, no installed Glide was touched, the thumbnail disk cache was off, and the user's folder was only read (see "The real folder").
Machine: Ryzen 7 9700X (8 cores / 16 threads), 61.6 GB, Windows 11 LTSC 26100, .NET 10, Release build, 12 decode lanes (the governor's value for this PC).

## Is the work serialised?

**No, not by Glide's own code, and that was proved at each place a serialisation could hide.**

| Candidate | Finding |
|---|---|
| Lane bug / governor setting lanes to 1 | `ImagePerformanceGovernor.ThumbnailLanesFor` never returns below 2 (12 here). Headless, with the released scheduler (`legacy` mode) and the real decoder on 120 synthetic 3744 x 5616 JPEGs: **12 decodes in flight** (`peakDecodeInFlight 12`), 290 ms for all tiles. |
| Awaiting each tile in the realisation loop | `PopulateExplorerTile` starts `LoadBrowserThumbnailAsync` fire-and-forget; the scheduler's `RunAsync` returns a task. The real tab (headless `MainWindow`, real realisation) with a 250 ms open: at least 8 reads in flight at once, the first 12 tiles all shown in under 2.8 s where serial opens would need over 3 s (`ExplorerThumbnailConcurrencyTests`). |
| UI-thread file opens | None: tile building does no file access (sizes and times come from the listing); the picture is applied through one `Dispatcher.UIThread.InvokeAsync` per tile after the lane is released. |
| Native decoder global lock | `Glide.Native` takes no lock on the JPEG, WebP, TIFF or CMYK paths (a `std::mutex` is used only by the video player); libjpeg-turbo handles are per call. 12 concurrent decodes ran at the single-thread speed (25 ms each). |
| Disk-cache write lock | **Latent hazard, fixed** (below). Not the cause here: the user's cache holds 39 files, scanning it takes 9 ms. |
| Single-threaded `FileStream` | The JPEG path reads with one `ReadFile` into a malloc buffer; the Skia path reads sequentially. Neither serialises across files. |
| Cheapest-first ordering | Within one cost class the order is arrival order, so 127 same-size JPEGs are decoded in the order they were asked for. That is the "name order" the user sees: it is the grid's reading order, not a one-lane queue. |

What the released code **does** do wrong when a first open is slow (a virtual disk behind an on-access scanner is exactly that), shown with a
device model in the benchmark (`--latency-ms 1500 --device-parallel 2`: every open takes 1.5 s and the "disk" serves two at a time, which is
what a scanner that works on a couple of files at once looks like):

1. **The 8 s decode timeout covered the file open.** A job waiting in the 12 lanes for the device waits longer than 8 s, times out, keeps the
   file-type glyph, is remembered as failed, and its lane is counted "abandoned" until the open returns. With 12 lanes and a slow device the
   lanes drain away (`EffectiveLanes` falls toward 1): the tab ends up opening files one at a time with most tiles empty.
   Released scheduler, 120 files: **10 tiles shown, 110 timed out**, 89.6 s. (The same device with the new pipeline: all 120 tiles, no timeout; see the table.)
2. **Opens were limited to the number of decode lanes (12)** and were made by the decode lanes themselves, so a slow open also stopped a CPU
   lane from decoding. Nothing overlapped a slow open with the decode of another tile.
3. **No on-screen ordering.** The repeater also realises a buffer of tiles above and below the viewport; they all queued as "visible", ordered
   by when they were realised, so off-screen files were read ahead of nothing and could delay a visible tile.
4. **The same file could be read twice.** A tile realised again (a layout change, a refresh) cancelled its first request, but a read that has started
   cannot be stopped; the new request read the file again. In the headless Explorer tab every one of the first 12 files was opened twice
   (`opened: 000..010, 001..011, ...`). On a disk where an open costs seconds, that doubles the wait.
5. **The disk cache's first scan ran under a lock on the first writer's thread** (`EnsureScanned`): every other thumbnail write of that folder
   queued behind it, each holding a thread-pool thread, which can starve the decode lanes of threads (the pool starts at 16). Only matters with a large
   cache on a slow disk, so it is a hazard rather than this report's cause.

What could not be reproduced: **the 2 s reads themselves.** Measured now (see "The real folder") the folder reads at 460 - 520 MB/s from the device
(unbuffered, 1 to 12 threads) and 0.2 ms per cached open; the slow period was transient. Whatever slowed `ReadAllBytes` to 1.1 MB/s (on-access scanning
of files the scanner had not seen, the drive pool, contention) is outside Glide, so the fix is to make Glide tolerate a slow first open (items 1 - 4) and
to avoid the read entirely when Windows already has the thumbnail.

## What changed

- **I/O stage in front of the decode lanes** (`ThumbnailScheduler`, `ThumbnailPipeline`). 2 x the decode lanes (4 to 16; 16 here) dedicated
  threads open and read files; the decode lanes only decode. JPEG (native `GlideDecodeJpegThumbnailMemory`, a new export of
  `Glide.Native.dll`) and PNG / GIF / ICO (Skia on a memory stream) are decoded from the bytes the I/O lane read, so each file is **opened exactly once**
  (a scanner that rescans on every open is not paid twice). Formats that read only part of a file (RAW previews, pyramid TIFF, PSD, ...), WebP / TIFF / BMP
  / WIC / codec plug-ins and files above 48 MB (32 MB for PNG) keep their own path-based decode.
- **Slow reads never cost a decode lane or a timeout.** The 8 s timeout starts when the decode starts; a read has its own 45 s watchdog and its own lane
  pool. Read-ahead is bounded (192 MB of waiting bytes).
- **Ordering by on-screen position.** I/O jobs are ordered visible, then near (realised just off screen, nearest first), then look-ahead; inside a class
  by the tile's reading position (`ThumbnailRequest.Order`: its index for visible tiles). The decode queue keeps cost-first inside the class and uses the same position as tie-break.
- **Windows thumbnail cache first.** The I/O stage asks, in order: the Glide disk cache, then `IShellItemImageFactory::GetImage` with
  `SIIGBF_THUMBNAILONLY | SIIGBF_INCACHEONLY | SIIGBF_BIGGERSIZEOK` (the existing `GlideDecodeShellCachedPreviewW`), then the file. A tile File Explorer
  already made appears without reading or decoding the file. Set `GLIDE_THUMB_WINCACHE=0` to skip it. The shell wants backslashes: a path with `/` is a miss.
- **Duplicate reads removed.** An identical request (same file version and tile width) waits for the read under way and takes its bytes; a request cancelled
  while its file is being read hands the bytes to the identical request that replaced it.
- **Disk cache scan no longer blocks writers**: it runs on its own pool item, writers add their size changes to a delta that the scan folds in.
- **Per-tile trace** (below).

## Numbers, synthetic (120 JPEGs of 3744 x 5616, 2.17 MB, no EXIF thumbnail, 12 decode lanes)

`tools/perf/Glide.PerfBench` `thumb-pipeline --mode legacy|pipeline --latency-ms L [--device-parallel K] [--timeout-s 8]`. "legacy" is the 5.2.0 scheduler as released
(decode lanes open the files); "pipeline" is this change. Latency is injected on every file open (`Thread.Sleep` before the open; the model of a slow first open).
Time to all 120 tiles; first = first tile.

| Case | legacy (released) | pipeline | Notes |
|---|---:|---:|---|
| No latency | 295 ms | 298 ms | decode-bound, 12 lanes: 25 ms each; same |
| 500 ms per open | 5386 ms (first 545) | 4137 ms (first 548) | 120 / 12 lanes x 0.5 s = 5 s was already the released result (no serialisation); 16 I/O lanes: 7.5 rounds |
| 500 ms per open, 32 I/O lanes | n/a | 2135 ms | latency-bound work scales with I/O lanes |
| 500 ms per open, Windows cache warmed | n/a | **156 ms** (peak decode 0, no file opened) | warmed by one shell call per file; hit probe about 7 ms |
| No latency, Windows cache warmed | n/a | 241 ms | decode 25 ms replaced by a probe of 7 - 14 ms |
| 1500 ms per open, device serves 2 at a time, 8 s decode timeout | 89641 ms, **10 of 120 tiles shown, 110 timed out** | 90660 ms, **all 120 tiles shown, 0 timed out** (first 1546 ms, a tile every 0.75 s) | device-bound (120 x 1.5 / 2 = 90 s either way); the released code gives up on most files |

Windows' cache (`thumb-wincache`): a cold probe misses in 2.4 - 5.8 ms; one shell call to extract and store the thumbnail costs 32 ms (87 ms the first time);
the probe afterwards hits in 7.2 ms and returns a 156 x 236 picture (box 236 x 236).

## The real folder (127 JPEGs, DrivePool disk), read only

Timings only: no file name, no pixel was printed, kept or copied; nothing was written to the folder; the thumbnail disk cache was off; the Windows cache was only probed.
The folder was fast while it was measured (the PC was otherwise idle), so the "before / after" difference is small there; the numbers prove what the machine does when it is not slow.

| Run | Mode | All 127 tiles | First tile | Open (median / p95) | Read (median / p95) | Decode (median) | Windows cache hits |
|---|---|---:|---:|---:|---:|---:|---:|
| 1, cold-ish (first touch of the session) | pipeline | 695 ms | 93 ms | 26 / 66 ms | 29 / 57 ms | 38.5 ms | 15 of 127 |
| 1 | released scheduler, straight after | 591 ms | 40 ms | n/a (inside decode) | n/a | 52 ms | n/a |
| 2, warm | pipeline | 591 ms | 87 ms | 0.2 / 0.4 ms | 1.1 / 2.6 ms | 47 ms | 15 of 127 |
| 2 | pipeline, Windows cache off | 562 ms | 67 ms | 0.3 / 0.5 ms | 1.6 / 43 ms | 48 ms | 0 |
| 2 | released scheduler | 607 ms | 46 ms | n/a | n/a | 55 ms | n/a |

Device reads (`thumb-read-scaling --unbuffered 1`: `FILE_FLAG_NO_BUFFERING`, so the device and its filter drivers are measured, not memory; 40 files per row, a different slice each time):
1 thread 459 MB/s (open 0.2 ms, read 4.9 ms per 2.3 MB), 4 threads 518 MB/s, 12 threads 524 MB/s (read 41 ms each, 12 in flight): the SATA link is the limit, concurrency does not hurt the device.
Buffered reads (OS cache) 7 - 31 GB/s at 1 to 16 threads, open 0.1 - 0.2 ms.
In run 1 the opens took 26 ms median with 16 in flight against 0.2 ms in run 2: whatever sits in front of the file (the scanner, the pool driver) charged more for the first
touch. 127 files at 460 MB/s is 0.63 s of device time, which is about what the whole folder took: with the machine quiet the tab is device-bound, not Glide-bound.
Only 15 of 127 files had a thumbnail in Windows' cache (File Explorer had been through part of the folder).

## Reading the trace on a slow PC

Close Glide, then start it with the diagnostic trace on and open the slow folder in an Explorer tab:

```
Glide.exe --diagnostic-trace
```

(or set `GLIDE_DIAGNOSTIC_TRACE=1` for the process; a Glide that is already running ignores the switch of a second launch). The trace goes to
`%USERPROFILE%\Downloads\Glide-Diagnostic-Latest.txt` (2 MB cap; `GLIDE_DIAGNOSTIC_TRACE_DIR` redirects it). It stays off otherwise: nothing is built or written.
File names are written as a 6-digit hash of the lower-cased name, never as text, so the file can be shared.

One line per tile, `thumbnail.tile`:

```
... | T22 | thumbnail.tile | id=17 file=a1b2c3 ext=.jpg kb=2310 prio=Visible order=16 plan=Bytes outcome=decoded source=Decoded io_wait_ms=412.6 cache_ms=0.1 win_cache_ms=6.8
      open_ms=1873.2 read_ms=41.0 io_ms=1925.9 decode_wait_ms=2.2 decode_ms=27.4 store_ms=0.8 total_ms=2368.0 io_lane=7 decode_lane=3 io_in_flight=16 decode_in_flight=5
```

| Field | Meaning |
|---|---|
| `prio`, `order` | Visible / Near / Prefetch, and the position that orders it (index for visible tiles) |
| `plan` | `Bytes` (the I/O lane reads the file and the decode lane decodes the bytes) or `None` (the decode lane opens it) |
| `outcome` / `source` | `decoded`, `disk-cache`, `windows-cache`, `failed`, `timeout` (decode over 8 s), `io-timeout` (read over 45 s) |
| `io_wait_ms` | Time waiting for an I/O lane. Large with `io_in_flight` at its maximum: reads are the bottleneck |
| `cache_ms`, `win_cache_ms` | Disk-cache stamp and look-up; Windows cache probe |
| `open_ms` | Opening the file. **Large: the scanner / virtual disk charges the open** (the user's 2 s) |
| `read_ms` | Reading it. Large with a small `open_ms`: device throughput or contention |
| `decode_wait_ms`, `decode_ms`, `store_ms` | Waiting for a decode lane (CPU-bound if large), the decode, the disk-cache write hand-off |
| `io_lane`, `decode_lane`, `io_in_flight`, `decode_in_flight` | Lane used and how many files were in flight when it started |

`thumbnail.applied | id=17 ui_apply_ms=3.1` follows when the picture reaches the tile (large: the UI thread was busy). `thumbnail.burst` is the summary when the
queue drains: `tiles`, `total_ms`, `first_tile_ms`, `peak_io`, `peak_decode`, `io_lanes`, `decode_lanes`, `timed_out`.

How to read it: `open_ms` in the seconds and `io_in_flight` at 16 means the disk side is slow and Glide is overlapping as much as it can; `io_wait_ms` growing in name order with
small `open_ms` means the lanes are busy; `decode_wait_ms` large means CPU; `ui_apply_ms` large means the UI thread.

## Follow-ups not done here

- The per-tile resolution / date lines under thumbnails (off by default) and sorting by dimensions or date taken open each file again for their header; folding them into the I/O
  stage's bytes would make "each file opened once" true for those settings too.
- An adaptive I/O lane count (grow while throughput rises, back off when per-open time grows with concurrency) if the trace shows a device that serialises opens.
