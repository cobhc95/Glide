# Explorer thumbnails of 45 MP progressive JPEG folders, 5.2.0

Report: on the installed 5.2.0 a folder of 133 progressive JPEGs (5464 x 8192, 45 MP, 4:2:0, about 4.6 MB, no EXIF thumbnail) took 20 - 30 s
to show every thumbnail in the Explorer tab, and the tiles appeared a few at a time. Most of the user's folders hold such files.

Everything below was measured headless (no Glide window, no installed Glide touched) on the user's PC: Ryzen 7 9700X (8 cores / 16 threads),
61.6 GB, Windows 11 LTSC 26100, Release build, the thumbnail disk cache off. The user's folder was only read and only timings and JPEG marker
structure were recorded (no file name, no pixel). The synthetic folder is `tools/perf/generate_progressive_45mp.py` (133 files, 4.0 MB each,
the same scan script as the user's files).

## Root cause

1. **Tiles past the viewport were never requested until scrolled to.** The tab asked only for realised tiles (the viewport plus the repeater's
   buffer) and, with the disk cache on, about two rows of look-ahead. In the user's in-app trace (installed build, `--diagnostic-trace`, cache
   cleared) 61 tiles came in one burst in 72 ms and the remaining 72 were not asked for until the user scrolled: "20 - 30 s for the folder" was
   the user scrolling through it in small bursts.
2. **Each progressive thumbnail committed and zeroed 132 MB.** The DC-only route (libjpeg buffered-image mode) allocates the whole picture's
   coefficient arrays before reading the first scan (64 x 2 bytes per block: 45 MP at 4:2:0 = 132 MB). Twelve decode lanes committed 1.6 GB and
   spent most of their time faulting in and zeroing pages: 32 ms per file alone, 106 ms each with 12 in flight (3.3x slower with more lanes).
3. **The Windows-cache probe cost more than the decode**: 35 - 60 ms per file in the user's folder (in-app trace), ahead of every JPEG read.

What it was not (each checked):

| Hypothesis | Finding |
|---|---|
| From-memory decode bypasses the DC path | No. `GlideDecodeJpegThumbnailMemory` takes the same DC route: from path 33.8 ms, from memory 32.2 ms, DC-only 31.7 ms per file (1 thread); 0 of 133 files fell back. |
| The real files have an unusual scan script | No. All 133: `SOF2`, components 1:2x2 2:1x1 3:1x1, first scan interleaved DC of all three (Ss=0 Se=0 Ah=0 Al=1), then libjpeg's standard script (10 scans), JFIF APP0 only, no DRI, Huffman. Identical to the synthetic files. |
| Something serialises decodes (native lock, governor, read-ahead cap) | No: 12 decodes in flight in every run (`peakDecodeInFlight 12`), 15 - 16 reads in flight. |
| Disk throughput | No: 420 - 480 MB/s unbuffered from the pool drive at 1 - 16 threads (133 x 4.6 MB = 1.3 s of device time at worst). |

## Fixes

- **Own DC reader** (`native/Glide.Native/GlideJpegDc.cpp`, `GlideJpegDcOnly`): parses the markers, decodes the DC Huffman codes of the DC
  scan(s) into one 16-bit value per block (1.5 MB for 45 MP) and stops; handles restart markers, interleaved and one-component DC scans, scans
  before the last DC scan are skipped, successive approximation (Al); output is libjpeg's 1/8 picture (1x1 IDCT, no fancy upsampling,
  JFIF YCbCr) within 2 levels. Both progressive thumbnail routes (path and memory) use it first; libjpeg's DC route stays as the fallback.
  Exports for tests: `GlideJpegDcOnlyMemory`, `GlideJpegLastThumbnailRoute`.
