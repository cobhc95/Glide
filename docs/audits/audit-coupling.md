# Coupling audit: functionality spaghetti

Date: 2026-11-18. Scope: `src/` (Glide.Core, Glide.Imaging, Glide.Input, Glide.Diagnostics, Glide.ThumbnailCodecs, Glide.App), `tests/`, `native/`.
I only read the code for this audit. Nothing was edited, built or run. Line counts come from `wc -l` on `*.cs` files, excluding `obj/` and `bin/`.

---

## 1. Map

### 1.1 Project graph (from the `.csproj` files)

```
                 Glide.Core  (9.6k lines, no package refs; one shlwapi P/Invoke in ImageSort.cs)
                 ^   ^   ^   ^
                 |   |   |   +------------------------------+
   Glide.Input --+   |   |                                  |
   (71 lines)        |   |                                  |
                 Glide.Imaging (19.5k) --> Avalonia, Svg.Skia
                     ^      ^
                     |      +------- Glide.Diagnostics (1.4k) --> Core, Imaging
                     |                       ^
                 Glide.App (48.3k; 170 files) --> Core, Input, Imaging, Diagnostics, Avalonia.*

   Glide.ThumbnailCodecs (no ProjectReference; 18 <Compile Include> links into Core/Imaging source files)

   tests/Glide.Core.Tests     --> Core, Imaging, Diagnostics, **App**   (InternalsVisibleTo from App)
   tests/Glide.Explorer.Tests --> Core, Imaging, Diagnostics, **App**   (InternalsVisibleTo from App)
   tests/Glide.Input.Tests    --> Input
```

There are no project-level cycles. The coupling problems are inside projects, and in the "wrong-way" edges listed below.

### 1.2 Namespace graph inside Glide.Core

| From | Uses | Notes |
|---|---|---|
| Core.Commands | Core.Settings (`SettingsCatalog`, `SettingActionCatalog`) | `AppActionRegistry.All` generates the "Toggle:/Cycle:" actions from `SettingsCatalog.All`. |
| Core.Settings | Core.Commands (`GestureCatalog`, `AppActionRegistry`) | `SettingsCatalog` generates `gesture.*` rows from `GestureCatalog.Slots`. **Cycle.** |
| Core.Data | Core.Reset (`ResetCatalog`) | |
| Core.Reset | Core.Data (`GlideDataCategoryCatalog`) | **Cycle.** It already needs a lazy-init workaround: see the comment at `GlideDataCategoryCatalog.cs:125`. |
| Core.Workspace | Core (`ImageNavigator`) | OK |
| Core.Navigation, Updates, Windows | self only | Clean |

### 1.3 Runtime subsystems and who owns them

| Subsystem | Where it lives today | Owner in practice |
|---|---|---|
| Composition root / window | `MainWindow.axaml.cs` plus **31 more partials**: 32 files, **16,523 lines**, about 267 private fields | MainWindow |
| Settings schema | `SettingsCatalog` (Core), `SettingEffectRegistry` (Core), `SettingActionCatalog` (Core), `GlideSettingsState` (App, **308 properties**), `SettingAccessors` (App, 168 rows), `SettingsDefaults`, `SettingsChangeSet`, `ResetCatalog` property-name lists (Core) | Split across 2 projects and about 8 tables |
| Settings persistence/migration | `SettingsStore` (884 lines, static); also writes partial documents for `WindowPlacementService` and `UpdateService` | static |
| Settings apply | `MainWindow.TitleSettings.ApplySettingsFromDialog` → `ApplySettingsVisuals` (128 lines, about 35 Apply/Update calls) | MainWindow |
| Settings UI | `SettingsWindow` (2,997 lines plus 12 partials) | SettingsWindow; it also calls `SettingsStore.Save` itself |
| Actions/commands | `AppActionRegistry` (Core) → `MainWindow.CreateCommandDispatcher` (one large dictionary literal) → `MainWindowCommandDispatcher` (43-line lookup) | MainWindow |
| Hotkeys | `HotkeyCatalog` (Core) → `Glide.Input.InputRouter` → MainWindow key handlers | Mixed |
| Gestures | `GestureCatalog.ResolveAction` (Core), called from `ImageViewport` (64 references), `WindowInWindowOverlayManager`, and 7 MainWindow partials | Scattered |
| Navigation | `ImageNavigator` (Core) ← `NavigationCoordinator` (21-line pass-through) ← `MainWindow.Navigation.cs` (1,802 lines) | MainWindow |
| History | `NavigationHistory` (Core); per-tab instances in `MainWindow._tabHistories`; legacy `BrowserNavigationState.History` in `TabState` | MainWindow |
| Tabs/workspace | `WorkspaceState`/`TabState` (Core, clean); `MainWindow.Tabs.cs` (1,653 lines), `.NewTabs`, `.SideTabs`, `.TabPreview`, `.Sessions`; `TabDragController`, `TabAttachCoordinator` | MainWindow |
| Explorer/browser | `ExplorerModel`, `ExplorerFileOperations`, `ExplorerShell`, `ExplorerHost` (static seams) plus **9 MainWindow partials (about 3,000 lines)**. `BrowserSurface` says: "All behaviour ... lives in MainWindow's Explorer partials" | MainWindow |
| Decode pipeline | `ImageLoadCoordinator` (Imaging) ← `IImageDecoderBackend` (returns Avalonia `Bitmap`) ← `NativeCodecProvider` / P/Invoke `Glide.Native` | Imaging |
| Status bar | `StatusBarGroupCatalog`/`StatusBarAutoHide` (Core, pure); `MainWindow.StatusChrome`/`.StatusLayout`/`.Chrome.UpdateStatusStats` (pull model, called from **46 sites**) | MainWindow |
| Process-wide | `App` statics, `ExternalLaunchBroker` statics, `GlideWindowRegistry`, `UpdateService.Current`, `WindowPlacementService`, `RecentHistoryStore`, `WorkspaceSessionStore`, `ImageSortOrder.Current` | static |

