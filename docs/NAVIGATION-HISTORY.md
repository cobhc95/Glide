# Back / Forward: the per-tab navigation history (5.0.0)

Glide's Back and Forward work like a web browser or Windows 11 File Explorer, per tab. Example: open an image from
the Explorer tab (in the same tab), press Back: the Explorer comes back in the same folder, scrolled as it was, with
the image selected. Press Forward: the image is shown again.

## The model (Glide.Core, framework-free)

`src/Glide.Core/Navigation/NavigationHistory.cs`

| Type | Role |
|---|---|
| `NavigationEntry` | One step: `Home`, `Explorer` (folder, selected item, scroll offset, view mode) or `Image` (file). |
| `NavigationHistory` | The list + current index of one tab. `Push`, `Replace`, `UpdateCurrent`, `Record`, `Step`, `GoTo`, `Back`, `Forward`, `CanGoBack`, `CanGoForward`, `Snapshot` / `FromState`. At most 100 entries (oldest dropped). |
| `NavigationTargetResolver` | What Back / Forward does with a target that changed on disk (below). |

Rules:

- **Push**: going somewhere else (another folder, an image opened from the Explorer or Home in the same tab) drops
  every Forward entry and adds the new one, like a browser. Pushing the place that is already current replaces it.
- **Record** (what the window calls whenever a tab shows something): nothing recorded yet -> first entry; same place ->
  kept as it is (its remembered selection / scroll stay); **image -> image -> replaced in place**; anything else -> push.
  So the arrow keys, Next / Previous, the wheel, the slideshow, sibling-folder navigation and Open file in the same tab
  never add history steps. Back from an image opened from the Explorer selects the image that is on screen **when Back
  is pressed** (not the one that was opened), and Forward shows that image again.
- **Explorer selection / scroll** are written into the current Explorer entry whenever the tab leaves it (another
  folder, an image, Back / Forward, a tab switch, a session save), so Back restores them.

## Where it lives in the window

`src/Glide.App/MainWindow.History.cs` owns `_tabHistories` (one history per tab id), `NavigateBackAsync`,
`NavigateForwardAsync`, `NavigateHistoryToIndexAsync` (history dropdown), `RecordActiveTabLocation`,
`SyncCurrentHistoryEntry`, `UpdateHistoryNavigationState` (all Back / Forward controls) and `RemoveTabForTransfer`.

- `ActivateWorkspaceAsync` records the active tab at the end; `OpenAsImageTabAsync` first saves the Explorer state of
  the tab it replaces, then records the image (`startNewHistory` for a placeholder Home replaced by a launched file).
- `NavigateBrowserTo(addHistory: true)` pushes the folder; `addHistory: false` (refresh, Back / Forward) only records.
- Back to an Explorer entry from an image / Home turns the same tab back into an Explorer tab
  (`new BrowserTabState(sameId, folder)`) with the tab's own last view of that folder (sort, filter, icon size) and the
  entry's selection, scroll and view mode; the selected item is scrolled into view if the scroll misses it.
- Back / Forward to an image or Home replaces the tab state in place (same tab id, same position).
- A step taken while an image is still loading supersedes it (the workspace epoch cancels the old load), like pressing
  Back again in a browser before the page finished.

## Inputs (all the same history)

| Input | Default |
|---|---|
| Back (`nav.back`, `GlideCommand.NavigateBack`) | **Alt+Left**; mouse back button (XButton1); title-bar Back button |
| Forward (`nav.forward`, `GlideCommand.NavigateForward`) | **Shift+Backspace**, **Alt+Right**; mouse forward button (XButton2); title-bar Forward button |
| Backspace (`nav.backOrPrevious`, `GlideCommand.BackOrPrevious`) | **Back, or Previous image**: Back when the tab has a step back (also in the Explorer list and Home), else Previous image in an image tab, else nothing |
| Explorer toolbar Back / Forward, history dropdown, `explorer.back` / `explorer.forward` | the same history (the dropdown also lists images and Home; full paths, most recently visited place first, each place once: `NavigationHistory.RecentOrder`) |
| Up one folder | Alt+Up (fixed Explorer key, unchanged) and the toolbar Up button: a new history step |

- **Backspace is one composite action, `nav.backOrPrevious` ("Back, or Previous image").** `MainWindow.BackOrPreviousAsync`:
  if the active tab's history `CanGoBack` it is exactly Back (an image opened from the Explorer returns to that folder with
  the image selected, Forward returns to the image); otherwise, in an image tab, it is Previous image (an image opened
  directly from Windows Explorer, a fresh tab or window, or a tab whose history is used up has no Back step); otherwise it
  does nothing (Explorer or Home tab without history). `nav.back` (Alt+Left, mouse back button) is strict: it never falls
  back to Previous image. Forward (Shift+Backspace, Alt+Right) is strict too. `nav.previousBack` ("Previous image (extra
  key)", legacy id kept for saved files and presets) has no default key; Left arrow / Page Up are unchanged. There is no
  fixed Explorer Backspace key any more (`fixed.explorer.backspace` was removed): the Explorer list's Backspace reaches the
  window's hotkey route like every bound key, so a user who moves or removes the binding moves or removes it in the Explorer.
