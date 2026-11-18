# Project instructions

Map of subsystems, files and tests: `docs/ARCHITECTURE.md`. Never edit or search `Archive/` (old copies; excluded by `.ignore`). Read only the top section of `CHANGELOG.md`. The folder is a local git repo: use `git diff` to review your change.

## Commands
- Inner loop: `powershell -NoProfile -File tools\quick-check.ps1 [-Filter <dotnet test filter>] [-Project Core|Explorer|Input]` (Debug compile errors-only + all audits + optional filtered tests; ~10 s incremental).
- Before handing over: `build.cmd --no-pause` (Release build, Core + Input + Explorer tests, diagnostics self-test, publish). Last log: `build-output.txt`.
- Audits: `python tools/audit-settings-search.py`, `python tools/audit-action-registry.py`, `python tools/audit-setting-wiring.py [id ...]` (names the missing pieces per setting id).
- Many tests assert on production source text or reflect into private members by name: after a rename, run the matching test area.

## Add an action (hotkey / gesture / menu / title-bar button)
Register an AppAction in `src/Glide.Core/Commands/AppActionRegistry.cs` and its handler in `CreateCommandDispatcher` (`MainWindow.Commands.cs`); hotkeys, gestures, menus, title-bar buttons and Settings search read from it. Recipe: `docs/ACTIONS-AND-HOTKEYS.md`. Run `python tools/audit-action-registry.py`.

## Add a setting (every file that needs a line)
1. `src/Glide.Core/Settings/SettingsCatalog.cs`: entry with id, category (= Settings page title), label, description, kind, default, useful search terms. Windows-only (shell/registry/startup)? add the id to `WindowsOnlyIds`.
2. `src/Glide.Core/Settings/GlideSettingsState.cs`: property with the same default as the catalogue (a test checks toggles).
3. `src/Glide.App/Settings/SettingAccessors.cs`: one row. Toggle/Choice are hotkey-bindable by default: `Bool(...)`/`Text(...)` in `All` (a Choice also needs its option list in `SettingActionCatalog.Choices`, same order as the combo box). Not bindable: an exclusion reason in `SettingActionCatalog.Excluded` and a `Flag`/`Int`/`Double`/`Str` row in `NonBindableRows`. This table is how changes are detected and routed.
4. `SettingsWindow.axaml`: the control inside the right `SettingsCard`; `SettingsWindow.axaml.cs`: `M("id", Control)` in `BuildSettingControlMap` (Settings search). A plain CheckBox/ToggleSwitch is then loaded and read automatically (`LoadMappedToggles`/`ReadMappedToggles`); only combos, numbers, text and derived values need a line in `LoadControls` and `ReadControls`.
5. The consuming code (usually a `MainWindow.*.cs` partial, reading a slice such as `NavigationOptionsView`; add a member to `src/Glide.Core/Settings/SettingsOptionSlices.cs` if needed). Live apply: nothing to do (the full re-apply runs), or add the id to its owner's narrow handler in `MainWindow.SettingsEffects.cs`.
6. Only when needed: `SettingEffectRegistry.cs` exception (owner or live/commit semantics differ from the id prefix; rows are derived), `SettingsStore.cs` normalisation/migration of Choice/Number values.
7. Run `python tools/audit-setting-wiring.py <id>`, `python tools/audit-settings-search.py`, `python tools/audit-action-registry.py`. If a control is intentionally not searchable, document why in `audit-settings-search.py`.
Saving: never call `SettingsStore.Save`; use `SettingsService.Save` (MainWindow: `SaveSettingsAndPublish`).
