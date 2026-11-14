# Actions, hotkeys, gestures and menus

Glide has **one master list of everything a user can do**: `src/Glide.Core/Commands/AppActionRegistry.cs`.
Hotkeys, mouse gestures, Settings search, title-bar buttons, status-bar buttons and context menus all read from it.
Nothing that a user can trigger is wired by hand anywhere else.

## The registry in one screen

| Piece | File | Role |
|---|---|---|
| `GlideCommand` | `Glide.Core/Commands/GlideCommand.cs` | Stable numeric command ids (saved in old profiles; **append only**). |
| `AppActionRegistry.All` | `Glide.Core/Commands/AppActionRegistry.cs` | The master list. One `AppAction` per function. |
| `HotkeyCatalog.All` / `.FixedShortcuts` | `Glide.Core/Commands/HotkeyCatalog.cs` | Hotkey rows **derived** from the registry (+ read-only fixed shortcuts). |
| `GestureCatalog.Actions` | `Glide.Core/Commands/GestureCatalog.cs` | Gesture picker actions **derived** from the registry. |
| `SettingActionCatalog` | `Glide.Core/Settings/SettingActionCatalog.cs` | Which settings get a "Toggle:" / "Cycle:" action, option lists, documented exclusions. |
| `ShortcutText` | `Glide.Core/Commands/ShortcutText.cs` | Canonical shortcut text; every comparison of key strings goes through it. |
| `InputRouter.ResolveShortcutAction` | `Glide.Input/InputRouter.cs` | Pressed key (canonical) -> hotkey row -> registry action. |
| `MainWindow.CreateCommandDispatcher` | `Glide.App/MainWindow.Commands.cs` | The **one place** where command handlers are registered. |
| `MainWindow.ExecuteActionAsync` | `Glide.App/MainWindow.Commands.cs` | The single execution entry point (context gate, setting toggles, commands). |
| `SettingAccessors` | `Glide.App/Settings/SettingAccessors.cs` | Typed, AOT-safe get/set for every bindable setting. |
| `TitleBarButtonCatalog` | `Glide.App/Settings/TitleBarButtonCatalog.cs` | 22 historical button ids + every registry command with an `Icon` (5.0.0 default starts with Back / Forward). |
| `NavigationHistory` | `Glide.Core/Navigation/NavigationHistory.cs`, `Glide.App/MainWindow.History.cs` | Per-tab browser-style Back / Forward (see `docs/NAVIGATION-HISTORY.md`). |
| `MenuActions` | `Glide.App/Services/MenuActions.cs` | Tags every context-menu item with its registry action. |
| `StatusButtonActionIds` | `Glide.App/MainWindow.StatusLayout.cs` | Every status-bar button -> registry action. |

An `AppAction` has: a stable string `Id`, `Label`, `Category`, `Command`, `Kind`
(`Command`, `SettingToggle`, `SettingCycle`, `GestureOnly`, `FixedShortcut`), `Description`, `SearchTerms`,
`Inputs` (hotkey / gesture / title bar / status bar / context menu), `Contexts` (Viewer / Home / Explorer / Overlay),
`HotkeySlots` (hotkey rows with default keys), `Gesture` (gesture id, areas, input kinds), `SettingId`,
`FixedShortcut`, `AliasOf`, `Destructive`, `Icon`.

Key flow: `KeyDown` -> `KeyboardShortcutText.From` -> `InputRouter.ResolveShortcutAction` (canonical compare) ->
`MainWindow.RouteHotkeyAsync` -> `ExecuteActionAsync` -> context gate -> command dispatcher / `ChangeSetting`.

## How to add a feature (the recipe)

1. **Command.** Append a value to `GlideCommand` (never insert or renumber).
2. **Registry entry.** Add one line to `CommandActions()` in `AppActionRegistry.cs`:
   ```csharp
   Cmd("view.sharpen", "Sharpen view", "Viewing", GlideCommand.SharpenView, [], ["sharpen", "detail"]) with { Icon = "Fit" },
   ```
   - default keys: leave `[]` unless the owner chose one; write them canonically (`Ctrl+Shift+Alt+Key`).
   - add the id to `ViewerOnlyIds()` when it works on the current image (it must not act on a hidden image).
   - `Destructive = true` keeps it out of gestures; `Inputs = NoGesture` for dialog / file-writing commands.
   - `Icon = "..."` (a `GlideIconView` kind) offers it as a title-bar button automatically.