How far shared MainWindow state reaches (partials that reference each symbol): `_settings` 28 of 32, `_diagnostics` 21, `_workspace` 19, `Viewport` 15, `_currentPath` 10, `_navigator` 9, `_overlays` 9, `ApplyStatusVisibility` 9, `_slideshow` 8, `_loader` 7.

---

## 2. Findings

Format: **dependency**, then why it hurts, the decoupling plan, risk, effort (S/M/L), and ordering.

### C1. MainWindow is the god object for every feature (partial-class sprawl)
- **Dependency:** 32 partial files share a single `this`: about 267 private fields and every feature's logic (navigation, tabs, history, Explorer, sessions, slideshow, status bar, overlays, backup, updates, pickers). Any partial can read or write any field. The method list of `MainWindow.Tabs.cs` alone covers tab strip rendering, tooltips, MRU, drag/tear-off with Win32 P/Invoke (`GetCursorPos`, `SetWindowPos`, `SendMessageW`), context menus, new-tab policy, Home recent-history list, window duplication, `HandleEscapeAsync` and `ShowVideoUnavailableAsync`.
- **Harm:** Partials are file organisation only, not encapsulation. A change in one feature can silently break another through shared fields such as `_currentPath` or `_settings`. Nothing can be unit-tested without building a real `MainWindow` (20 test files do: `new MainWindow`). Merge conflicts concentrate here, and AOT trimming and startup cost cover everything.
- **Plan:** Extract feature controllers that own their state and talk to the window through narrow interfaces (`IViewerSurface`, `IChromeHost`, `IDialogService`). Start with the subsystems whose state is already partly in Core: (a) `TabStripController` (rendering, overflow, tooltips, MRU) over `WorkspaceState`; (b) `TabTearOffController`, which absorbs the drag/native-move code and its P/Invoke into `Platform/`; (c) `ExplorerController` (see C6); (d) `NavigationController` (see C5); (e) `StatusBarViewModel` (see C9). MainWindow keeps composition, layout and event wiring only. Target: under 3k lines and under 40 fields.
- **Risk:** High. These are the hottest paths (first-frame and navigation latency are measured), and there are subtle ordering contracts (warm presentation gate, bitmap retirement).
- **Effort:** L, done incrementally, one controller per release.
- **Order:** After C2 (settings snapshot interface) and C12 (test seams). Otherwise every extracted controller needs the full `GlideSettingsState` and reflection-based tests break.

### C2. Settings are read directly everywhere through the mutable `GlideSettingsState` (308-property bag)
- **Dependency:** `_settings.X` appears 565 times across 28 MainWindow partials (`TitleSettings` 126, `Tabs` 70, `Startup` 54, `StatusChrome` 45, `Navigation` 40...). Controls, services and `SettingsWindow` also take the whole state. `GlideSettingsState` is a mutable class in **Glide.App**, so Core and Imaging cannot see it. Imaging gets a hand-built projection (`BuildPerformancePolicy` in `MainWindow.TitleSettings.cs:446`).
- **Harm:** Every feature depends on every setting. You cannot tell which settings a feature reads, so live preview can only work by re-applying everything (C3). Mutability means snapshot discipline (`CloneState`, `KeepRuntimeOwnedValues`, `MergeHydratedPreservingEdits`) has to be enforced by convention.
- **Plan:**
  1. Introduce per-feature read-only option records in Core: `NavigationOptions`, `TabOptions`, `StatusBarOptions`, `SlideshowOptions`, `OverlayOptions`, `ExplorerOptions`, `ImagePerformancePolicy` (exists). Add a mapping `GlideSettingsState → options` in one place (`SettingsProjection`).
  2. Add `ISettingsSource { T Get<T>() where T : IOptionsSlice; event Changed(SettingsDelta) }`, implemented by a `SettingsService` that owns the single live `GlideSettingsState`.
  3. Migrate feature code partial by partial from `_settings.X` to `_navOptions.X`. Each partial is an independent PR.
- **Risk:** Medium. The work is mechanical, but derived/normalised fields (`SettingAccessors.ApplyDerived`) must be projected identically.
- **Effort:** M for the infrastructure, then L for full migration.
- **Order:** First. It unlocks C1, C3, C5, C6, C9.

