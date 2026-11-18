# Glide architecture map

Where to start for each subsystem. Paths are relative to `src/` unless noted; test names are files in
`tests/Glide.Core.Tests/` unless noted. Keep this one screen long: add a row, not prose.

## Projects

| Project | Role |
|---|---|
| `Glide.Core` | UI-free policies and catalogues: settings, actions/hotkeys/gestures, navigation, workspace, reset, updates |
| `Glide.Imaging` | Decoders, encoders, `ImageLoadCoordinator` scheduling, codec providers, video probe |
| `Glide.App` | Avalonia app: `MainWindow` (32 partials), `SettingsWindow` (14 partials), controls, Win32 platform code |
| `Glide.Input` | `InputRouter` (one file) |
| `Glide.Diagnostics` | `DiagnosticRunner` structural self-test (`--diagnostics self-test`, run by build.cmd), live trace |
| `Glide.ThumbnailCodecs` | NativeAOT thumbnail codec DLL; links 18 `Glide.Imaging` decoder files |
| `native/` | C++: `Glide.Native` (WIC/JPEG/video bridge), `Glide.Codecs`, `Glide.ShellThumbnail` (Explorer provider) |
| `tests/` | `Glide.Core.Tests` (mostly App UI tests, headless Avalonia), `Glide.Explorer.Tests`, `Glide.Input.Tests` |

## Subsystems

