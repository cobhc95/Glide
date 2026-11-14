# Glide 4.2.0

## Built-in Explorer overhaul

- Explorer Back/Forward history now preserves the selected child/file for each visited folder.
- Up navigation records the folder being exited and highlights that child in the parent.
- Repeated Up/Back/Forward traversal no longer reuses a stale selection from an earlier folder.
- Browser history selection state travels with browser tabs/windows through `BrowserNavigationState`.
- View-mode changes now retire the previous ItemsRepeater visual generation before creating the new layout/template.
- Large, Medium and Compact views receive a fresh `UniformGridLayout`, preventing stale measured visuals from overlapping after mode changes.
- Browser item margins no longer compete with grid-cell geometry; grid spacing is owned by the layout.
- Existing virtualized thumbnail cancellation/generation protection is preserved.

## Existing 4.2.0 feature work retained

- Configurable navigation rate limiting and wait-indicator placement.
- Hierarchical previous-folder first/last-image preference.
- Overlay horizontal/vertical resize controls.
- Overlay viewport resize retains image zoom and crops/reveals instead of fitting.


## Explorer removal / overlay control repair

- Removed all user-facing entry points to Glide's built-in Explorer.
- Legacy Explorer tabs/settings migrate to Home/Welcome instead of opening the removed browser.
- Removed title-bar Up/Forward browser navigation, containing-folder commands, Explorer hotkeys,
  Explorer theme/new-Explorer-tab settings, and the title-bar Navigate-to-folder action.
- Windows File Explorer external-launch handling remains available where it is unrelated to the
  removed embedded browser.

### Overlay controls
- Restored reliable interaction for close, zoom, opacity and resize controls by removing the
  transient chrome-timer dependency from hit testing.
- The selected overlay keeps its chrome visible; hovering an overlay also exposes controls.
- Added a large right-middle horizontal resize button (↔).
- Added a large top-middle vertical resize button (↕), anchored to the bottom edge while resizing.
- Bottom-right resize remains available.
- All resize modes retain `Zoom` and `ContentScale`; resizing only crops/reveals the viewport.
