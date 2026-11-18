# Stage B notes (for the Stage C agent: settings single source)

Date: 2026-11-18. Scope: Stage B of `audit-combined.md`. No intended user-visible behaviour change. One commit per item
(`git log --oneline`, messages start with "Stage B").

## What changed

### B0. The four skipped tests are back
They predated two intentional 5.0.8 defaults, not app bugs:
- Mouse side buttons are pass-through by default ("Default (not used by Glide)"): the press is not handled unless the
  user binds an action. `HotkeyEndToEndTests` (two tests) and `ExplorerNavigationHistoryTests.Back_after_arrow_navigation…`
  now assert: default = unhandled and nothing moves; explicitly bound to `nav.back`/`nav.forward` = handled, Back/Forward.
- End of folder defaults to "Ask me each time". `EndOfFolderRenderTests.Settings_card…` asserts that. Its teardown
  removing the app styles threw "Call from invalid thread" (Avalonia transitions on the headless MediaContext clock) once
  the body passed; teardown now removes each style tolerantly. Zero skipped tests remain.

### B1. Warnings: 2323 -> 148 unique (Release rebuild of Glide.sln)
- `Glide.globalconfig` (all projects) and `tests/Glide.Tests.globalconfig` (projects named `*.Tests`, `global_level = 101`),
  wired through `GlobalAnalyzerConfigFiles` in `Directory.Build.props`. Every suppression has its reason in the file:
  CA1861, CA2007, CA1416 everywhere; CA1707, CA1711, CA1305/1307/1310 and xUnit style rules in tests.
- Real sites fixed: Ordinal `StartsWith("--")` in `CommandLineTools`/`Program`, `".~"` in `FolderWatch`; Invariant in
  `UiDiagnosticCapture`, `DiagnosticRunner`, the diagnostics export stamp (a non-Gregorian locale used to change the year in
  the file name) and the settings dump; explicit `CurrentCulture` for UI text (title template numbers, Explorer dates).
- `WarningsAsErrors` now includes `nullable` (was clean after fixing 3 sites). The five libraries (Core, Imaging, Input,
  Diagnostics, ThumbnailCodecs) run the trim/AOT analyzers and fail on IL2xxx/IL3xxx (analyzers only, no IsTrimmable
  metadata). Glide.App is not clean: Avalonia `ReflectionBinding` in SettingsWindow XAML (IL2026 x2) and Svg.Custom/ExCSS
  (IL2104/IL3053) at AOT publish, so it keeps them as warnings.
- ThumbnailCodecs lost its private `NoWarn` list; linked Imaging files now get Imaging's severities.
- Remaining 148: CA1859/CA1822/CA1826/CA1806/CA1870 (perf/style suggestions), CA1068, CS0618 (obsolete headless key API
  in Explorer test harness), xUnit1031, a few one-offs (CS0675 in ExrPiz, CS0108 BackupDialogs.Clip, CS0414/CS0219 in tests).
  Baseline method: `dotnet build Glide.sln -c Release --no-incremental -flp:WarningsOnly` with a binlog (counts are unique
  lines). CA1307 stays off: string.Contains/Replace/IndexOf without a comparison are already ordinal.

### B2. Test areas
- `[Trait("Area", …)]` on every test class (174 files): Explorer 27, Decode 26, Navigation 16, Settings 14, Window 12,
  Hotkeys 11, Tabs 8, Overlay 8, Gestures 8, Data 7, Regression 7, Print 7, Video 6, Update 5, Diagnostics 4,
  Thumbnails 4, StatusBar 4, Security 3, Shell 3. New test classes need one.
- `tools/quick-check.ps1 -Area Settings` (combines with `-Filter`), `-Project All` runs Core, Explorer and Input.
- **Glide.App.Tests split: not done** (not straightforward). Almost every Core.Tests class references `Glide.App` types
  (SettingsStore, GlideSettingsState, MainWindow, SettingsWindow), and the headless fixture, `SourceText`, the store
  isolation collections and `ManagedDecoderTestSupport` are shared. Plan: (1) Stage C moves `GlideSettingsState` and the
  settings catalogue into Core; (2) then move the classes whose only App dependency was settings into a pure
  `Glide.Core.Tests`; (3) rename the rest to `Glide.App.Tests` with the headless fixture; (4) add an
  `ArchitectureContractTests` (Core has no Avalonia and no Glide.App reference).

### B3. Source-text / reflection tests (partial)
- Replaced with behaviour tests: `AlwaysOnTopContextMenuContractTests` (deleted; menus are built and clicked headless in
  `HotkeyEndToEndTests.Windowed_and_fullscreen_context_menus_expose_a_tickable_always_on_top_item`) and two
  `FitScopeContractTests` checks (now `Status_bar_Fit_buttons_apply_their_mode_through_the_Fit_control_scope`).
