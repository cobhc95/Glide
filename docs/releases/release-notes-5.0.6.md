# Glide 5.0.6

Thanks to Vertigo220 for yet another round of detailed feedback. Settings from 5.0.x load unchanged, apart from three default migrations, each applied once: Navigation minimum interval 80 to 120 ms, Open new tabs in the foreground off to on, and After closing a tab to Previously used tab. The full list is in [CHANGELOG.md](https://github.com/cobhc95/Glide/blob/main/CHANGELOG.md).

## Laptop keys skipping pictures

**The problem:** a bouncy or sticky key, common on laptop keyboards, sends one press several times, so a single tap of an arrow or Page Down key skipped several pictures.

**Telling it apart from holding a key:** a held key pauses for about 250–500 ms before Windows starts repeating it, and every repeat is marked as a repeat. A bounce arrives as separate, unmarked presses less than 150 ms apart, or as one press that moves 3 or more pictures and is released within 250 ms.

```text
Held key     ▼ press ......... 250-500 ms ......... r r r r r   (r = marked repeat)
Bouncing key ▼ press  ▼ press  ▼ press                          (each < 150 ms apart, unmarked)
             └───────────────── time ─────────────────────────►
```

**What changed:**

- **Navigation minimum interval** now defaults to **120 ms** (was 80 ms). Settings saved with 80 ms move to 120 ms once; any other value you chose is kept. Lower it, or set 0, for faster browsing.
- A help line under the setting explains when to raise or lower it. Settings search finds it with "skip", "laptop", "bounce" and "sticky key".
- When Glide sees a bounce, a small toast asks **Skipping several pictures at once?** with **Adjust navigation speed →** (opens the setting) and **Don't remind me again**. It never takes focus and shows at most once every 5 minutes. Turn it back on with *Tell me when a key press seems to skip pictures* in Settings.

## New

- **New tabs open at** (next to the current tab, or at the end) and **Open new tabs in the foreground** (now on by default; saved settings with the old default move to on once). Optional: *Shift and Alt change how a new tab opens*. Ctrl+T and the **+** button follow the position too.
- **After closing a tab, activate:** Previously used tab (new default; saved settings with the old default move to it once), Match Ctrl+Tab order, Tab to the right or Tab to the left.
- **Tab previews on hover:** a background tab's tooltip shows its picture, or the first thumbnails of an Explorer tab, in a tighter card with name, full path, size and mouse hints. Optional: *Animate GIF previews on hover*.
- Explorer tab: a **Sort** button next to the view box, **click a selected item's name to rename it** (as in File Explorer), and *Keep selection in view when sorting*.
- **Text overlay border:** the overlay lines in one box with a rounded accent border.
- Settings: Performance now sits under General, Tabs & status bar and the Explorer tab card under Window & navigation, with links between related settings (Explorer scroll speed, opening pictures in new tabs).
- The **Unsaved settings changes** dialog is smaller, with a short scrolling list of what changed.

## Fixes

- Tooltips appear over an inactive Glide window again, no longer pop up while scrolling, and side tabs' tooltips open beside the tab.
- Closing a tab returns to the tab you were on; several items opened in background tabs keep their order.
- Explorer tab: a plain click inside a multi-selection selects just that item; right-click menus stay left-aligned, Cut and Paste have icons, *Copy N paths* and *Properties* target the item you right-clicked; the view box no longer cuts off its text (views are now Thumbnails XL / L / M / S, List and Details).
- Text overlay changes preview instantly again, and the template help shows `\n` correctly.
- Hover previews and Explorer thumbnails skip pictures beyond the safe pixel or memory limit.
- Settings search **Open** scrolls to and highlights the setting; *Always expand* applies to the first page too.
- Dialogs open centred on the window that opened them instead of the top-left of the screen.
- Back / Forward work as soon as a new tab is active.

## Downloads

- `Glide.Setup.exe`: installer (Windows 10 1607 or later, x64)
- `Glide-5.0.6-Portable.zip`: portable build
- `Glide.Setup.sha256`: checksum
