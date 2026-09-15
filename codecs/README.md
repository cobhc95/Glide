# Optional codec deployment

Glide 2.2 keeps optional codec breadth completely off the ordinary cold-launch path.

Each provider consists of a native DLL and a sibling `*.glidecodec.json` manifest. `tools/generate-codec-index.ps1` verifies each DLL SHA-256 at build/package time and writes `codecs.index.json`. At runtime Glide reads only that tiny index when an exotic file first requires an optional provider; the selected DLL alone is then hashed, ABI-checked and loaded.

New providers should implement ABI v2 from `../native/Glide.CodecProvider.ABI.h` and return premultiplied BGRA8 decoded surfaces. ABI v1 remains accepted only for migration. A format is not release-certified merely because it appears in the 196-suffix registry: it needs a real fixture, malformed-file isolation, correct dimensions/orientation/alpha and cold/warm/navigation measurements as specified in `../docs/CODEC_PERFORMANCE_MASTER_PLAN.md`.

Do not add eager static initializers, process-wide codec scans, helper processes at normal startup, or viewport/input changes as part of codec work.