### C3. Settings apply is a global switchboard; `SettingEffectRegistry` is not used at runtime
- **Dependency:** `ApplySettingsFromDialog` → `ApplyNavigationSortOrder`, `ApplySettingsVisuals` (128 lines calling about 35 subsystems: tab strip rebuild, title action strip, overlay manager, welcome layout, status bar, topmost, gesture resolution, accent, scrollbars...), `UpdateGifStatusUi`, `UpdateService.ApplyTo/SettingsChanged`, `App.PublishCurrentSettings`, `SaveSettingsAndPublish`, `App.UpdateTrayIconState`, plus inline file deletion of the overlay layout. `SettingEffectRegistry` already assigns each setting a `SettingEffectOwner` (Navigation, StatusBar, TabsWorkspace...), but its own doc comment says "migrating existing UI switchboards to consume it is incremental". Its only consumers are diagnostics, `UiDiagnosticCapture` and tests.
- **Harm:** Every preview tick re-runs every subsystem. There is already a special-case fast path (`OnlyTextOverlayStyleDiffers`), which shows the cost. Adding a setting means editing this method, and a forgotten apply call fails silently. Effect ownership is declared in one place and implemented in another, with nothing linking them.
- **Plan:** Compute a `SettingsDelta` (changed ids; `SettingsChangeSet` already diffs properties) → group by `SettingEffectRegistry` owner → dispatch to `ISettingEffectHandler` per owner (one per extracted controller from C1). Keep `ApplySettingsVisuals` as the fallback handler for owners that have not migrated yet. Add a test that every `SettingEffectOwner` with runtime ids has a registered handler.
- **Risk:** Medium. Missed cross-owner dependencies would make a setting fail to apply live. Mitigate by keeping the full re-apply on commit until each owner is verified.
- **Effort:** M.
- **Order:** After C2 step 1. It can then proceed in parallel with C1.

### C4. One setting is registered in about 8 parallel tables, kept honest by scripts and tests
- **Dependency:** Adding a setting touches `SettingsCatalog` (Core), `SettingEffectRegistry` (Core), `SettingActionCatalog.Excluded` or `SettingAccessors` (App), a `GlideSettingsState` property (App), `SettingsStore` migration/clamp code, `SettingsWindow.ReadControls` plus `BuildSettingControlMap`, `SettingsDefaults.KeepRuntimeOwnedValues` (if runtime-owned), and possibly `ResetCatalog`'s **string lists of `GlideSettingsState` property names** (`UpdateCheckerMemoryProperties`, `SavedPerformancePresetProperties`, `StartupSwitchProperties`... in Core). `tools/audit-settings-search.py` and `tools/audit-action-registry.py` exist to catch drift.
- **Harm:** Core knows App's schema only by string, which is a reverse dependency the compiler cannot check. The cost of adding a feature is high, drift is caught late, and the audits are regex tooling instead of types.
- **Plan:** Make the catalog row the single source. Extend `SettingDefinition` with `Owner` (from `SettingEffectRegistry`), `ResetGroup`/`DataCategory`, `RuntimeOwned`, and a typed accessor reference. Move `GlideSettingsState` (the POCO only, no Avalonia) into `Glide.Core.Settings`, so the accessor table and `ResetCatalog` can use `nameof`/lambdas instead of strings. Then generate `SettingEffectRegistry.All`, the `ResetCatalog` property lists and `SettingAccessors` from the catalog, keeping a source-generator option for AOT. Turn the Python audits into xunit tests over the catalog.
- **Risk:** Medium. The JSON property names must stay byte-identical (source-gen `JsonSerializerContext` attributes move with the type).
- **Effort:** M to L.
- **Order:** After C2. Moving `GlideSettingsState` to Core is a prerequisite for the typed accessors and also helps C10.

### C5. Navigation logic lives in UI code; `NavigationCoordinator` is a pass-through
- **Dependency:** `Services/NavigationCoordinator.cs` (21 lines) only forwards to `ImageNavigator`. The real logic lives in `MainWindow.Navigation.cs` (1,802 lines): `NavigateCoreAsync`, rate limiting (`TryAcceptNavigationRateLimit`, `NavigationRateLimitAppliesInCurrentMode`), boundary handling (`RefreshCurrentFolderIndexAtBoundaryAsync`), sibling-folder traversal, prefetch scheduling, folder watch, request epochs (`BeginImageRequest`, `IsRequestCurrent`), alongside surface switching (`ShowHomeSurface`, `ShowBrowserSurface`, `EnsureNativeExplorerHost`). `FullscreenSessionController` is likewise 21 lines of storage. The sort order is global (C7).
- **Harm:** You cannot test navigation semantics (end-of-folder, skip, rate limit, history interaction) without a window. Tests resort to source-text assertions instead, for example `Assert.Contains("_navigator.ReplaceFolderIndex(paths, expectedPath)", source)`.
- **Plan:** Grow `NavigationCoordinator` into a real UI-free `NavigationSession` in Core or a new `Glide.Application` layer. It owns navigator, request epoch, rate limiter, end-of-folder policy (`EndOfFolderPolicy` is already pure), sibling-folder resolution (`SiblingFolderNavigationService`) and prefetch plan (`ImageLoadCoordinator.BuildNeighbourPlan` is already static). It returns `NavigationOutcome` records (Present(path, direction) / Boundary(notice) / RateLimited / FolderChanged). MainWindow only renders outcomes. Inject `IClock`, `IFileSystem` and `NavigationOptions`.
- **Risk:** High (latency-critical path). Keep presentation (bitmap lease, warm gate) in the window and move only decisions.
- **Effort:** L.
- **Order:** After C2 and C7. Before the full C1 extraction of `Navigation.cs`.

