# Audit: maintainability for AI coding agents

Date: 2026-11-18 · Scope: whole repository (read-only; nothing was built or run) · Version on disk: 5.0.8

Question: what makes Glide expensive (tokens, wall-clock time, mistakes) for an AI agent to read, debug and
extend, and what would make it much cheaper. Token estimates use ~4 bytes per token for C#/XAML.

## Summary

The code is well disciplined in places that matter to agents (one action registry, one settings catalogue,
guard tests and two audit scripts that catch omissions). The cost comes from five things:

1. **Two god classes.** `MainWindow` is 33 partial files, 896 KB (~225k tokens). `SettingsWindow` is 14 files,
   478 KB (~120k tokens), and its core `SettingsWindow.axaml.cs` (182 KB, 2,997 lines) plus
   `SettingsWindow.axaml` (110 KB) must be opened for almost every settings change.
2. **A setting is spread over 9 to 11 files, and the documented recipe lists only 3 of them.** Agents learn the
   rest by failing builds or tests, and each cycle costs 3 to 10 minutes.
3. **No fast feedback loop.** Even `build.cmd fast` configures and builds the C++ code, builds Release and runs a
   NativeAOT publish. No path does "compile only" or "run only the tests for this area". Tests have no
   traits, and 68 of the 162 Core.Tests files run in one serialized Avalonia collection.
