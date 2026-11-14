# Glide 4.2.1

## Settings search completeness

- Audited the Settings catalogue against the live Settings controls so user-facing controls are represented in search.
- Added searchable/editable entries for Always-on-top strength, fullscreen exit behaviour, same-image behaviour, embedded Explorer theme, new Explorer-tab location controls, folder-navigation behaviour, left-window-drag behaviour, Overlay interaction behaviour, and the two slideshow toggles that were previously omitted from the editable search map.
- Normalized Overlay and Profile catalogue categories so category-filtered search finds them under **Window in Window** and **Profiles & Presets**.
- Hotkey and interaction presets can now be selected from search and applied/loaded directly there; reset-hotkeys is also exposed as a search action.
- Existing action-style settings remain directly actionable from search, including diagnostics, file associations, import/export, gesture configuration, and the hotkey registry.

## Always-on-top wording

- Replaced the old/new wording with a technical explanation:
  - **Soft** applies `HWND_TOPMOST` normally.
  - **Hard** additionally reasserts `HWND_TOPMOST` after competing Z-order changes, helping Glide remain above fullscreen windows.
