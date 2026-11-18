# Stage C notes (for the Stage D agent: split the god classes)

Date: 2026-11-18. Scope: Stage C of `audit-combined.md` (settings single source; C2, C3, C4 of `audit-coupling.md`, R9 of
`audit-ai-maintainability.md`). No intended user-visible behaviour change; glide.settings.json is byte-compatible (same
type and property names, same `AppJsonContext`). One commit per item (`git log --oneline`, messages start with "Stage C").

## What changed

### C3a. SettingEffectRegistry is derived, not hand-written
`SettingEffectRegistry.All` is computed from `SettingsCatalog.All`: owner from the id prefix (`OwnerByPrefix`, 28 prefixes)
unless listed in `OwnerExceptions` (6), editor from the kind (Action = DynamicAction), live/commit from
`LivePreviewPrefixes` unless listed in `SemanticsExceptions` (16), evidence id `settings.effect.<id>`. A one-off dump
before/after proved all 306 bindings identical. 251 hand rows (~260 lines) are gone; a new setting needs no row.
`SettingEffectRegistry.Find(id)` (dictionary) is the runtime lookup. Note the row order is now catalogue order.

### C4a. The settings schema lives in Glide.Core
`GlideSettingsState`, `WindowPlacementState`, `OverlayWindowPlacementState`, `ShowInExplorerTargets` and
`TitleBarButtonCatalog` moved to `src/Glide.Core/Settings/` (namespace `Glide.Core.Settings`; `<Using>` added to Glide.App and
both App-referencing test projects). `ExplorerDefaultViews` stays in App (`Settings/ExplorerDefaultViews.cs`, depends on
Explorer view modes). Members that were `internal` (`UpdateStateProperties`, `NormalizeUpdateState`, the `*Equal` helpers,
`ClonePlacements`) are public now. `ArchitectureContractTests` pins: Glide.Core references neither Avalonia nor Glide.App.
This unblocks the Glide.App.Tests split (stage-b-notes B2 plan, steps 2-4) - not done here.

### R9. One id <-> property table; table-driven toggles
- `SettingAccessors.Values` = the hotkey-bindable `All` rows (unchanged, 168) + `NonBindableRows()` (`Flag`/`Int`/`Double`/`Str`:
  numbers, paths, non-bindable toggles; 50 rows). 218 ids. Six legacy control-map ids without a catalogue entry are in it
  (`general.newExplorerTab*`, `appearance.explorerTheme`, `tabs.show*Button`, `folderNav.explore`).
- `SettingsWindow.LoadControls/ReadControls` call `LoadMappedToggles`/`ReadMappedToggles`: every `_settingControls` entry that
  is a `ToggleButton` with a Bool accessor. 125 toggles, 250 hand-written lines deleted. Combos, numbers, text and derived
  values (LightTheme, RightDragPan, FullscreenXClosesApp, ShowTrayIcon, RememberedMultiTabCloseChoice...) stay hand-written.
- Tests: `SettingBindingTableTests` (every accessor round-trips; catalogue toggle defaults equal state defaults; every mapped
  toggle loads and reads through the window).
- Not done: a Roslyn generator. The table is plain C# lambdas (NativeAOT-safe, no reflection). Generating it from the
  catalogue would need the property name in `SettingDefinition`; the C# table was the smaller step.
- Defaults: the catalogue `DefaultValue` and the `GlideSettingsState` initialisers are still two places; the new test makes
  them fail together for toggles. Numbers/choices are not checked yet (some catalogue defaults are display values).

### C1/C3. SettingsService and owner routing
- `src/Glide.App/Settings/SettingsService.cs`: the one owner of the process-wide live settings. `Save(state)` composes
  `WindowPlacementService.ApplyTo` + update-checker memory, calls `SettingsStore.Save`, publishes. `Publish(state, delta?)`
  (previews), `SavePresetSnapshot` (Settings window's "save User custom preset": persisted, not published - same as before),
  `Current`, and `Changed` (event args with a lazily computed `SettingsDelta`). `App.PublishCurrentSettings` /
  `TryGetStartupSettings` forward to it; `App._currentSettings` is gone. Nothing outside the service calls
  `SettingsStore.Save` now (load/migration stays in `SettingsStore`).
- `SettingsDelta.Compute(before, after)`: changed ids from `SettingAccessors.Values`; `HasUnmappedChanges` when anything
  without an accessor differs (hotkeys, gestures, title-bar buttons, derived fields, runtime-owned values).
- `MainWindow.SettingsEffects.cs`: `ApplySettingsFromDialog` computes the delta once. If every changed id is listed by its
  owner's handler (owner = `SettingEffectRegistry`), only those handlers run; otherwise (empty delta, unmapped change, any
  unlisted id) the full `ApplySettingsVisuals` runs exactly as before. 10 owners have handlers (Navigation, FolderNavigation,
  Slideshow, StatusBar stats, Overlay text style, Input, Viewing, Developer, TabsWorkspace, General); ~60 ids. Most are
  "read at use" (the feature reads `_settings` when it runs) and were verified by call-site search (`TryAcceptNavigationRateLimit`,
  `EnsureSlideshow` lambdas, `HandleEscapeAsync`, `ResolveNewTabOpening`, Explorer wheel handler...).