- **Whole-folder preparation** ("Prepare thumbnails for the whole folder in the background", on): after the first tile the tab queues every
  picture of the folder at the lowest priority (at most 5000, 1500 with the disk cache off), cancelled on navigation; results go to a 256 MB
  in-memory LRU (`ThumbnailMemoryCache`, pixels, not bitmaps) and the disk cache. A tile realised while its file is queued joins that job
  (raised to the tile's rank) instead of decoding it again.
- **Windows-cache probe skipped for a JPEG read into memory** (Glide's decode is faster than the probe); other formats keep it.
- **"Thumbnails loaded at once"** (Settings > Performance > Memory and caches): Automatic (12 on this PC) or 4 - 32 lanes, live, every value
  lowered to a quarter of the free memory at 160 MB per lane (`ThumbnailLanesSetting`). I/O lanes are twice the decode lanes (4 - 32).

## Numbers

### Per-file progressive decode, 45 MP (`Glide.PerfBench thumb-progressive`, 24 files)

| Route | 1 thread | 12 at once | Memory, 12 at once |
|---|---:|---:|---:|
| libjpeg DC (before), synthetic | 31.6 ms | 106 ms | +1576 MB |
| own DC reader (after), synthetic | 10.5 ms | 18.9 ms | +56 MB |
| Explorer route from memory, real folder, before | 32.2 ms | 108 ms | +1575 MB |

### Time to all 133 thumbnails, decode pipeline (`tools/perf/thumb-folder-time.ps1`, disk cache off)

| Folder | Lanes | Before | After | First tile (after) | Decode median (after) | Peak memory (after) |
|---|---|---:|---:|---:|---:|---:|
| synthetic | Automatic (12) | 1537 ms | 255 ms | 39 ms | 19 ms | +313 MB |
| synthetic | 16 | | 242 ms | 194 ms | 21 ms | +337 MB |
| synthetic | 24 | | 247 ms | 187 ms | 21 ms | +329 MB |
| synthetic | 32 | | 241 ms | 174 ms | 21 ms | +334 MB |
| real (timings only) | Automatic (12) | 1655 ms (decode median 133 ms) | 286 ms | 40 ms | 21 ms | +650 MB |
| real | 16 | | 266 ms | 228 ms | 24 ms | +666 MB |
| real | 24 | | 270 ms | 211 ms | 24 ms | +670 MB |
| real | 32 | | 284 ms | 45 ms | 24 ms | +625 MB |

The peak memory after is mostly file bytes waiting for a lane (read-ahead budget 192 MB) and pooled read buffers, not decoding.
More than 12 lanes gains nothing here (the 16 threads are saturated) and delays the first tile; Automatic stays at 12.

### The real Explorer tab (headless MainWindow, `ExplorerLargeProgressiveTimingTests`, disk cache off, no scrolling)

| Folder | Lanes | First screen | All 133 ready | Last screen after scrolling to it | Process peak |
|---|---|---:|---:|---:|---:|
| synthetic, before | 12 | 802 ms | never without scrolling (2731 ms scrolling as fast as tiles came) | | |
| real, before | 12 | 503 ms | never without scrolling (2322 ms scrolling as fast as tiles came) | | |
| synthetic, after | Automatic (12) | 424 ms | 538 ms | 7 ms | 407 MB |
| real, after | Automatic (12) | 429 ms | 563 ms | 7 ms | 720 MB |
| synthetic, after | 16 / 24 / 32 | 534 / 527 / 514 ms | 535 / 527 / 520 ms | 7 ms | 436 - 454 MB |
| real, after | 16 / 24 / 32 | 544 / 594 / 490 ms | 560 / 594 / 570 ms | 7 ms | 765 - 773 MB |

### Viewer first view of a 45 MP progressive JPEG (`Glide.PerfBench decode-one`, cold process; unchanged by this work)

| File | First frame | Full picture | Settled | Peak |
|---|---:|---:|---:|---:|
| synthetic | 193 ms | 201 ms | 416 ms | 322 MB |
| real (3 files) | 210 - 245 ms | 226 - 249 ms | 453 - 512 ms | 322 MB |

The viewer's first frame is libjpeg-turbo's 1/4 scaled decode, which has to entropy-decode every scan. A DC-only first frame (683 x 1024, about
11 ms with the new reader) shown while that runs would cut the first frame to about 15 ms at the cost of a soft first picture: a follow-up.

## Reproduce

```
python tools/perf/generate_progressive_45mp.py %TEMP%\glide-p45
powershell -NoProfile -ExecutionPolicy Bypass -File tools\perf\thumb-folder-time.ps1 -Folder %TEMP%\glide-p45 -Lanes Automatic,16,24,32 -Real
```

Tests: `ProgressiveDcReaderTests` (own reader = libjpeg DC on 45 MP, 30 MP, restart-marker and grayscale fixtures; the 45 MP route from memory,
from the path and through `ThumbnailDecode`; declines baseline, CMYK and cut files; lane setting resolution and memory clamp; live lane change;
memory-cache hit and LRU; a visible tile joining the preparation job), `ThumbnailPipelineTests` (a JPEG read into memory skips the Windows probe).
Not covered by a fixture: a jpegtran-style script with separate per-component DC scans (the reader supports it; PIL cannot write one).
