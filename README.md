# Glide 3.5-2 — Configurable left/right drag policies and clean Settings baseline

**3.5-2 bug fix:** Whole-app Overlay now keeps the native/top-level Avalonia Window transparent from initial creation for its entire lifetime. Normal Glide opacity is painted by `MainRoot`; Overlay suppresses that inner fill. This prevents an already-created opaque top-level surface from showing transparent PNG/WebP pixels against grey. The 3.5-1 alpha-safe decoder/Shell-thumbnail guard remains in place.
**3.5-1 bug fix:** Overlay transparency now preserves real image alpha by forbidding Windows Shell/Explorer thumbnail fallbacks for transparency-capable formats; those cached thumbnails may already contain an irreversible grey matte.

Glide 3.5-2 preserves the single-real-window startup architecture and the 3.4 right-drag policy, while adding an independent left-drag window-movement policy. By default, image/canvas interactions may move a normal/restored window where configured, but a maximized or fullscreen Glide window cannot accidentally become a native window drag. Settings > Mouse exposes the policy and retains the separate left-drag-on-image action.

Settings now establishes its dirty-state baseline *after* all controls have loaded and canonicalized persisted/legacy values. Merely opening Settings must therefore remain **Up to date** and must not produce a false unsaved-change/restart prompt.

Read `GLIDE_MANIFESTO_AND_HANDOFF.md` first. Windows build/runtime validation remains required for this package.