3. **Handler.** Register it in `CreateCommandDispatcher` (`MainWindow.Commands.cs`). Never map a command to a bare
   `Task.CompletedTask`: the build's guard tests fail on placeholders.
4. **Buttons and menus.** A menu item uses `MenuActions.Tag(item, "<action id>")` and executes through
   `ExecuteActionAsync`; its shortcut text comes from `ShortcutForAction`. A status-bar button gets an entry in
   `StatusButtonActionIds`.
5. **Done.** The feature now appears in: the hotkey list (unassigned), Settings search, the gesture picker
   (click rows, unless destructive / `NoGesture`), the title-bar customizer (if it has an icon).

## How to add a setting

1. Add the `SettingsCatalog` entry and its control in `BuildSettingControlMap` (see `AGENTS.md`), then run
   `python tools/audit-settings-search.py`.
2. A `Toggle` or `Choice` setting is **hotkey- and gesture-bindable by default** ("Toggle: <label>" /
   "Cycle: <label>"). Then either
   - add its accessor to `SettingAccessors.cs` (and, for a Choice, its option list to
     `SettingActionCatalog.Choices` in the exact Settings combo order), **or**
   - list it in `SettingActionCatalog.Excluded` with a reason (OS registration, multi-value staging, needs a path...).
3. Setting actions change the value exactly like Settings > Apply: clone the state, set the value,
   `SettingAccessors.NormalizeDerived`, `ApplySettingsFromDialog(state, commit: true)` (applies live, saves, publishes).
   If the setting has a derived field written in `SettingsWindow.ReadControls`, mirror it in `NormalizeDerived`.

## Fixed shortcuts

Keys owned by one surface that are not rebindable (Explorer list keys, Space pauses a running slideshow, overlay
keyboard zoom, Esc while drawing an overlay area) are registry entries of kind `FixedShortcut`. Since 5.0.0 Alt+Left /
Alt+Right are no longer fixed Explorer keys (they are the editable defaults of Back / Forward), Alt+Up stays the fixed
"Up one folder". Backspace is not a fixed key any more: it is the editable default of "Back, or Previous image"
(`nav.backOrPrevious`), which also covers the Explorer list (the old fixed entry `fixed.explorer.backspace` is gone, so
Backspace has one owner). They are listed
read-only in the hotkey list, found by Settings search and by "Search hotkey", and Explorer's context menu reads its
shortcut column from them. Code that handles such keys must be on the allow-list in
`HotkeyEndToEndTests.Hardcoded_key_handling_only_exists_in_allow_listed_places`, with the reason.

## Actions on "the image" in an Explorer tab

Viewer-only actions are blocked in Explorer / Home tabs (`IsActionAvailableHere`). *Open in external program 1-3* are the
exception: they work in image **and** Explorer tabs, and `MainWindow.LaunchExternal` (the one dispatcher handler every
hotkey, gesture, title-bar button and menu reaches) picks the image per tab. Image tab: the current image. Explorer tab:
the image currently selected in the list (folders and unsupported files do not count; with several selected images the
focused one if it is selected, else the first in list order); nothing selected: nothing is launched and the Explorer
message line shows "Select an image first". Home tab: nothing. It never falls back to another tab's picture, the last
opened image or an earlier selection. Tests: `ExternalProgramExplorerTests` (Glide.Explorer.Tests).

## Compatibility rules

- `GlideCommand` numeric values, hotkey row ids (`HotkeyCatalog.LegacyIds`, 75) and gesture action ids
  (`GestureCatalog.LegacyActionIds`, 56) are persisted: never rename or remove them. The tests compare them with a
  verbatim copy of the pre-registry tables.
- Saved shortcut strings are compared canonically, so "Alt+Shift+S" in an old settings file and the pressed
  "Shift+Alt+S" are the same key. Defaults must be written canonically (guard test).
- A changed default shortcut needs a one-time migration, because a settings file stores the resolved shortcut of every
  action (default and user choice look the same). `HotkeyDefaultMigrations` / `SettingsStore.MigrateDefaultsSchema8`
  (settings schema 8; schema 9 adds the Back / Forward title-bar buttons and keys) moved "Show / hide status bar" (`status.toggle`) from Alt+Shift+S, the Windows input-language
  chord, to **Ctrl+Shift+B**: only a file older than schema 8 whose binding is exactly the old default (either spelling)
  moves; any other binding (rebound, unassigned, extra keys), and any file saved at schema 8 or later, is kept; nothing
  moves if another action already uses Ctrl+Shift+B. Known limit: someone who deliberately chose exactly Alt+Shift+S
  before schema 8 is indistinguishable from the old default and moves too (one rebinding restores it).
  Tooltips read the live binding (`ShortcutFor`), never a hard-coded key.