- Still open: 126 source-text grep lines in 36 files (mostly native/overlay window contracts, installer/security scans
  and UI layout) and ~230 private-member reflection lines (`GetField(window, "_settings")` style helpers in the big
  MainWindow tests). The AvaloniaHeadlessFixture locator reflection is documented and stays. The cheapest next step is
  `internal` accessors on MainWindow for the 10 most-used fields (`_settings`, `_workspace`, `_currentPath`,
  `_statusCollapsed`, `_sessionDefaultViewMode`…) — Glide.App already exposes internals to the test projects — then a
  sweep replacing `GetField(window, "...")`. Do it after Stage C, which changes how `_settings` is owned anyway.

### B4. Waits
- `tests/TestSupport/Wait.cs`: `Wait.UntilAsync(condition, what, timeoutMs, pump)`, `TryUntilAsync`, `ForAsync(task)`,
  blocking `Until`. `ExplorerHarness.WaitUntil` uses it.
- 18 sleep-then-assert or hand-rolled polling sites converted (Overlay video, update UI, skip hint, title bar locate,
  hotkeys, image loader, folder watch, Explorer copy-path, tile captions). 95 -> 77 `Task.Delay`/`Thread.Sleep` sites
  remain: frame-pump `Settle` loops, negative waits ("nothing happens within N ms", now commented where touched),
  debounce/double-click spacing, fake cancellable delays, and pauses before screenshot capture.

### B5. `tools/release.ps1`
Dry run by default; `-Publish` is required for sha256 file, notes placeholder, mirror, commit, tag, push and
`gh release create` (`-WhatIf` forces a dry run). Privacy grep (C:\Users\<name>, /Users/<name>, the user name as a word,
`-PrivacyPattern`) over the project files and, when publishing, over the clone after the mirror. Verified by a dry run
against a scratch clone and fake assets. Never run with `-Publish` by an agent.

### B6. Coupling stage 1
- **Sort order**: `SettingsStore` no longer sets `ImageSortOrder.Current`. `Program.LoadSettingsAndActivateSortOrder`
  sets it right after the startup load (same moment as before). `ImageSortOrder.SpecFor(name, descending)` added. The
  global itself remains (navigator, tab previews, sibling folders read it); passing `ImageSortSpec` explicitly belongs
  with the Stage C `NavigationOptions` slice.
- **Catalogue order**: `Glide.Core.GlideCatalogs.Build()/Validate()` documents and fixes the order (gesture slots ->
  settings -> actions/hotkeys -> effect bindings -> data -> reset). `SettingEffectRegistry` has no validating static
  constructor any more (validation is in tests/diagnostics). Data <-> Reset cycle broken: `ResetItemId`, `ResetGroup` and
  the settings property groups (`GlideSettingsPropertyGroups`) live in `Glide.Core.Data`; `ResetCatalog` forwards its old
  members; the lazy-init workaround in `GlideDataCategoryCatalog` is gone. The Commands <-> Settings *namespace* edge
  still exists (SettingsCatalog generates gesture rows; AppActionRegistry generates setting actions) but there is no
  type-initialiser cycle; Stage C's generated catalogue is the place to make it one-way.
- **Commands**: the 17 commands that invoked click handlers with `new RoutedEventArgs()` call extracted methods
  (`OpenImageFromPickerAsync`, `OpenSettingsAsync`, `ToggleAlwaysOnTopSetting`, `NavigateBrowserUp`…). Async ones go
  through `Fire` (same fire-and-forget timing as the async void handlers). Left: `SettingsClicked(this, new RoutedEventArgs())`
  in `MainWindow.Sessions.cs`/`TitleSettings.cs`, `BrowserRefreshClicked(null, …)` in ExplorerMenus, and SettingsWindow's
  Ok/Cancel/Backup handler calls (not commands).
- **Window registries**: `GlideWindowRegistry` is the single list; each entry has an "open window" membership
  (Register/Unregister) and an "external-open receiver" membership (`SetLaunchReceiver`, ordered by when it became a
  receiver) so Speed Boost standby and the broker behave exactly as with the two lists. `ExternalLaunchBroker` keeps its
  own lock for its pipe state and calls the registry.

## For Stage C (settings single source)
- Settings touch points you will meet: `SettingsCatalog` (+ gesture rows), `SettingEffectRegistry`, `SettingActionCatalog`,
  `SettingAccessors` (App), `GlideSettingsState` (App), `SettingsStore.MigrateFromJson`, `SettingsWindow` Load/Read/
  `BuildSettingControlMap`, `GlideSettingsPropertyGroups` (Core.Data; formerly the ResetCatalog lists).
- `GlideCatalogs.Build()` is where a generated catalogue should be composed; keep `Validate()` the single test entry point.
- Moving `GlideSettingsState` to Core unblocks the Glide.App.Tests split (B2) and lets `ImageSortOrder.Current` go (B6).
- `tools/audit-setting-wiring.py --warnings` still lists 39 soft findings (see stage-a-notes item 3).
- Run `tools/quick-check.ps1 -Area Settings` and `-Area Data` while iterating.
