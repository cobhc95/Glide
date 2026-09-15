# Glide.Core

PURPOSE: headless product state and deterministic behavior.

OWNS: folder navigation, viewer state, semantic command definitions, typed tab/workspace model.

DOES NOT OWN: Avalonia controls, native HWND operations, file decoding, pixel buffers.

KEY TYPES: `ImageNavigator`, `ViewerState`, `GlideCommand`, `WorkspaceState`, `TabState`.

INVARIANTS: Core remains UI-framework independent; all externally important state must be inspectable as simple records/enums.

COMMON FAILURE MODE: putting UI event code or bitmap ownership here. Do not.