- Mouse side buttons (XButton1 back / XButton2 forward) are gesture inputs (`GestureInputs.BackButton` / `ForwardButton`,
  slots `windowed.` / `fullscreen.backButtonImage`, `tabs.backButton`, `explorer.backButton` and the matching forward
  slots), not hotkeys: the hotkey map never stores mouse buttons. `MainWindow.TryRouteSideButtonGesture` maps a press to
  the area under the pointer (the Welcome page follows the Windowed image rows) and runs the bound action. The
  default is **Back / Forward** (`GestureCatalog.DefaultActionFor`, "Default (back)" / "Default (forward)"); the first
  5.0 build left the press untouched. "Do nothing" or any other bound action (row or custom gesture) wins.
- Back / Forward (`nav.back` Alt+Left, `nav.forward` Shift+Backspace / Alt+Right) and "Back, or Previous image" (`nav.backOrPrevious`, Backspace, command `GlideCommand.BackOrPrevious`, appended last) are new hotkey rows. The composite runs Back when the tab can go back, else Previous image in an image tab, else nothing; `nav.back` is strict and never falls back; `nav.previousBack` ("Previous image (extra key)", legacy id kept) has no default key. Settings schema 11 (`HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious`) moves Backspace once when `nav.previousBack` is bound to exactly Backspace and no other action uses it; rebound / removed / taken keys and schema >= 11 files are untouched, and `AddMissingRows` adds the composite row empty so a freed Backspace is never re-granted. The IrfanView, nomacs, FastStone and XnView MP presets assign Backspace to Previous image (the conflict logic unassigns the composite's Backspace). A saved map that
  lacks a row gets its defaults minus any key another row of that map already uses
  (`HotkeyDefaultMigrations.AddMissingRows`): a user binding is never taken away. They never fire from a text box.
  `explorer.back` / `explorer.forward` run the same tab history. Details: `docs/NAVIGATION-HISTORY.md`.
- `folder.exploreParent` ("Explore/Browse containing folder") has the default **Ctrl+E** (settings schema 10,
  `HotkeyDefaultMigrations.MigrateExploreParent`): its row already exists in old files with no key, so `AddMissingRows` cannot
  add it. A file older than schema 10 gets the key only when the action has no key and no other action uses Ctrl+E; a
  user binding is kept and a removal made at schema 10 or later stays. The alias `file.showInExplorer` has no key of its own;
  the title-bar button tooltip reads the target's live keys (`ShortcutForTitleButton`).
- `app.exit` ("Exit Glide", no default key) quits the whole program (all windows, tray / Speed Boost cannot keep it
  alive; the multi-tab "confirm close" rule applies). `window.close` ("Close Glide window", Ctrl+Shift+W) still closes
  only the current window.
- The five fit / size commands (Fit image, Fit to window, Fit width, Fit height, Actual size) run through
  `ApplyStatusBarFit`, so every input (status bar button, hotkey, menu, gesture) follows "Fit control scope".
- "Viewport zoom step" (`ZoomStepPolicy`): keys, menus and buttons use 1 + step; the wheel uses 12/15 of the step
  (exactly 15% / 12% at the default step of 15).
- Custom hotkey presets, the conflict prompt, sortable columns and Search hotkey all read `HotkeyCatalog.All`,
  which is the registry projection.

## Guard rails (fail the build)

- `tests/Glide.Core.Tests/AppActionRegistryTests.cs`: registry contract (`AppActionRegistry.Validate`), legacy ids,
  canonical unique defaults, every Toggle/Choice setting bindable or excluded, searchable, destructive never gestures.
- `tests/Glide.Core.Tests/HotkeyEndToEndTests.cs` (headless `MainWindow`): every default hotkey pressed through the
  window's real key handlers reaches its action; Ctrl+Shift+B collapses/expands (and Alt+Shift+S no longer does); Enter/Space after clicking a button;
  Ctrl+Tab follows the map; image hotkeys never touch a hidden image from Explorer/Home; setting hotkeys and
  gestures flip + persist; every context-menu item, status-bar button and title-bar button maps to the registry;
  no placeholder handlers; no hardcoded key handling outside the allow-list.
- `python tools/audit-action-registry.py`: static audit of the same rules (commands, accessors, choice lists vs
  the Settings XAML, menu tagging, status-bar buttons, no second hand-written action list).