| Subsystem | Start here | Also | Tests | Docs |
|---|---|---|---|---|
| Actions / hotkeys / gestures | `Glide.Core/Commands/AppActionRegistry.cs` | `MainWindow.Commands.cs` (`CreateCommandDispatcher`), `HotkeyCatalog.cs`, `GestureCatalog.cs`, `Services/MainWindowCommandDispatcher.cs`, `Settings/SettingsWindow.Hotkeys.cs`, `.Gestures.cs` | `AppActionRegistry*`, `Hotkey*`, `Gesture*`, `MainWindowCommandDispatcherCoverage*` | `docs/ACTIONS-AND-HOTKEYS.md` |
| Settings | `Glide.Core/Settings/SettingsCatalog.cs` | `SettingEffectRegistry.cs`, `SettingActionCatalog.cs`, `Glide.Core/Settings/GlideSettingsState.cs`, `SettingsOptionSlices.cs`, `SettingAccessors.cs` (id <-> property table), `SettingsService.cs` (live state, save), `SettingsDelta.cs`, `SettingsDefaults.cs`, `SettingsStore.cs`, `SettingsWindow.axaml(.cs)`, `MainWindow.TitleSettings.cs` (apply), `MainWindow.SettingsEffects.cs` (owner routing) | `Settings*`, `RestoreDefaults*` | AGENTS.md checklist |
| Image loading / decode | `Glide.Imaging/ImageLoadCoordinator.cs` | `IImageDecoderBackend.cs`, `BuiltInRasterDecoder*.cs`, `*Decoder*.cs`, `Glide.Core/ImageFormatRegistry.cs`, `CodecCapability.cs` | `ImageLoad*`, `*Decoder*`, `ImageFormatRegistry*`, `ManagedFormatRouting*` | `docs/format-coverage.md` |
| Saving / encoders | `Glide.App/Services/ImageSaveService.cs` | `Glide.Imaging/Encoders/*`, `Glide.Core/SaveFormats.cs`, `ImageEditTransform.cs` | `ImageSave*`, `ImageEncoder*`, `MultiFrameSaveGuard*` | |
| Viewport / zoom / pan | `Controls/ImageViewport.cs` | `MainWindow.ViewerChrome.cs`, `Glide.Core/ZoomStepPolicy.cs` | `Viewport*`, `WheelDirection*` | |
| Tabs / sessions | `MainWindow.Tabs.cs`, `MainWindow.Sessions.cs` | `.SideTabs.cs`, `.NewTabs.cs`, `.TabPreview.cs`, `Glide.Core/Workspace/*`, `Services/TabDragController.cs` | `Tab*`, `Workspace*`; Explorer project `Tab*` | |
| Explorer tab | `MainWindow.Browser.cs`, `MainWindow.Explorer*.cs` | `Explorer/*`, `Controls/BrowserSurface.axaml`, `Platform/WindowsBrowserFrameController.cs` | `Explorer*`; `tests/Glide.Explorer.Tests` | |
| Folder navigation / history | `MainWindow.Navigation.cs`, `MainWindow.History.cs` | `.EndOfFolder.cs`, `.NavigationSkipHint.cs`, `Glide.Core/Navigation/*`, `Glide.Core/ImageNavigator.cs`, `ImageSort.cs` | `Navigation*`, `EndOfFolder*`, `HierarchicalFolder*`, `CrossTabHistory*` | `docs/NAVIGATION-HISTORY.md` |
| Status bar / title bar / chrome | `MainWindow.StatusChrome.cs`, `MainWindow.StatusLayout.cs`, `MainWindow.Chrome.cs` | `Glide.Core/StatusBarAutoHide.cs`, `StatusBarGroupCatalog.cs`, `Glide.Core/Settings/TitleBarButtonCatalog.cs` | `StatusBar*`, `StatusChrome*`, `TitleBar*` | |
| Fullscreen / slideshow | `Services/FullscreenSessionController.cs`, `MainWindow.Slideshow.cs` | `FullscreenTransitionPlanner.cs`, `SlideshowSessionController.cs`, `Glide.Core/Fullscreen*.cs` | `Fullscreen*` | |
| Window-in-window overlays / video | `Controls/WindowInWindowOverlayManager.cs` | `NativeOverlayWindowRenderer.cs`, `MainWindow.Overlays.cs`, `Glide.Core/Overlay*.cs`, `Glide.Imaging/NativeVideoPlayer.cs` | `Overlay*`, `Video*` | |
| Print | `Printing/PrintWindow.cs` | `PrintService.cs`, `PrintLayoutEngine.cs`, `PrintPreviewControl.cs` | `Print*` | `docs/print-research.md` |
| Reset / backup | `Glide.Core/Reset/ResetCoordinator.cs` | `Glide.Core/Data/*`, `Settings/GlideBackupService.cs`, `SettingsWindow.Reset.cs`, `.Backup.cs` | `Reset*`, `Backup*`, `GlideBackup*` | |
| Updates | `Updates/UpdateService.cs` | `Glide.Core/Updates/*`, `MainWindow.Updates.cs` | `Update*`, `GitHubUpdateFeed*` | |
| Startup / single instance / launch | `Program.cs`, `MainWindow.Startup.cs` | `Services/ExternalLaunchBroker.cs`, `Services/LaunchElection.cs`, `Services/StartupTrace.cs` (always-on `startup-trace.log`), `StartupWorkspacePlanner.cs`, `StartupPathQueue.cs` | `ExternalLaunchBroker*`, `SilentExitRegression*`, `LaunchDecisionLog*` | |
| Windows integration | `Platform/*` | file associations, Run key, taskbar identity, shell thumbnails registration | `FileAssociation*`, `ThumbnailRegistration*`, `Taskbar*` | `docs/explorer-thumbnails.md` |
| Native bridge / thumbnails | `native/Glide.Native`, `native/Glide.ShellThumbnail` | `native/Glide.Codecs`, `tools/build-codecs.ps1` | `ShellThumbnailProvider*`, `JpegTurboParity*` | `docs/explorer-thumbnails.md`, `codecs/README.md` |
| Build / release | `build.cmd` (log: `build-output.txt`) | `tools/quick-check.ps1`, `tools/build-installer.cmd`, `Directory.Build.props` | | `docs/releases/`, `CONTRIBUTING.md` |

## Cross-cutting facts

- `MainWindow` partials share all fields; there is no encapsulation between them (see `docs/audits/audit-coupling.md`).
- Many tests read production source text (`SourceText.cs`) or reflect into private members by name: a rename can
  fail tests at runtime only. Run the relevant test area after renames.
- Headless Avalonia tests share one fixture per test assembly (`AvaloniaHeadlessFixture.cs`, `ExplorerTestApplication.cs`).
- `Archive/` holds old backup copies: never edit or search it (excluded by `.ignore`).
