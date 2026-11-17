# JPEG first-frame speed plan (5.0.7), approved by the user

Goal: open JPEG/JPG and show the first frame as fast as possible; full-size render fast too.
Hard limit: setup (dist-installer\Glide Setup.exe) must stay under 22 MB, ideally under 20 MB (user, updated); prefer options that keep it under 20. Current: 18.3 MB.
Idle checks: a few seconds at most, then run and flag busy samples (never wait for full idle).

## Process (user, 2026-11-17)
1. Full baseline remeasurement first (current Glide, all JPEG kinds and sizes, synthetic + real incl. the new jpg-camera, jpg-phone, jpg-web folders).
2. Then iterate: try each method and hybrid variant, measure, refine, repeat until the best configuration is clearly established.
3. Final serial before/after comparison of the chosen configuration vs the baseline.

## Rules
- Everything compared against a BEFORE baseline captured first, through Glide's real open path.
- Metric: input (key press / open) to first pixel on screen, plus decode-only, full-size time, peak memory.
- Strictly serial runs, idle checks, warm-up + repetitions, median/p95. Only one measurement at a time; no builds/tests overlapping.
- Experiment: compare several hybrid variants and each trick on/off (ablation) and in combination; keep only measured wins.
- Corpus: real JPEGs (<bench-corpus>, RAW-embedded JPEGs) + synthetic detail-heavy JPEGs 12/24/50/100 MP:
  baseline, progressive, EXIF-rotated (2-8), with and without EXIF thumbnail, restart markers, CMYK/grayscale, 4:4:4/4:2:0.

## Candidates to measure
Hybrid variants (at least):
- H0 current (WIC for first view + full).
- H1 turbo DCT-scaled first view for baseline, WIC first view for progressive, turbo full-size for all.
- H2 turbo for everything.
- H3 WIC first view everywhere, turbo full-size only.
- H4 variants of H1 with different scale-factor choice / thread counts.
Tricks (each on/off):
- Embedded EXIF thumbnail shown instantly as placeholder, swapped for the real first view.
- Smallest DCT scale that still fills the viewport.
- Decode straight to display format (BGRA premultiplied), no extra copies/conversions.
- Single large read / memory-mapped file.
- Reuse WIC factory/decoder setup.
- Neighbour pre-read + first-view pre-decode (next/previous).
- Multi-core full-size decode (restart markers / banded).
Fallback to WIC on any turbo failure. Progressive detection via SOF marker.

## Correctness
Pixel-identical (or within 1 LSB) vs current for orientation, CMYK, grayscale, odd sizes; tests for each.

## Added scope (user, 2026-11-17): speed-built codec pack
Setup size limit for the combined result is now 22 MB (user).
- Rebuild glidecodecs.dll for speed (/O2, SIMD where available) and lift the 8-thread cap (use all cores sensibly).
- Measure before/after on JPEG XL, AVIF, HEIC, JPEG 2000 (round 2 showed official libjxl/avifdec 1.5–3x faster than Glide's /O1 build).
- Optionally prototype AVIF via libavif directly if it fits; keep only measured wins.
- Report the setup size after each step.

## Excluded
LibRaw (on hold), libvips. PNG/WebP improvements are later separate steps.

## Deliverable
Section in docs/perf/benchmark-report-5.0.7.md: before/after tables per variant and trick, chosen configuration, setup size before/after.
