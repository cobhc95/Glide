# Combined audit (code hygiene, coupling, AI maintainability), 2026-11-18

Sources: audit-code-hygiene.md (38 findings), audit-coupling.md (16 findings, 6-stage roadmap), audit-ai-maintainability.md (14 recommendations). All read-only; nothing changed.

## Big picture
- MainWindow: 32 partial files, ~16.5k lines, ~267 fields; settings read directly 565 times; one setting change re-applies ~35 subsystems.
- SettingsWindow: 13–14 partials, ~6.3k lines; every setting mapped by hand 3–4 times (Load/Read/ControlMap/accessors) + ~8 parallel tables.
- Feedback loop is slow: no compile-only check, no per-area tests, ~2000 warnings drown errors, build log copied to the clipboard on failure.
- Tests are brittle: 52–124 source-text assertions, 61–63 reflection calls on private members, hard-coded counts (306 settings, 196 extensions…), 131 Task.Delay waits, Explorer tests (~125–195) outside Glide.sln so build.cmd never runs them.
- Searches are noisy: no git, no .ignore; Archive/ (62 MB of old copies) and .artifacts (960 MB) get grepped.

## Stage A — quick wins (low risk, ~1 session, no behaviour change)
1. Root .ignore (Archive/, .artifacts/, build dirs) + make the folder a git working copy (R1).
2. Add tests/Glide.Explorer.Tests to Glide.sln so every build runs them (H1).
3. tools/quick-check.ps1: Debug compile of Glide.App errors-only + both audits + filtered tests, < 60 s (R2).
4. ARCHITECTURE.md subsystem → files/tests/docs map; AGENTS.md trimmed to ~25 lines with the full "add a setting" checklist (R4, R5).
5. Replace hard-coded counts with catalogue-wide invariants (R6).
6. build.cmd: stop copying the log to the clipboard; keep the last log (R14).
7. Remove verified dead code and stray files (__pycache__, TestResults, root zips, no-op props); shared TempDir test helper; try/catch on the async void handler (hygiene top 3, 8, 10).
8. Move stale history docs (GLIDE_MANIFESTO_AND_HANDOFF etc.) to docs/history (R12).

## Stage B — tooling and test seams (low–medium risk)
- Warning triage via .globalconfig: suppress noise (CA1707 test names, CA1861, CA2007), fix real culture/string-comparison sites; nullable + AOT warnings as errors (R7, W0 SARIF baseline first).
- [Trait("Area", …)] on tests; split App-dependent tests into Glide.App.Tests (R3, C12).
- Replace source-text assertions and private-member reflection with behaviour tests / InternalsVisibleTo (R8) — prerequisite for any file split.
- Replace sleep-then-assert waits with signals.
- tools/release.ps1 scripting the publish steps (R13).
- Coupling stage 1: remove global sort order static, explicit catalogue build order (fix Core namespace cycles), stop commands calling click handlers, merge window registries.

## Stage C — settings single source (medium risk)
- One SettingsService owning live settings; per-feature read-only slices; route changes only to the owning subsystem (C2, C3).
- Generate state/accessors/Load/Read/control-map/effect tables from one catalogue (R9, C4); defaults only in SettingsDefaults.
- Later: stable keys for Choice values instead of English labels, with migration (R10).

## Stage D — split the god classes (medium risk, one feature per release, benchmark hot paths)
- Extract from MainWindow, lowest risk first: status bar (46 manual refreshes) → gesture resolver (implemented ~10 times) → tab strip → Explorer controller → history → navigation (C1, C5, C9, C13).
- Split SettingsWindow into per-page views (R11).
- Imaging without Avalonia: decoded-image type instead of Bitmap; Glide.Codecs.Managed project replacing 18 linked files (C8).
- Interop: consolidate duplicate Win32 declarations, move to LibraryImport, set DefaultDllImportSearchPaths.

## Estimated payoff
Typical "add a setting / fix a UI bug" agent task: ~150–300k tokens → ~50–100k; 30–60 min → 10–20 min (mostly from Stage A + B).
