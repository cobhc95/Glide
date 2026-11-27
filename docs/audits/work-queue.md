# Work queue (user-approved 2026-11-18), run sequentially

1. Audit Stage A — quick wins (running).
2. Audit Stage B — tooling and test seams.
3. Audit Stage C — settings single source.
4. Audit Stage D — split the god classes (one feature per step, benchmark first-frame/navigation hot paths before/after).
5. Investigate silent exit on launch (installed Glide started, saved settings, exited with no window; twice: right after 5.0.7 install, and after using Overlay mode then quitting). Reproduce with a project build (overlay mode → quit → relaunch, trace on), fix with a regression test.
6. Vertigo220's next round of suggestions (list to be supplied by the user):
   - Bugs: fix all automatically; each must be reproduced before and verified after the fix.
   - Improvements to current behaviour: take on board.
   - Complete changes of how the app behaves: consult the user first with visuals sent as PNG pictures (user is on the phone).

Rules: no publishing without the user's confirmation; never touch the installed Glide; delegate to background agents; brief idle checks only for benchmarks.