### C6. Explorer/browser behaviour lives in 9 MainWindow partials; seams are process-wide statics
- **Dependency:** `MainWindow.Browser`, `.ExplorerInput`, `.ExplorerOps`, `.ExplorerMenus`, `.ExplorerSession`, `.ExplorerLocate`, `.ExplorerClickRename`, `.AltFileManager`, `.Pickers` (about 3,000 lines). `BrowserSurface` is a passive view. External effects go through `ExplorerHost`'s static delegates (`Confirm`, `ShowError`, `RevealInWindows`, `OpenWith`...), `ExplorerFileClipboard.Current` (static settable), `ExplorerFileOperations._recycler`, `ExplorerLocations.QuickAccessOverride`, and `MainWindow.WindowsExplorerLocateLauncher` (static).
- **Harm:** Static seams are shared by every window and every test. Tests must be serialised with `[Collection("ExplorerFileOperations")]`, and two windows cannot have different hosts. The Explorer cannot be reused or tested without a MainWindow (`ExplorerTabHarness` builds the real window and shares it across the collection because construction is slow and "intermittently stalls").
- **Plan:** Create an `ExplorerController` (per tab or per window) that owns `ExplorerModel`, selection, rename, clipboard, file ops and history capture. Constructor-inject an `IExplorerHost` instance (replacing the static `ExplorerHost` delegates), `IExplorerFileClipboard` and `IFileRecycler`. Keep the statics as `Default` only for the composition root. `BrowserSurface` binds to the controller. The test harness constructs the controller with fakes and no window.
- **Risk:** Medium (keyboard/focus routing between viewer and Explorer).
- **Effort:** L.
- **Order:** After C12 (instance seams pattern) and C2. Independent of C5.

### C7. Global mutable sort order (`ImageSortOrder.Current`) set as a side effect of loading settings
- **Dependency:** `Glide.Core/ImageSort.cs:45` holds a static `_current`. `SettingsStore.MigrateFromJson` sets it during load (`SettingsStore.cs:590`), and `MainWindow.ApplyNavigationSortOrder` sets it again. `ImageNavigator`, `TabPreviewCache`, `SiblingFolderNavigationService`, `MainWindow.Tabs` and `MainWindow.Sessions` read it implicitly via `CreateComparer()`.
- **Harm:** A persistence routine mutates domain behaviour. Multi-window or per-tab sort is impossible. Tests must save and restore the global (`ImageSortOrderTests` does). Load order is a hidden contract ("Activate the chosen order before any folder is enumerated").
- **Plan:** Remove the setter. Pass `ImageSortSpec` explicitly: `ImageNavigator(ImageSortSpec)` / `Resort(spec)`, `CreateComparer(spec)` (it already exists). The current spec comes from `NavigationOptions` (C2). Callers that have no options use `ImageSortSpec.Default` explicitly. Also, `SiblingFolderNavigationService.WindowsLogicalStringComparer` duplicates Core's StrCmpLogicalW comparer, so keep one.
- **Risk:** Low.
- **Effort:** S.
- **Order:** First wave. It is small and independent, and it de-risks C5.

### C8. Glide.Imaging depends on Avalonia; the decode contract is an Avalonia `Bitmap`
- **Dependency:** `IImageDecoderBackend.DecodeFull/DecodePreview` return `Avalonia.Media.Imaging.Bitmap`. `ImageLoadResult.Bitmap`, `ImagePerformancePolicy.PreviewInterpolation` (`BitmapInterpolationMode`), `StartupImagePreloader`, `CodecPixelSurface` and `SvgDecoder` all use Avalonia. As a consequence, `Glide.ThumbnailCodecs` (which must be Avalonia-free for the shell extension) cannot reference Imaging and instead **links 18 source files** with `<Compile Include="..\Glide.Imaging\...">`. `BuiltInRasterDecoder` is split into `.Avalonia.cs`/`.Dds.cs`/`.Legacy.cs` partials, and `ManagedCodecSupport` into `.Skia.cs`/`.Host.cs`, to make the linking work. `Glide.Diagnostics` (a CLI self-test) also pulls Avalonia transitively.
- **Harm:** The decode core and caching cannot be tested or benchmarked headless without Avalonia. File-level linking is fragile: a new `using Avalonia` in a linked file breaks only the thumbnail build. The UI toolkit is cemented into the codec boundary that the README calls "permanent".
- **Plan:**
  1. Create a project `Glide.Codecs.Managed` (netstandard-style, no Avalonia) containing the pure decoders now linked into ThumbnailCodecs. Both `Glide.Imaging` and `Glide.ThumbnailCodecs` reference it, and the `<Compile Include>` links go away.
  2. Introduce `IDecodedImage` (or reuse `CodecPixelSurface` as a pixel-buffer abstraction) and a `BitmapInterpolation` enum owned by Imaging. Keep a thin `Glide.Imaging.Avalonia` adapter, either an assembly or a folder isolated by `InternalsVisibleTo`, that converts to `Bitmap` and owns the `AvaloniaImageDecoderBackend` default.
  3. `ImageLoadCoordinator` becomes generic over the decoded handle, or holds `IDecodedImage`. Lease/retire logic stays.