- The text overlay fast path is the Overlay route (`TextOverlayStyleIds`); `OnlyTextOverlayStyleDiffers` is kept for tests.
  Preview cost: one `SettingsDelta.Compute` (two `ContentEquals` + 218 accessor reads) replaces `OnlyTextOverlayStyleDiffers`
  (two `ContentEquals` + a clone), so the spinner fast path is unchanged (tests: 0 full passes over 7 font-size previews).
- Commit after an identical preview (empty delta) still does the full pass, as before.
- Tests: `SettingsDeltaRoutingTests`, `Release506Tests.Stage_C_owned_previews_are_routed_and_unknown_ones_fall_back_to_the_full_pass`.

### C2. Option slices
`src/Glide.Core/Settings/SettingsOptionSlices.cs`: `NavigationOptions`, `SlideshowOptions`, `ExplorerOptions`,
`StatusBarOptions`, `OverlayOptions` - read-only structs over the state (member = property minus the feature prefix, e.g.
`StatusBarOptionsView.AutoFit` for `StatusBarAutoFit`). MainWindow exposes `NavigationOptionsView`... built per access from the
current `_settings` (never stale, even though many partials still mutate `_settings` in place). 218 reads replaced
mechanically (writes untouched). `python tools/count-settings-reads.py [--files]`:
**533 -> 315 direct `_settings` reads in MainWindow partials** (620 -> 402 incl. writes; 219 slice reads).

### C4b. Platform availability
`SettingDefinition.Platforms` (`SettingPlatforms` flags, default `All`); `SettingsCatalog.WindowsOnlyIds` marks 10
(thumbnail provider x5, file associations, default viewer, start with Windows, registry and startup resets). Nothing reads it
yet. `audit-setting-wiring.py` fails on an unknown id there and prints the count; `ArchitectureContractTests` pins it.

## Numbers
| | before | after |
|---|---|---|
| Files edited to add a plain toggle | 7 files, 9 edits (catalogue, effect row, accessor, state, axaml, Load + Read + M, consumer) | 6 files, 6 edits (catalogue, state, accessor, axaml, M, consumer) |
| ... a Choice / Number | 7-8 files, 9-10 edits | 6-7 files, 8-9 edits (Load/Read lines remain) |
| Hand-written SettingEffectRegistry rows | 251 | 22 exceptions + 28 prefix owners |
| Hand-written toggle Load/Read lines in SettingsWindow | 250 | 0 |
| `ApplySettingsVisuals` edits needed for a new live setting | 1 (always) | 0 (fallback) or 1 narrow route |
| Direct `_settings` reads in MainWindow partials | 533 | 315 |
| Places that write glide.settings.json | 3 (`MainWindow`, `SettingsWindow`, store) | 1 (`SettingsService`) |

## Not done / for later
- **`ImageSortOrder.Current` global** (item 4, judged not cheap): still set by `Program.LoadSettingsAndActivateSortOrder` and
  `ApplyNavigationSortOrder`. Plan: `ImageNavigator(Func<ImageSortSpec>)` plus `LastSortSpec` (so `ApplyNavigationSortOrder`
  compares against the navigator, not the global), an `ImageSortSpec` parameter on the static `EnumerateSupportedFolder`,
  `SiblingFolderNavigationService.EnumerateImages` and `TabPreviewCache.FirstPicturesAsync`, and `SettingsService.SortOrder`
  (set in `Publish` and once at startup) as the provider for windows. ~25 call sites incl. tests and one source-text test
  (`BoundaryFolderRefreshContractTests`). Add `ImageSortSpec Sort` to `NavigationOptions`.
- **R10 stable Choice keys** (optional, not started). Plan: add `SettingChoice(Key, Label)` lists in `SettingActionCatalog.Choices`
  (keys = current English labels lower-kebab, e.g. `end-of-folder.next-folder`), store keys in a new schema step
  (`SettingsStore.MigrateFromJson` maps label -> key; unknown values -> default), keep reading old labels forever, make
  `SettingsWindow` combos bind by key (Tag) instead of `SelectByText`, and the accessors/`NextChoice` by key. Test with a corpus
  of old files (every Choice label of every release) round-tripping to keys. Do it per setting group; the label change becomes
  a one-line edit afterwards.
- More slices: `TabOptions`, `GestureOptions`, `AppearanceOptions`, `PerformanceOptions` (`BuildPerformancePolicy`), then the
  remaining 315 reads (`TitleSettings`, `Tabs`, `Startup` are the biggest; `count-settings-reads.py --files`).
- More narrow routes: Appearance (theme/accent re-runs `ThemePaletteApplier` + brushes), StatusBar visibility groups, tab strip
  options. Each needs the owner's apply extracted out of `ApplySettingsVisuals`; Stage D's controller extraction (status bar
  presenter first) is the natural place - give each controller its `SettingEffectHandler`.
- `SettingsChangeSet` (unsaved-changes dialog) still diffs by reflection over `GlideSettingsState`; it could use
  `SettingsDelta` + catalogue labels (keep the current wording for unmapped properties).
- Glide.App.Tests split: now possible (settings types are in Core); follow stage-b-notes B2 steps 2-4.
- B3 follow-up from stage-b-notes: `GetField(window, "_settings")` helpers can now read `SettingsService.Current` or an
  `internal` accessor.

## Verification
- `tools/quick-check.ps1 -Area Settings`: 141 passed. All three audits pass (wiring: 252 settings, 10 Windows-only, 39 soft
  findings, unchanged count).
- Full `build.cmd --no-pause`: exit 0, 0 failed, 0 skipped (Core 2404, Input 21, Explorer 195 passed).