4. **Search noise and no version control in the working folder.** There is no `.git`, so ripgrep (and the
   agents' Grep tool) does not apply `.gitignore`. Searches walk `Archive/` (484 stale .cs/.md/.axaml copies),
   `.artifacts/` (960 MB), CMake build trees and a 170 KB CHANGELOG. Agents also cannot `git diff` their own
   work or revert it.
5. **Brittle tests that block refactors.** 33 test files (52 call sites, many of the 734
   `Assert.Contains("...")` assertions) grep production source text. Hard-coded catalogue counts (306
   settings, 41 cards, 196 extensions, 54 slots) break on every legitimate addition.

Fixing 2 to 5 is mostly small or medium work. It should cut a typical "add a setting" task from ~150k to 250k
tokens and 3 to 5 build cycles to ~50k tokens and one quick-check. A typical bug hunt in `MainWindow` would
also fall from "read 4 to 8 partials" to "read the subsystem map, then 1 or 2 files".

## 1. Measurements

### 1.1 Files over ~800 lines or 45 KB (first-party only)

| File | Lines | KB | Note |
|---|---:|---:|---|
| `src/Glide.App/Settings/SettingsWindow.axaml.cs` | 2,997 | 182 | Load/Read/Map for every control, plus search and layout |
| `src/Glide.App/Settings/SettingsWindow.axaml` | 831 | 110 | 188 lines over 200 chars (max 810) |
| `src/Glide.App/Controls/WindowInWindowOverlayManager.cs` | 2,138 | 104 | One class |
| `src/Glide.Core/Settings/SettingsCatalog.cs` | 358 | 93 | 225 lines over 200 chars (max 1,187): hard to `Edit` exactly, costly to diff |
| `src/Glide.App/Controls/ImageViewport.cs` | 1,988 | 91 | |
| `src/Glide.App/MainWindow.Navigation.cs` | 1,802 | 91 | |
| `src/Glide.Imaging/BuiltInRasterDecoder.cs` | 2,153 | 89 | Many formats in one file |
| `src/Glide.App/MainWindow.Commands.cs` | 1,404 | 79 | `CreateCommandDispatcher` lives here |
| `src/Glide.App/MainWindow.Tabs.cs` | 1,653 | 77 | |
| `src/Glide.Imaging/ImageLoadCoordinator.cs` | 1,358 | 75 | |
| `tests/Glide.Core.Tests/StatusBarAutoHideTests.cs` | 1,473 | 73 | Largest test file |
| `src/Glide.App/Styles/GlideTheme.axaml` | n/a | 66 | |
| `src/Glide.App/MainWindow.Browser.cs` | 1,196 | 60 | |
| `src/Glide.App/MainWindow.StatusChrome.cs` | 1,098 | 55 | |
| `src/Glide.Core/Settings/SettingEffectRegistry.cs` | 326 | 55 | 251 near-identical rows (see 2.2) |
| `src/Glide.Core/Commands/AppActionRegistry.cs` | 568 | 54 | |
| `src/Glide.App/Settings/SettingsStore.cs` | 884 | 53 | |
| `src/Glide.App/MainWindow.TitleSettings.cs` / `.Sessions.cs` / `.axaml.cs` | 977 / 958 / 890 | 51 / 49 / 52 | |
| `src/Glide.Imaging/ScientificDecoders.cs`, `PdnDecoder.cs`, `VideoContainerProbe.cs`, `RawPreviewDecoder.cs`, `TextureBlockDecoders.cs`, `ExrDecoder.cs` | 816–1,137 | 46–55 | Self-contained decoders: low agent cost, leave alone |
| `src/Glide.Diagnostics/DiagnosticRunner.cs` | 869 | 52 | |
| `tests/Glide.Core.Tests/HotkeyEndToEndTests.cs`, `tests/Glide.Explorer.Tests/ExplorerTabCoreTests.cs` | 982 / 858 | 51 / – | |
| `native/Glide.ShellThumbnail/ThumbnailCore.cpp` | 2,693 | – | |
| `native/Glide.Native/GlideNative.cpp`, `native/Glide.Codecs/GlideCodecs.cpp` | 1,373 / 1,140 | – | |

Totals: 157k lines in 596 first-party source and script files. `Glide.App` holds 2.7 MB of C#/XAML,
`Glide.Core.Tests` 1.9 MB. The `Glide.Input` project is one 3 KB file.

The decoder files are large but cohesive: an agent reads one per format bug. The real cost is the
UI god classes, because almost every feature touches them.

### 1.2 What adding one Toggle setting touches today

Traced from `general.showRecentOnHome` (13 occurrences in 9 files) and `tabs.cycleOrder` (11 files):

| # | File | What | In the documented recipe? |
|---|---|---|---|
| 1 | `src/Glide.Core/Settings/SettingsCatalog.cs` | metadata, default, search terms | yes |
| 2 | `src/Glide.Core/Settings/SettingEffectRegistry.cs` | effect row; a **missing row throws in the static constructor** | **no** |
| 3 | `src/Glide.App/Settings/GlideSettingsState.cs` | property and default (duplicates the catalogue default) | **no** |
| 4 | `src/Glide.App/Settings/SettingAccessors.cs` | get/set lambdas | yes (AGENTS.md) |
| 5 | `src/Glide.Core/Settings/SettingActionCatalog.cs` | Choice option list (duplicates the combo items in XAML) or an exclusion | yes |
| 6 | `src/Glide.App/Settings/SettingsWindow.axaml` | the control | implied |
| 7 | `SettingsWindow.axaml.cs` LoadControls (~line 445) | `X.IsChecked = s.Prop` | **no** |
| 8 | `SettingsWindow.axaml.cs` ReadControls (~line 759) | `s.Prop = X.IsChecked == true` (duplicates #4) | **no** |
| 9 | `SettingsWindow.axaml.cs` BuildSettingControlMap (~line 2406) | `M("id", X)` | yes |
| 10 | `SettingsStore.cs` / `SettingsDefaults.cs` | normalisation, for Choice and numeric settings | **no** |
| 11 | `tests/.../SettingsCatalogTests.cs`, `SettingsCardLayoutTests.cs` | hard-coded `306` and `41` | **no** |
| (+) | the consuming feature code (`MainWindow.*.cs`) | the actual effect | – |

Items 3, 4, 7 and 8 are four hand-written copies of the same "id ↔ property" mapping. Item 2 is 251 rows
in which `EvidenceId` is always `"settings.effect." + id` and `EvidenceStatus` is always `Unknown`. Neither
field is read anywhere outside the file, so it is dead metadata kept in sync by hand.

### 1.3 Magic strings

Choice values are English UI labels. The same strings are persisted to JSON, compared in logic, listed in
`SettingActionCatalog.Choices` and listed again as combo items in XAML. Seven sample values (`"Welcome tab"`,
`"Recent pictures page"`, `"Ask me each time"`, …) appear 69 times in 23 files, including `MainWindow.Tabs.cs`,
`MainWindow.ViewerChrome.cs`, `SettingsStore.cs` and `WindowInWindowOverlayManager.cs`. In `Glide.App`, 45
`string.Equals(..., "Some Label", StringComparison...)` calls compare against labels. Rewording a label
therefore means a data migration plus edits in about 10 places, and a typo compiles cleanly.

### 1.4 Hard-coded counts that break on legitimate additions

- `SettingsCatalogTests.Glide_catalog_has_248_current_and_no_future_entries` asserts **306**; the name is already stale.
- `SettingsCardLayoutTests`: `Assert.Equal(41, cards.Count)`, with a comment that keeps a running history.
- `GestureCatalogTests`: 54 slots (twice). `Glide22CapabilityTests` and `ImageFormatRegistryTests`: 196
  extensions (6 times), 10 fast-path extensions. `ManagedFormatRoutingTests`: 40 RAW extensions.
- The legacy 75 / 56 hotkey and gesture counts are frozen history and fine to keep.
- Docs repeat counts that are now wrong. `GLIDE_MANIFESTO_AND_HANDOFF.md` says 271 entries and
  "about 268 actions" against 306 entries today. README says "132 settings". The checklists hard-code test
  totals.

### 1.5 Tests

- 1,727 `[Fact]`/`[Theory]`, **0 `[Trait]`**: you cannot run "just the settings tests" except by guessing
  class-name filters.
- `Glide.Core.Tests` is misnamed. It references `Glide.App`, Imaging, Diagnostics and Avalonia.Headless, so any
  App edit recompiles the whole test assembly. 68 files share `AvaloniaHeadlessCollection` with
  `DisableParallelization = true`, which is the main reason a full run takes ~2.5 min.
- **Source-text contract tests:** `SourceText.Read` is used from 33 test files (52 call sites). Example:
  `FitScopeContractTests` asserts that the literal `ApplyStatusBarFit("Fit width", Viewport.FitWidth)` exists in
  the MainWindow partials. Splitting a file, renaming a local or reformatting a call fails these tests with no
  behaviour change. That makes agents afraid to refactor, or they waste cycles discovering these tests.
- Eight version-named test files (`Glide22*`, `Glide23*`, `Regression422`, `Release503`, `Release506`,
  `TesterRoundFixesTests`, `AuditItemsTests`, 2,480 lines) say when a test was written, not which area it covers. An
  agent searching for "tests of tab closing" will not find them by name.

### 1.6 Build and output

- `build.cmd fast` still does CMake configure and build for 2 native projects, a Release solution build, the
  NativeAOT publish (the slow step) and the fixture check. There is no Debug "does it compile" path.
  `tools/run.cmd` is `dotnet run` (Debug) and is the closest thing to one.
- `AnalysisMode=Recommended` + `EnforceCodeStyleInBuild=true` + `TreatWarningsAsErrors=false` and no
  `.globalconfig`/`NoWarn` triage produce the reported ~2,000 warnings. An agent reading build output must
  page through them to find the one `error CS…` line, or else rely on the tail of the output.
- `build.cmd` deletes `build-output.txt` on success, so no warning baseline survives. On failure it runs
  `type build-output.txt | clip` (line 31), which **overwrites the user's clipboard**. That is a surprising side
  effect when an agent runs the build.

### 1.7 Discoverability and docs

- `AGENTS.md` is short and accurate on actions. Its settings line omits items 2, 3, 7, 8, 10 and 11 of 1.2. It has no
  pointer to a subsystem map, the build or test commands, or "where is X".
- No `ARCHITECTURE.md`. The best map is "Architecture notes added in 5.0.0" inside
  `docs/GLIDE_MANIFESTO_AND_HANDOFF.md` (36 KB). That file is titled **"GLIDE 5.0.1 — ACTIVE HANDOFF"** while the
  version is 5.0.8, and three quarters of it is historical checkpoints (4.7.0, 4.3.2, 4.3.1, 4.2.3).
- `docs/README.md` says `src/*/README.md` exists "one short page per project". **`Glide.App` (the biggest
  project) has none.** Neither have `Glide.ThumbnailCodecs` nor any test project.
- `docs/QUEUE-vertigo-round.md` and `docs/CLEANUP-5.0.md` are finished work logs that sit next to living
  docs. `docs/releases/PUBLISH-COMMANDS-5.0.0.md` and `-5.0.1.md` are near-copies (8 KB each) of a script
  written in prose.
- Grep-unfriendly naming: the MainWindow partial names are decent (`MainWindow.Tabs.cs`), but their sizes vary
  from 1.5 KB to 93 KB, and topics overlap across `Navigation`/`History`/`Browser`/`Explorer*`, and across
  `StatusChrome`/`StatusLayout`/`ViewerChrome`/`Chrome`.

### 1.8 Release

Steps that are copy-paste prose today: robocopy mirror into a clean clone, rename `Glide Setup.exe` →
`Glide.Setup.exe` and fix the name inside the `.sha256` file, replace `SHA256_PLACEHOLDER` in the notes,
commit, tag, `gh release create`, then download and verify the hash. Every one of these can be scripted. The
genuine hand checks (CET PC, installer upgrade, network share) cannot.

## 2. Recommendations

Effort: S < ½ day, M 1–3 days, L > 3 days. Savings are per typical agent task (estimates).

| # | Change | Saves | Effort | Risk |
|---|---|---|---|---|
| R1 | `.ignore` file + `git init` the working folder | 20–60k tokens per broad search; enables diff/revert | S | none |
| R2 | `tools/quick-check.ps1` (Debug compile, errors only, area-filtered tests) | 5–10 min → 30–90 s per iteration; ~3–8k tokens of build output per cycle | S | low |
| R3 | Test traits per area + split UI tests | lets R2 run 10–200 tests instead of 1,727 | M | low |
| R4 | `docs/ARCHITECTURE.md` subsystem map + fix AGENTS.md | 30–80k tokens of exploratory reading per task | S | none |
| R5 | Complete "add a setting" checklist now; single-source later (R9) | 2–4 failed build/test cycles per settings task | S | none |
| R6 | Replace hard-coded counts with derived invariants | one spurious failure + fix per catalogue change | S | low |
| R7 | Warning triage: `.globalconfig`, baseline, `-clp:ErrorsOnly` for agents | finding the error in 2k warnings (~5–15k tokens) | M | low |
| R8 | Retire source-text contract tests incrementally | unblocks file splits; fewer false failures | M | medium |
| R9 | Single-source the setting binding (generator or table) | ~6 edits → 2 per setting; removes 3 duplicate tables | M–L | medium |
| R10 | Typed choice values (enum or stable key + label) | label rewording becomes 1 edit; typos stop compiling | L | medium (persisted data) |
| R11 | Split god classes along subsystem lines | 50–150k tokens per UI task | L (incremental) | medium |
| R12 | Docs hygiene: archive history, one handoff, no counts in prose | ~10k tokens per "read the docs first" | S | none |
| R13 | `tools/release.ps1` | ~1–2 h owner time and error risk per release | M | low |
| R14 | Remove `| clip` from build.cmd; keep last log | avoids clobbering the clipboard | S | none |

### R1. Make search see only live source (S, no risk)

Add a root `.ignore` (ripgrep reads it even without git):

```
Archive/
.artifacts/
artifacts/
dist/
dist-installer/
codecs/
native/*/build/
native/third_party/
tools/benchmark_corpus/
tools/benchmark_references/
tools/bench-507/
docs/screenshots/
*.zip
```

Then make the working folder a git working tree, either by `git init` with the existing `.gitignore` or by
working directly in the publish clone that `PUBLISH-COMMANDS` already uses. Agents could then run `git diff` to
review their own change, `git stash`/`checkout` to undo, and `git log -S` for archaeology. Without git, every
"what did I change" question costs a re-read. **This change alone removes the 484 stale `Archive/` source copies
from every search.**

### R2. `tools/quick-check.ps1` (S, low risk)

Target: under 60 s on an incremental tree.

```powershell
param([string]$Area, [switch]$NoTests)
dotnet build src/Glide.App/Glide.App.csproj -c Debug -m -nologo -clp:"ErrorsOnly;Summary" -p:RunAnalyzers=false -p:EnforceCodeStyleInBuild=false
if ($LASTEXITCODE) { exit $LASTEXITCODE }
python tools/audit-settings-search.py; python tools/audit-action-registry.py   # each < 2 s
if (-not $NoTests) {
  $f = if ($Area) { "Category=$Area" } else { "Category!=Slow&Category!=UI" }
  dotnet test tests/Glide.Core.Tests -c Debug --filter $f -nologo -v q
}
```

Notes: Debug skips AOT, R2R and composite R2R, and turning analyzers off removes the warning flood. The
native DLLs are already copied from their existing build output by `Directory.Build.props`, so no CMake run is
needed. Add an `-Area` → filter map (for example `settings`, `hotkeys`, `explorer`, `imaging`, `tabs`) and
document it at the top of AGENTS.md. Keep `build.cmd fast` as the pre-PR step.

### R3. Test traits and a faster UI collection (M, low risk)

- Add `[Trait("Area", "...")]` at class level. A one-off script can tag by file-name prefix: `Settings*`,
  `Hotkey*`, `Gesture*`, `Explorer*`, `Overlay*`, `Print*`, `Update*`, `Reset*`, `*Decoder*` and so on. The 8
  version-named files need manual sorting. Add `Category=UI` to the headless collection and
  `Category=Slow` to perf harnesses.
- Rename the 8 version-named files by area (or move their tests into the matching area files).
- Longer term, move the 68 Avalonia tests into `Glide.App.Tests`. Pure Core/Imaging tests then compile without
  Avalonia and run in parallel, and `Glide.Core.Tests` means what it says.

### R4. `docs/ARCHITECTURE.md` + a sharper AGENTS.md (S, none)

A one-screen table of subsystem → entry files → tests → doc. For example:

| Subsystem | Start here | Also | Tests |
|---|---|---|---|
| Actions/hotkeys/gestures | `Glide.Core/Commands/AppActionRegistry.cs` | `MainWindow.Commands.cs` (`CreateCommandDispatcher`), `HotkeyCatalog.cs`, `GestureCatalog.cs` | `AppActionRegistryTests`, `Hotkey*`, `Gesture*` |
| Settings | `Glide.Core/Settings/SettingsCatalog.cs` | `SettingEffectRegistry.cs`, `GlideSettingsState.cs`, `SettingAccessors.cs`, `SettingsWindow.*`, `SettingsStore.cs` | `Settings*` |
| Image loading | `Glide.Imaging/ImageLoadCoordinator.cs` | decoders, `CodecCapabilityRegistry` | `ImageLoad*`, `*Decoder*` |
| Viewport/zoom/pan | `Controls/ImageViewport.cs` | `MainWindow.ViewerChrome.cs` | … |
| Tabs/sessions | `MainWindow.Tabs.cs`, `MainWindow.Sessions.cs` | `Glide.Core/Workspace/*` | `Tab*`, `Workspace*` |
| Explorer tab | `MainWindow.Browser.cs`, `MainWindow.Explorer*.cs` | `Platform/WindowsBrowserFrameController.cs` | `Glide.Explorer.Tests` |
| Folder navigation/history | `MainWindow.Navigation.cs`, `MainWindow.History.cs` | `Glide.Core/Navigation/*` | `Navigation*`, `EndOfFolder*` |
| Status bar/chrome | `MainWindow.StatusChrome.cs`, `StatusLayout.cs`, `Chrome.cs` | | `StatusBar*`, `StatusChrome*` |
| Window-in-window overlays | `Controls/WindowInWindowOverlayManager.cs` | `NativeOverlayWindowRenderer.cs` | `Overlay*` |
| Native bridge/thumbnails | `native/Glide.Native`, `native/Glide.ShellThumbnail` | `docs/explorer-thumbnails.md` | `ShellThumbnailProviderTests` |

A generator script (`tools/gen-architecture.py`) can keep the file sizes and test-file globs current.
AGENTS.md should grow to ~25 lines: the commands (quick-check, fast build, audits), the settings checklist (R5),
"source-text tests exist: if you rename code, run `--filter Category=SourceContract`", and "never edit
`Archive/`". Also add the missing `src/Glide.App/README.md`, which should point at ARCHITECTURE.md.

### R5. Make the settings recipe complete today (S, none)

Before any refactor, replace the AGENTS.md settings bullet with the full 1.2 checklist. Mention that a missing
`SettingEffectRegistry` row throws `TypeInitializationException` at the first use of the catalogue, and that
`LoadControls` and `ReadControls` both need lines. Better still, have `audit-settings-search.py` print the exact
missing items per setting id ("`foo.bar`: no GlideSettingsState property, no ReadControls line"). An agent can
then work from one audit run instead of a test run.

### R6. Derived invariants instead of counts (S, low)

Replace `Assert.Equal(306, SettingsCatalog.All.Count)` with properties that hold for any catalogue: ids are unique,
every id has an effect row, an accessor or exclusion, and a mapped control, and gesture rows equal
`GestureCatalog.Slots.Count`. Do the same for extensions (`ImageFormatRegistry` ⇔ `CodecCapabilityRegistry`
set equality) and for cards (every card has a heading and at least one mapped control). If the owner wants a
"count changed" tripwire, put the counts in one `tests/expected-counts.json` that the audit script can update
with `--accept`. Remove counts from prose docs, or generate them.

### R7. Warning triage (M, low)

1. Capture a baseline once: `dotnet build -c Release -bl` → group by ID (`dotnet build ... | sls 'warning (\w+)'`
   grouped). The top 5 to 10 IDs usually cover 90 % of the warnings.
2. Add a root `.globalconfig` that sets IDs the owner does not want to `none` (typically CA1305/CA1307/CA1310
   culture rules on UI strings, CA1848 logging, IDE style rules that cannot be fixed cheaply). Fix
   the cheap rest mechanically with `dotnet format analyzers --diagnostics <ID>`.
3. Promote the real-bug families to errors with `WarningsAsErrors`: nullable (`CS86xx`), `CA2000`, `CA2007` if
   relevant, and the IL/AOT warnings (IL2xxx/IL3xxx). Release checklists already track "only the 3 known IL
   warnings", so that check can become a build gate.
4. Until then, give agents `-clp:ErrorsOnly` (R2) and a `tools/build-errors.ps1` that prints only `error` lines
   from `build-output.txt`.

### R8. Retire source-text contract tests (M, medium)

Tag them `[Trait("Category","SourceContract")]` now so agents know which failures a rename caused. Then convert
them area by area. Most check wiring that a headless test can observe: the Fit button calls the scoped helper
→ click it in the headless window and assert `_sessionDefaultViewMode`; or a small internal seam (an
`internal static` policy method) that a unit test calls directly. Keep source scans only for real
repository-wide guards such as privacy or forbidden APIs, and centralize those in one `RepoScanTests.cs`. This must
come before R11, because otherwise every file split breaks dozens of assertions.

### R9. Single-source the setting binding (M–L, medium)

Goal: adding a toggle = catalogue line + XAML control + effect code.

- Make `SettingAccessors` the one id ↔ property table (or generate it; see below). Have
  `SettingsWindow.LoadControls`/`ReadControls` loop over `BuildSettingControlMap` × `SettingAccessors` for every
  CheckBox/ComboBox/NumericUpDown/TextBox. Keep hand-written code only for derived and multi-control settings. This
  deletes hundreds of lines from `SettingsWindow.axaml.cs`.
- Move defaults to one place: `GlideSettingsState` initializers from `SettingsCatalog.DefaultValue`, or a test
  that asserts they are equal. Note: two default sources exist today, and `AuditItemsTests` checks only some of them.
- Make `SettingEffectRegistry` rows default to `(owner from category, FromCatalog, CommitOnly)`. List only the
  exceptions, and drop `EvidenceId`/`EvidenceStatus` (unused) or compute them. 251 rows become ~40.
- Fill Choice option lists in XAML combos from `SettingActionCatalog.Choices` at runtime instead of
  duplicating them. The audit already checks that they match, which is proof that the duplication bites.
- AOT constraint: no reflection. Use a Roslyn incremental source generator over a `[Setting("id")]` attribute on
  `GlideSettingsState` properties that emits `SettingAccessors.All`. It is AOT-safe and keeps the table out of
  hand-edited code. A simpler alternative: a `tools/gen-settings.py` that emits a `.g.cs` from a CSV/JSON table
  checked into `src/Glide.Core/Settings/settings.json`, with metadata, default, state property, effect owner
  and choices in one row. The second option also fixes the 1,187-character lines.

### R10. Typed choice values (L, medium)

Give every Choice setting a stable key (`"welcome"`, `"recent"`, `"explorer"`) and a separate display label.
Logic compares keys, ideally through a generated enum. Migrate the persisted JSON in `SettingsStore` (it already
has a normalization layer). This removes the 23-file label coupling and lets the UI wording change freely. Do it
after R9, which centralizes the options.

### R11. Break up the god classes (L, incremental; medium)

Do not split by line count. Extract subsystems that already have names into plain classes that own their state,
and keep MainWindow as the composition root:

- `WindowInWindowOverlayManager` (104 KB): layout/collision policy (partly done in Core) vs. rendering vs. input.
- `SettingsWindow.axaml.cs`: after R9 the Load/Read halves shrink sharply; move search into `SettingsSearchController`
  and responsive layout into its existing `.Responsive.cs` file.
- `SettingsWindow.axaml` (110 KB): one `UserControl` per page or card group (8 pages / 41 cards). An agent then
  opens a 10 KB file instead of 110 KB.
- `MainWindow.Navigation.cs` (93 KB), `.Tabs.cs` (77 KB), `.Commands.cs` (79 KB): move the dispatcher table into
  `CommandDispatcherTable.cs`, so the registry recipe points at a small file, and move navigation and tab state machines
  into `Glide.Core` policies (the `TabCloseActivation`/`EndOfFolderPolicy` pattern already works well and is unit-tested).

Expected payoff: a UI feature task reads 1 or 2 files of 10 to 30 KB instead of 3 to 6 files of 50 to 100 KB.
Prerequisite: R8.

### R12. Docs hygiene (S, none)

- Rename and trim `GLIDE_MANIFESTO_AND_HANDOFF.md` into `docs/PRINCIPLES.md`, keeping the invariants, release
  output contract and toolchain (~8 KB). Move the 4.x and 5.0.0 checkpoints to `docs/history/`. Remove the
  version from the title so it cannot go stale.
- Move `QUEUE-vertigo-round.md`, `CLEANUP-5.0.md`, the 5.0.0/5.0.1 checklists and the publish commands to
  `docs/history/`, and keep one generic `docs/RELEASING.md`.
- Update `docs/README.md` to match reality (Glide.App README, audits folder).
- Tell agents in AGENTS.md not to read `CHANGELOG.md` (170 KB, ~43k tokens) except its top section.

### R13. Scripted release (M, low)

`tools/release.ps1 -Version (from Directory.Build.props)`. It runs `build.cmd --no-pause` and checks the
"only known IL warnings" gate. It then copies and renames `Glide Setup.exe` → `Glide.Setup.exe`, rewrites the
`.sha256` file and replaces `SHA256_PLACEHOLDER` in the notes. It mirrors into the clean clone with the same
robocopy exclusions, shows `git status`, and waits for an explicit `-Publish` switch before commit, tag and
`gh release create`. Finally it downloads the assets and verifies the hash. The hand checks stay in a short
generic checklist. This retires the per-version `PUBLISH-COMMANDS-*.md` copies.

### R14. Build script side effects (S, none)

Remove the `type ... | clip` on failure from `build.cmd`, or guard it behind `GLIDE_COPY_LOG=1`. Keep the last log
as `build-output.last.txt` instead of deleting it on success, so a warning baseline and agent post-mortem are
possible.

## 3. Suggested order

1. **Day 1 (all S):** R1, R14, R4, R5, R6, R12, R2 using name-based filters. These give the largest savings per
   hour.
2. **Next:** R3 (traits), R7 (warnings), R13 (release script).
3. **Then:** R8 → R9 → R11 for the settings window → R10 → R11 for MainWindow, one subsystem per change,
   each landing with `quick-check` and the two audits green.

Rough overall effect for a typical agent task (add a setting or fix a UI bug): tokens fall from
~150–300k to ~50–100k, wall-clock from 30–60 min (3 to 6 full builds) to 10–20 min (several quick-checks plus one
`build.cmd fast`), and "missed file" mistakes caught by tests fall to near zero because the audit names them
first.