- **Risk:** Medium to high. The zero-copy `CodecPixelSurface → WriteableBitmap` path and lease counting are performance-critical, so benchmark before and after (`tools/benchmark-glide.ps1`).
- **Effort:** Step 1 is M. Steps 2 and 3 are L.
- **Order:** Step 1 can ship any time. Steps 2 and 3 go after C12 (tests no longer need the App).

### C9. Status bar is a pull model wired by 46 manual refresh calls
- **Dependency:** `UpdateStatusStats()` (in `MainWindow.Chrome.cs`) reads `_currentPath`, `_navigator`, `_currentPixelWidth/Height`, `_currentFileSize`, `Viewport` and `_settings` directly. `UpdateStatusStats`, `UpdatePictureCounter` and `ApplyStatusVisibility` are called from 46 sites across 9+ partials (navigation, tabs, settings, GIF, slideshow...).
- **Harm:** Every feature that changes visible state must remember to poke the status bar. Status-layout code (`StatusChrome` 1,098 lines plus `StatusLayout`) cannot be tested on its own.
- **Plan:** Add a `ViewerStatusModel` (an immutable record: path, index/count, dimensions, size, zoom, GIF state, slideshow state) published by the navigation, viewport and GIF controllers through one `StatusChanged` event. Extract a `StatusBarPresenter` that subscribes to it and owns the layout/visibility logic (it already has pure Core helpers: `StatusBarGroupCatalog`, `StatusBarAutoHide`). The 46 call sites shrink to model updates at their source.
- **Risk:** Low to medium (refresh ordering and flicker).
- **Effort:** M.
- **Order:** After C2. It works best after C5, but can start with just a model that MainWindow fills.

### C10. Duplicated state: current image, per-tab history, window registries, settings copies
- **Dependencies:**
  - **Current image** lives in three places: `_currentPath` (assigned in 4 places, read in 10 partials), `_navigator.Current`, and `((ImageTabState)_workspace.Active).Path`.
  - **Per-tab history** lives in three places: `MainWindow._tabHistories` (dictionary keyed by tab id), `TabState.History` (the travelled snapshot), and legacy `BrowserTabState.Navigation` (`BrowserNavigationState.History`), reconciled by `HistoryFor()`/`FromBrowserNavigation()`.
  - **Window registries** exist twice: `GlideWindowRegistry.Windows` and `ExternalLaunchBroker.Windows`/`_activeWindow` (both are lists of `WeakReference<MainWindow>`), plus `UpdateProduction.Visible()`, which enumerates windows a third way.
  - **Settings copies:** the window's `_settings`, `App._currentSettings` (`PublishCurrentSettings`, called from 4+ sites), `SettingsWindow._committed`, `UpdateService`'s live memory (re-applied with `UpdateService.Current?.ApplyTo(_settings)` before every save), and `WindowPlacementService._store` (`ApplyTo` before save). `SettingsWindow` also calls `SettingsStore.Save(_committed)` directly (`SettingsWindow.axaml.cs:1132`), so there are several writers to one file.
- **Harm:** These copies get out of sync (stale counter or title, lost update-checker memory, last-writer-wins on `glide.settings.json`). Every save site must remember the right sequence of `ApplyTo` calls.
- **Plan:**
  - Make `WorkspaceState.Active` the single source for the current path. `_currentPath` becomes a computed property, and `ImageNavigator` is keyed off it.
  - Move `NavigationHistory` ownership into `TabState` or `WorkspaceState` (a Core model keyed by tab id). Migrate legacy `BrowserNavigationState` once at session load.
  - Merge the window registries into one `IGlideWindowRegistry` (instance), used by the broker, tab docking and updates.
  - Create a single `SettingsService` (C2) that owns the live state. Runtime-owned slices (placements, update memory) are sub-stores it composes at save time, so callers never call `ApplyTo`. `SettingsWindow` asks the service to save and never calls `SettingsStore` directly.
- **Risk:** Medium.
- **Effort:** Settings service M; current path S; history M; registry S.
- **Order:** The current-path and registry merges are stage 1. History goes with C5. The settings service is part of C2.

