# Glide 4.2.3

Search coverage, small-screen and keyboard-input release. Compiled, tested and packaged on
Windows x64.

## Fixes

### 1. Folder-boundary prompt clipped its buttons on small or scaled screens

The "Continue to nearby folder?" prompt used a fixed 245 px height. With a long folder path the
wrapped message grew past that, pushing the Continue/Cancel buttons outside the dialog — exactly the
small-laptop symptom reported. Prompts now:

- size themselves to their content (`SizeToContent`),
- clamp to the owner's working area (DPI-aware),
- scroll long messages while keeping the decision buttons always visible.

The same treatment was applied to the Escape-close prompt, the rename dialog and the profile-name
dialog so no fixed-height prompt can clip its buttons again.

### 2. One arrow-key press could skip several images on some laptop keyboards

Some laptop keyboards deliver a duplicate KeyDown (switch bounce) just after the first, which the
previous code treated as a second navigation intent.

Rather than hard-code a guard, Glide now uses the existing **Navigation minimum interval** setting
(`Interface & Behavior`) and ships an **80 ms stock default**. That interval already gates every
accepted keyboard and mouse-button next/previous action, so a duplicated key event inside 80 ms is
ignored while a genuine tap is unaffected. It is fully user-configurable: 0 means unlimited, and the
input scope (Both / Mouse only / Keyboard only) and windowed/fullscreen/slideshow toggles are
unchanged. The held-browse producer suppresses the interval indicator so holding a key does not flash
the "wait" notice.

A settings-schema migration (**schema 5**) upgrades existing profiles whose interval was the
historical stock 0 to the new 80 ms default. A non-zero value is preserved, and a profile already at
schema 5 keeps an explicit 0 (unlimited).

In addition, losing window activation now stops any held navigation producer, so a missed KeyUp can
no longer keep browsing in the background.

### 3. Settings search is now genuinely global

The first pass of this audit only iterated settings that were already declared in
`SettingsCatalog`, so it could not see settings that existed in the UI but had never been declared.
A deeper audit compared **every control wired in the Settings window** against the catalogue and
found real gaps:

- **10 settings were wired to live controls but missing from the catalogue entirely**, so they were
  invisible to search, profiles and diagnostics — including the reported **"Always-on-top strength"**
  (`windows.alwaysOnTopMode`), plus `fullscreen.exitBehavior`, `windows.sameImageBehavior`,
  `mouse.leftWindowDrag`, `overlay.wholeAppWindowedInteractions` and `hotkeys.preset`.
- Six of those are user-facing and were added to the catalogue (with matching effect-registry
  bindings). The four embedded-Explorer settings (`appearance.explorerTheme`,
  `tabs.navigateToFolderBehavior`, `general.newExplorerTabLastLocation`,
  `general.newExplorerTabDefaultDirectory`) remain intentionally **out** of the catalogue because that
  feature was removed; their controls are hidden and a contract test forbids exposing them.
- **Combo/number/text editors carry their label as a sibling `TextBlock`, which was never indexed.**
  Search now also indexes that visible row label (e.g. "Always-on-top strength"), the control's
  content, tooltip and name.

The coverage test was hardened accordingly: it now asserts that **every UI-wired setting is declared
in the catalogue** (excluding the intentionally hidden Explorer controls) and that every setting is
findable by its label, every declared search term, and its visible on-screen wording.

**Audit result: 188 / 188 catalogued settings searchable (177 editable + 11 action rows with companion
buttons), no gaps.** Verified live in the graphical UI by typing each label into the real search box.

### 4. No automatic diagnostic files in Downloads

The always-on crash breadcrumb wrote `Glide-Diagnostic-Latest.txt` (plus an abnormal-exit copy of the
previous run) into the user's Downloads folder on every launch. That is developer evidence, not
consumer output, so it is now **opt-in only**:

- enabled explicitly with `--diagnostic-trace` or `GLIDE_DIAGNOSTIC_TRACE=1` for crash
  investigations and benchmarks;
- a normal installed or portable run writes nothing to Downloads.

User-initiated exports (Settings → Developer Options → Export Diagnostic ZIP, and Export settings)
still write to Downloads only when the user asks for them.

### 5. Directory organization

- Superseded release notes, the previous full handoff and the audit report now live in `Archive/`.
- The authoritative `GLIDE_MANIFESTO_AND_HANDOFF.md` is now a lean current-state document.
- The README screenshot gallery is present under `docs/screenshots/` and all links resolve.
- Version 4.2.3 across assembly, installer, UI and diagnostics metadata.

## Verification

| Check | Result |
|---|---|
| Managed Release build | 0 errors |
| Glide.Core.Tests | 284 passed |
| Glide.Input.Tests | 19 passed |
| Settings search coverage audit | 182/182 searchable (172 editable, 10 actionable) |
| Live diagnostic export | OVERALL PASS |
| Installer | built, PE-validated, SHA-256 recorded |
