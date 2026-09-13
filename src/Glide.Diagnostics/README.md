# Glide.Diagnostics

**Owns:** truthful, machine-readable structural evidence about Glide's runtime/build state and export format.  
**Must not own:** UI feature behavior, image decoding, workspace state mutation, or hiding failures.

## Current Glide 2.9 contract

`DiagnosticRunner` provides the headless `--diagnostics self-test` build gate and export files. It verifies assertions that can be proven without a live Avalonia visual tree, including settings-schema integrity, typed workspace transfer, the 68-action hotkey contract, the 24-slot/19-action gesture contract, overlay state/layout and the 196-suffix capability registry. Live-only checks remain `SKIP`/`WARN`; structural PASS is never visual certification.

Exports include `codec_capabilities.json`, which reports recognised/routed breadth separately from guaranteed decode, and `codec_providers.json`, which reports only providers already verified in the current process. Exporting this evidence does not enumerate, hash or load optional providers.

`Glide.App/Diagnostics/UiDiagnosticCapture` extends the same evidence model when real windows exist. Developer Options export captures main-window screenshots, every Settings category immediate/settled, the editable search fixture, resize scenarios, logical/physical geometry, clipping/hit-target audits, control inventory, settings/effect state, runtime state and the application behaviour trace.

Exports are written to the Windows Downloads known folder. Keep diagnostic wording synchronized with actual implementation state and never upgrade a live-only claim to PASS without live evidence.