### C11. Process-wide static state across subsystems
- **Dependency:** `App` (12 settable statics: `StartupPath(s)`, `StartupSettingsTask`, `StartupFirstFramePolicy`, `BenchmarkExitAfterFirstFrame`, `TrayIconInstance`, `_currentSettings`...), `ExternalLaunchBroker` (9 static fields including `_state`, `_activeWindow`), `UpdateService.Current`, `WindowPlacementService._store`, `RecentHistoryStore` (8 statics), `WorkspaceSessionStore` (7 statics), `ImageMetadataService.BypassNavigationMetadata`, `PopupCursorGuard`/`ToolTipGuard`/`DialogEscape` registrations, `ImagePerformanceGovernor` (Lazy profile; acceptable), `GlidePerformanceTrace` (acceptable as a tracing sink). Also 42 distinct `*ForTests` static hooks in production code.
- **Harm:** There is hidden temporal coupling (startup order, "publish before read"). Tests need named collections to avoid interference ("UpdateService process-wide state", "RecentHistory", "ShellThumbnailProvider"). It also blocks multiple windows or instances with different config, for example in a test host.
- **Plan:** Introduce a small `GlideAppServices` container (a plain class, AOT-friendly, no DI framework) created in `Program`/`App` and passed to each `MainWindow`. Convert statics one by one into instance services exposed through it: `ISettingsService`, `IRecentHistoryStore`, `IWorkspaceSessionStore`, `IWindowPlacementStore`, `IUpdateService`, `ILaunchBroker`, `IGlideWindowRegistry`. Startup arguments become an immutable `StartupOptions` record. Leave pure caches (`ImagePerformanceGovernor`) static.
- **Risk:** Medium. Startup latency is measured, so services must stay lazy.
- **Effort:** M per service, L in total.
- **Order:** The container goes in stage 1, and services move with their consumers.

### C12. Test fixtures coupled to internals (reflection, source-text scanning, App reference from "Core" tests)
- **Dependency:** `Glide.Core.Tests` references **Glide.App** (93 of 165 files use App types) through `InternalsVisibleTo`. There are 63 `GetMethod/GetField/GetProperty("name")` reflection calls on private members (`ReadControls` ×5, `RunSearchRebuild` ×5, `_settingControls` ×3, `_settings` ×2, `ApplyStatusVisibility` ×2...). At least 13 test files `File.ReadAllText` production `.cs` files and make about 124 `Assert.Contains/DoesNotContain("code text", source)` assertions (`MainWindow.Sessions.cs`, `App.axaml.cs`, `ExternalLaunchBroker.cs`, `BuiltInRasterDecoder.cs`...). 20 test files build a real `MainWindow`.
- **Harm:** Renaming a private method or reformatting a line fails tests. Tests freeze the current structure, so they actively resist the decoupling above. The project named "Core.Tests" is in practice an App integration suite, so Core changes rebuild App.
- **Plan:**
  1. Split the test projects: `Glide.Core.Tests` (Core and Input only, no App reference), `Glide.Imaging.Tests`, and `Glide.App.Tests` (headless UI and integration).
  2. For each reflection target, add an `internal` test-facing API on the extracted controller, or test the new controller directly.
  3. Replace source-text assertions with behaviour tests as each area is extracted. Keep a small, explicitly named `ArchitectureContractTests` for the few genuine "must never call X" rules, ideally expressed over types, for example "Glide.Core has no Avalonia reference" and "Imaging.Codecs has no Avalonia".
- **Risk:** Low for production. It is a test-only change.
- **Effort:** Step 1 M, steps 2 and 3 ongoing.
- **Order:** Step 1 in stage 1. It makes every later stage cheaper.

### C13. Gesture resolution is scattered across control, overlay manager and window
- **Dependency:** `GestureCatalog.ResolveAction` plus the `Default/PassThrough/Nothing/ContextMenu/Pan/MoveWindow...` switch is implemented inside `ImageViewport` (64 references; it receives `GestureBindings`/`CustomGestures` pushed in), inside `WindowInWindowOverlayManager`, and in MainWindow `.Tabs`, `.Browser`, `.ExplorerInput`, `.Slideshow`, `.StatusChrome`, `.Navigation`, `.Startup` and `.Commands`. The wheel logic in `ImageViewport` also embeds slideshow and right-held overrides.
- **Harm:** One user-facing concept ("what does this gesture do here") has about 10 implementations. Context precedence (slideshow over area over default) is duplicated and drifts. The control knows application policy.
- **Plan:** Add a Core `GestureResolver` that takes `(slotId, GestureContext{area, mode: slideshow/fullscreen/selection, modifiers}, bindings)` and returns `GestureDecision{ ActionId | Builtin(Pan/Zoom/...) | PassThrough }`. Use `Glide.Input` (currently a 71-line `InputRouter`) as its home, so hotkeys and gestures share one routing layer. `ImageViewport` raises raw `GestureInput` events, or consults an injected `IGestureResolver`, and executes only built-in viewport behaviours.
- **Risk:** Medium (input regressions are very visible). `Glide.Input.Tests` already exists to cover the logic.
- **Effort:** M.
- **Order:** After C2 (gesture options slice). Independent of C5 and C6.

### C14. Commands invoke UI event handlers; the dispatcher table is built inside the window
- **Dependency:** `CreateCommandDispatcher` maps `GlideCommand` to lambdas over MainWindow methods. 9 of them call **click handlers with a fake `RoutedEventArgs`** (`Event(OpenClicked)`, `Event(SettingsClicked)`, `Event(InfoClicked)`, `Event(BrowserBackClicked)`...). Several inline domain logic (RefreshFolder branching on tab kind). The table is an object initialiser with up to about 10 entries per physical line.
- **Harm:** The real behaviour lives in UI event handlers, so commands depend on UI controls existing. The `AGENTS.md` rule "register an AppAction ... and its handler in CreateCommandDispatcher" funnels every new feature into MainWindow, which reinforces C1.
- **Plan:** Invert it with `ICommandHandler` registrations. Each extracted controller registers its own `GlideCommand`s with the dispatcher (`dispatcher.Register(GlideCommand.NextImage, nav.NextAsync)`). Click handlers call the command, not the reverse. Keep `MainWindowCommandDispatcher.AssertComplete` as the completeness check. Update `docs/ACTIONS-AND-HOTKEYS.md` and `AGENTS.md` when the recipe changes.
- **Risk:** Low (mechanical, and AssertComplete guards it).
- **Effort:** S for the click-handler inversion, then M as controllers appear.
- **Order:** The handler inversion is stage 1. Moving registrations happens alongside C1.