- **Migration (settings schema 11):** `HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious` (called from
  `SettingsStore.MigrateDefaultsSchema8`, so Load, `ProfileStore.Load` and the settings import all run it) moves Backspace
  from `nav.previousBack` to `nav.backOrPrevious` only when the old row is bound to exactly Backspace and no other action
  uses it; rebound / removed / taken keys and schema >= 11 files are left alone. `AddMissingRows` adds the composite row
  empty so a Backspace the user freed is not re-granted. Presets: IrfanView, nomacs, FastStone and XnView MP deliberately keep
  Backspace on Previous image (the composite is left without it); Windows Photos frees it and Glide default follows the
  new default.
- **Text boxes:** Back / Forward / Back-or-Previous never fire while typing (Explorer address bar, filter box, inline rename): Backspace
  and Shift+Backspace are stopped by the text-input guard, Alt+Left / Alt+Right (and any rebound key) by an explicit check in
  `RouteHotkeyAsync`. Settings and dialogs are separate windows and never reach the main window's keys.
- **Mouse side buttons:** the gesture rows `windowed.` / `fullscreen.backButtonImage`, `tabs.backButton`,
  `explorer.backButton` (and the forward rows) show "Default (not used by Glide)" (5.0.8): Default leaves the
  press unhandled for Windows / mouse software. Bind `nav.back` / `nav.forward` (or anything) to use them; "Do nothing" blocks. Over a tab the active tab goes Back / Forward. The Welcome page has no gesture rows; it
  follows the Windowed image rows.
- **Title bar:** `TitleBarButtonCatalog.DefaultButtonIds` starts with Back, Forward (then Previous / Next folder, Open,
  Show in Explorer, Always on top, Settings). Previous / Next image remain optional buttons (the status bar and the
  arrow keys have them). Back / Forward are dimmed (class `inactive`, 34% opacity) when there is nothing to go to; they
  stay hit-testable (a disabled control would let the click fall through to the caption: window drag / maximize) and
  do nothing then. Tooltips read the live binding: `[Back (Alt+Left)]`.

## Missing targets

Same fallback as the workspace session restore (`NavigationTargetResolver`):

- an image that is gone is **skipped** and removed from the history; the step continues in the same direction. Message:
  "Skipped an image that no longer exists." (or, with nothing left, "That image no longer exists.");
- a folder that is gone opens its **nearest existing parent** (nothing selected, no remembered scroll). Message: "That
  folder no longer exists; showing the nearest folder that does." Two identical neighbours are merged;
- a remembered selected item that is gone: the folder opens with nothing selected;
- Home always works.

Messages appear in the Explorer message line or over the image (the folder-boundary notice); `LastHistoryNotice` keeps
the last one for tests.

## New tabs, moved tabs, sessions

- An image opened in a **new tab** (middle-click / "Open in new tab") starts with no history: Back is dimmed and does
  nothing (browser convention: a link opened in a new tab has no Back).
- A tab **dragged to another window or detached** carries its history (`TabState.History`, a `NavigationHistoryState`
  snapshot set by `RemoveTabForTransfer`, never saved to disk).
- **Session restore is unchanged:** the session file keeps an Explorer tab's folders (`BrowserNavigationState`,
  the Explorer entries of the history, with their selection as the highlight). Images and Home entries are not saved,
  and image / Home tabs restart with an empty history after a restart.
- A placeholder Home tab replaced by a file launched from Windows starts the tab's history at that image.

## Settings migration (settings schema 9)

- Hotkeys: a saved map without the new rows gets them with their defaults, **minus any key another row of the saved
  map already uses** (`HotkeyDefaultMigrations.AddMissingRows`). Example: a user who bound Alt+Left to something else
  keeps it; Back starts without Alt+Left (Settings > Hotkeys then offers Replace / Cancel if they assign it).
- Title bar: a saved layout that is exactly the previous default
  (`TitleBarButtonCatalog.PreviousDefaultButtonIds`) moves once to the new default; any other layout is kept as it is.
  Schema 9 makes this one-time, so a user who removes Back / Forward later keeps them removed. Known limit: someone who
  deliberately chose exactly the previous default is indistinguishable from an untouched one and gets Back / Forward
  once.
- Gestures: nothing is migrated; saved "default" side-button rows now mean Back / Forward.

## Tests

- `tests/Glide.Core.Tests/NavigationHistoryTests.cs`: model, resolver, registry, fixed keys, hotkey-row migration.
- `tests/Glide.Core.Tests/NavigationHistoryWindowTests.cs`: headless MainWindow: title-bar default / catalogue /
  migration, dimmed buttons and live tooltips, Home -> image -> Back / Forward with real key presses and title-bar
  clicks, Backspace routed to the composite, Backspace without a Back step = Previous image, Alt+Left without history does nothing, new tab without history, missing image, history travelling with a tab.
- `tests/Glide.Explorer.Tests/ExplorerNavigationHistoryTests.cs`: Explorer -> image -> Back (selection + scroll) ->
  Forward; arrows then Back selects the current image; mouse side buttons; toolbar buttons, dropdown and keys across
  the image; text boxes never navigate; real Backspace from the image back to the Explorer, Forward again, Backspace in a history-less Explorer list does nothing; a user Backspace binding takes over; new tab; folder fallback.
- `HotkeyEndToEndTests` (side buttons default), `AuditItemsTests` (side-button rows), `DiagnosticRunner`
  `navigation_history_contract`.
