# Gesture precedence per surface (Stage D step 3)

Recorded from the code before the `GestureResolver` extraction (`src/Glide.Core/Commands/GestureResolver.cs`), pinned by
`tests/Glide.Core.Tests/GestureResolverTests.cs`. "Default" = the row is left on its catalog default. Custom gestures
(same area + input + held key) always win over the slot binding (`GestureCatalog.ResolveAction`).

## Which surface gets the input (outer to inner)

1. Status / chrome controls (status bar buttons, caption buttons, tabs, toolbar) own their own presses even when the
   image/overlay layers already marked the event handled (tunnel handlers, handled-events-too).
2. Side buttons (X1/X2), window-level tunnel: area by what is under the pointer, first match wins:
   tab strip -> visible Explorer page -> visible image (Fullscreen or Windowed) -> visible Home page (Windowed rows) -> none.
3. Title bar: right-click and wheel rows apply only to the empty title bar (interactive sources and the tab strip excluded).
4. Tab strip wheel row, Explorer wheel row, status bar wheel (always browses), overlay windows (own resolver callback).
5. Image viewport (`ImageViewport`), below.

## Default meaning per row kind

| Row kind | Default does | Explicit choice |
|---|---|---|
| Title bar wheel / right-click, tab-strip wheel, Explorer wheel, double-left (windowed image / background / fullscreen) | surface built-in (nothing / menu / scroll strip / scroll list or Ctrl icon size / enter fullscreen) | runs, input handled ("Do nothing" blocks) |
| Side buttons | `DefaultActionFor(slot)`; none -> pass-through unhandled (5.0.8) | runs, input handled |
| Explorer double-click / middle-click | open in current tab / new tab | runs instead; the context menu's Open items always open |

## Image viewport, pointer press (first match wins)

1. Right press while a slideshow is active: Default stops a *running* slideshow; another non-pass-through action runs.
2. Left press on the picture during a slideshow: anything but "Ignore here" (Default = pause/resume).
3. Middle press during a slideshow: a non-Default, non-pass-through row.
4. Windowed double-right on the image (500 ms / 5 px): non-Default row.
5. Fullscreen left/right/middle row: non-Default/pass-through runs; Default left = click navigation (with the
   double-click-exit delay when enabled); right navigates back on release.
6. Off the picture: `drag.leftEmpty` / `drag.rightEmpty` rows; Default left = window drag, right = context menu on release.
7. On the picture: selection rows, then drag rows (left/right/middle), then click rows on release.

## Image viewport, wheel

Held-key row (Ctrl > Shift > Alt > plain). Shift / Alt on Default fall back to the plain row. While a slideshow is active
a non-Default `slideshow.wheel` replaces it; while the right button is held a non-Default `wheel.rightHeld` replaces that
(and the right release is never a click). Default = the viewport's built-in zoom/browse per settings.
