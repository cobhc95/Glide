# Glide.Native

**Owns:** only native operations that produce a measured benefit or are required for Windows fidelity.  
**Must not own:** application navigation, settings, tabs or general UI state.

Phase 1 exports one tiny C ABI function, `GlideProbeImageW`, using Windows Imaging Component to return dimensions/frame count. C ABI is deliberate: it keeps managed/native coupling obvious and stable for AI agents.

Do not grow this DLL casually. New native functions require a clear performance/platform reason and a matching managed wrapper/diagnostic path.


## 2.5 measured-performance boundary

`GlideDecodeImageBoundedW` and `GlideDecodeImageBoundedExW` request a physical-width/height bounded WIC first frame and uses decoder-native `IWICBitmapSourceTransform` scaling where the codec exposes it (notably JPEG/JPEG-XR). `GlideDecodeShellCachedPreviewW` is cache-only (`SIIGBF_INCACHEONLY`) and must never invoke a thumbnail extractor on the foreground path. `GlideDecodeImageW` / `GlideDecodeShellPreviewW` remain compatibility/fallback exports. Every native allocation returned to managed code is released through `GlideFreeImageBuffer`.


### Progressive JPEG first-frame policy (2.5)

`GlideDecodeImageBoundedExW` accepts the managed colour-first preference. When enabled, WIC is asked for progressive level 2, falling back to 1 then 0 without `GetLevelCount`; when disabled, level 0 is requested directly for absolute first-pixel speed. Both routes still attempt `IWICBitmapSourceTransform` reduced decode before scaler fallback. Full/background refinement does not use the staged low-level selection.
