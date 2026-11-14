# Project instructions

- New feature = register an AppAction in `src/Glide.Core/Commands/AppActionRegistry.cs` and its handler in `CreateCommandDispatcher`; hotkeys, gestures, menus, title-bar buttons and Settings search read from it. Recipe: `docs/ACTIONS-AND-HOTKEYS.md`; run `python tools/audit-action-registry.py`.
- New setting = `SettingsCatalog` entry (Toggle/Choice settings are hotkey-bindable by default: add a `SettingAccessors` entry, or an exclusion reason in `SettingActionCatalog.Excluded`).
- Every new user-facing setting or configurable feature must be discoverable through Settings search. Add its metadata to `SettingsCatalog` and connect its control in `BuildSettingControlMap`; include useful search terms and the correct category. Run `python tools/audit-settings-search.py` when changing settings. If a control is intentionally excluded, document why in that audit.
