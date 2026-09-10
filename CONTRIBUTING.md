# Contributing

Glide is intentionally a small, native, interaction-heavy Windows viewer. Contributions are welcome when they preserve that character.

Before a pull request:

- keep startup and first-image latency in mind;
- avoid adding large frameworks for small features;
- keep decode, cache, rendering, tab and settings concerns separated where possible;
- do not make network access a runtime requirement;
- add or update targeted diagnostics for behaviour you change;
- preserve the physical mouse-button semantics of drag operations;
- keep Settings searchable and keyboard/mouse actions centralized;
- keep specialist format claims truthful: registered/provider-assisted support is not the same as guaranteed built-in decoding.

For bugs, please include reproducible steps, Windows version, file format and—if relevant—the Glide diagnostics ZIP.
