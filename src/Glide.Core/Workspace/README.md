# Glide.Core.Workspace

PURPOSE: framework-free typed workspace/tab state.

OWNS: tab identity, active tab, create/select/close/restore/duplicate/cycle and the current path represented by an Image tab.

DOES NOT OWN: Avalonia tab visuals, HWND/native dragging, decoding, image viewport transforms.

KEY TYPES: `WorkspaceState`, `TabState`, `HomeTabState`, `ImageTabState`, `BrowserTabState`.

STATE FLOW: semantic command/UI intent -> WorkspaceState mutation -> MainWindow rebuilds visual tab strip -> active typed tab is presented.

INVARIANTS: at least one tab always exists; closing final tab creates Home; restored tabs receive fresh IDs; active index is always valid.

COMMON FAILURES: UI becoming source of truth; image navigation changes viewer path but not tab path; close events selecting wrong tab.

TESTS: `WorkspaceStateTests` in `tests/Glide.Core.Tests`.