### C15. Core namespace cycles (Commands ↔ Settings, Data ↔ Reset)
- **Dependency:** See §1.2. `SettingsCatalog` builds rows from `GestureCatalog`, while `AppActionRegistry` builds actions from `SettingsCatalog`. `GlideDataCategoryCatalog` and `ResetCatalog` read each other, and the type-initialiser ordering is already worked around with lazy init. `SettingEffectRegistry`'s static constructor throws if validation fails, so a catalog error becomes a `TypeInitializationException` wherever the type is first touched.
- **Harm:** Static-initialiser cycles are a latent crash source. The namespaces cannot be split into assemblies later.
- **Plan:** Introduce a composition step `GlideCatalogs.Build()` that builds gesture slots, then settings, then generated actions, then effect bindings in one explicit order and exposes immutable results. Validation runs in tests and diagnostics, not in a static constructor. Data/Reset: move the shared category enum and capability flags to `Core.Data`, and have `ResetCatalog` depend one-way on it.
- **Risk:** Low.
- **Effort:** S to M.
- **Order:** Stage 1. It is independent and simplifies C4.

### C16. Format/extension knowledge duplicated across 6 files and 3 projects
- **Dependency:** Extension lists live in `ImageFormatRegistry`, `CodecCapability`, `SaveFormats` (Core), `BuiltInRasterDecoder`, `RawPreviewDecoder` (Imaging), `BundledCodecLibraries.BundledExtensions/ManagedExtensions` (App), and `ThumbnailCodecs/Exports.cs`. The comments say "ThumbnailCoverageTests checks the two agree".
- **Harm:** Adding a format means editing several lists, and agreement is enforced only by tests.
- **Plan:** Make `ImageFormatRegistry` (already linked into ThumbnailCodecs) the single table, with columns for backend (built-in/managed/bundled native/WIC), save support and thumbnail support. Derive the other lists from it.
- **Risk:** Low.
- **Effort:** S to M.
- **Order:** Any time. Natural to do with C8 step 1.

### Lower-priority notes
- `Glide.Core/ImageSort.cs` P/Invokes `shlwapi.dll` (`StrCmpLogicalW`). Core is supposed to be headless and portable. Move it behind an `INaturalStringComparer` with the Windows implementation in App/Platform, or keep the managed `NaturalStringComparer` already present. Effort S.
- P/Invoke is sprinkled directly in `MainWindow.axaml.cs`, `.Commands`, `.Tabs` and `.ViewerChrome` (user32 drag/move loops). Move it to `Platform/` wrappers so the window does not own Win32. Effort S, do it with C1.
- `SettingsWindow` (2,997 lines plus 12 partials) reaches into `WindowsStartupRegistration`, `WindowsFileAssociationRegistration`, `ThumbnailSettingsStore`, `ResetCoordinator`, `ProfileStore` and `GlideBackupService` directly, and calls `SettingsStore.MigrateLegacyMouseToggles/MigrateDefaultsSchema8` for profile import. Route profile import through `SettingsStore.MigrateFromJson` (one migration entry point), and route OS side effects through an `ISystemIntegration` service. Effort M.
- `ImageLoadCoordinator.IsBitmapInActiveUse` (a callback from the cache into the UI's view of usage) is an acceptable inversion, but document it as part of the lease contract when doing C8.
- `_diagnostics` is used in 21 partials. This is acceptable cross-cutting logging. Keep it as an injected `IDiagnosticSink` when extracting controllers.

---

## 3. What is already good (keep and build on)
- Glide.Core has **no Avalonia dependency**, and its README states the invariant. `WorkspaceState`/`TabState`, `NavigationHistory`, `EndOfFolderPolicy`, `StatusBarAutoHide`, `OverlayCollisionPolicy`, `TabDragPolicy` and `FullscreenTitleBarPolicy` are pure and tested. Extracted controllers can wrap them directly.
- `AppActionRegistry` is a genuine single source for hotkeys, gestures, menus, title-bar buttons and search, and `MainWindowCommandDispatcher.AssertComplete` guarantees coverage.
- `SettingEffectRegistry` already encodes the ownership map that C3 needs. Only the runtime consumer is missing.
- `IImageDecoderBackend` / `IPathOptimizedImageDecoderBackend` are the right seams. They just need to stop speaking `Bitmap`.

---

## 4. Staged decoupling roadmap

Each stage is independently shippable, keeps behaviour identical, and lands with its own tests. Perf-sensitive stages (marked ⚡) must be checked with `tools/benchmark-glide.ps1` / `benchmark-visible-first-pixel.ps1` before and after.

### Stage 1: Seams and hygiene (S/M, low risk)
1. **C12.1** Split test projects: `Glide.Core.Tests` without App, `Glide.App.Tests` for the rest. Add `ArchitectureContractTests`: Core has no Avalonia, no `Glide.App` types are referenced from Core/Imaging/Input, and a ThumbnailCodecs-linked file has no `using Avalonia`.
2. **C7** Remove `ImageSortOrder.Current`. Pass `ImageSortSpec` explicitly and delete the side effect in `SettingsStore`. Tests: navigator sorts per spec, and settings load does not mutate globals.
3. **C15** Explicit catalog build order. Move validation out of static constructors and into tests and diagnostics.
4. **C14 (part)** Stop invoking click handlers from commands. Extract `OpenFileAsync()`, `OpenSettingsAsync()` and similar, and have handlers call them. `AssertComplete` stays green.
5. **C10 (part)** Merge `GlideWindowRegistry` and the broker's window list into one instance registry. Make `_currentPath` derive from `WorkspaceState.Active`.
6. **C11 (part)** Introduce `GlideAppServices` and `StartupOptions` (a record replacing the `App.Startup*` statics). No behaviour change.

### Stage 2: Settings boundary (M, medium risk)
1. **C2.1/C2.2** `SettingsService`, the single owner of live `GlideSettingsState`. It composes the placement and update-memory sub-stores at save, so `ApplyTo` disappears from call sites. `SettingsWindow` saves via the service. Option slices: `NavigationOptions`, `TabOptions`, `StatusBarOptions`, `ExplorerOptions`, `GestureOptions`, `SlideshowOptions`, `OverlayOptions`. Tests: projection equals the current derived values for every preset in `SettingsPresetCatalog`.
2. **C3** `SettingsDelta` → owner dispatch, with `ApplySettingsVisuals` as the fallback handler. Tests: each owner's ids reach its handler, and a no-op change triggers nothing.
3. **C2.3** Migrate partials from `_settings.X` to slices, one PR per partial, starting with the least-coupled ones (`EndOfFolder`, `NavigationSkipHint`, `History`, `TabPreview`).

### Stage 3: Single-source settings schema (M/L, medium risk)
1. Move the `GlideSettingsState` POCO and its JSON context into `Glide.Core.Settings`. Add a byte-compat test: round-trip a corpus of historical settings files and require identical JSON.
2. **C4** Extend `SettingDefinition` with owner, reset group and runtime-owned flag. Generate `SettingEffectRegistry`, the `ResetCatalog` property lists and `SettingAccessors`. Port the two Python audits to xunit tests.
3. One migration entry point. `SettingsWindow` profile import uses `SettingsStore.MigrateFromJson`.

### Stage 4: Feature controllers out of MainWindow (L, high risk, one controller per release) ⚡
Order by risk, lowest first. Each controller registers its own commands (C14) and settings-effect handler (C3), and gets controller-level tests without a window.
1. **C9** `ViewerStatusModel` and `StatusBarPresenter`, replacing the 46 manual refresh calls.
2. **C13** `GestureResolver` in Glide.Input. `ImageViewport` and the overlay manager consume it.
3. **C1a/b** `TabStripController` plus `TabTearOffController` (with P/Invoke moved to `Platform/`).
4. **C6** `ExplorerController` with an instance `IExplorerHost`/clipboard/recycler. Explorer tests run on the controller, and the static `ExplorerHost` is removed.
5. **C10 history** History moves into the workspace model keyed by tab id. Legacy `BrowserNavigationState` is migrated at session load.
6. **C5** `NavigationSession` (decisions only; presentation stays in the window) ⚡.

### Stage 5: Imaging independence (M/L, medium-high risk) ⚡
1. **C8.1 and C16** `Glide.Codecs.Managed` project (no Avalonia) replaces the 18 `<Compile Include>` links. `ImageFormatRegistry` becomes the single format table.
2. **C8.2/C8.3** `IDecodedImage` and an Imaging-owned interpolation enum. The Avalonia adapter is isolated, `ImageLoadCoordinator` is Avalonia-free, and `Glide.Imaging.Tests` runs headless.

### Stage 6: Statics and test debt cleanup (ongoing, low risk)
1. **C11** Convert the remaining static stores (`RecentHistoryStore`, `WorkspaceSessionStore`, `WindowPlacementService`, `UpdateService.Current`, `ExternalLaunchBroker`) to instance services in `GlideAppServices`, and delete the matching `*ForTests` static hooks and test collections.
2. **C12.2/C12.3** Retire reflection and source-text assertions as each area gets controller-level tests. Track the count; the current baseline is 63 reflection calls and about 124 source-text assertions across at least 13 files.
3. Update `AGENTS.md` and `docs/ACTIONS-AND-HOTKEYS.md` so the "new feature" recipe points to the owning controller instead of `CreateCommandDispatcher` in MainWindow.

### Dependency of stages
```
Stage 1 ──► Stage 2 ──► Stage 3
   │            │
   │            └──► Stage 4 (C9, C13 first; C5 last)
   └──► Stage 5.1 (independent)  ──► Stage 5.2 (after C12.1)
Stage 6 runs continuously after Stage 1.
```
