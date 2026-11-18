# Glide.App

The Avalonia desktop application: `Program.cs` (entry, single instance, CLI tools), `App.axaml(.cs)`, `MainWindow` (one class in
32 `MainWindow.*.cs` partials that share all fields), `Settings/SettingsWindow` (14 partials), `Controls/` (viewport, overlays,
Home, Explorer surface), `Explorer/`, `Platform/` (Win32 and registry), `Printing/`, `Services/`, `Updates/`.

Where a feature lives, which tests cover it and which doc explains it: `../../docs/ARCHITECTURE.md`.
Recipes for adding an action or a setting: `../../AGENTS.md`. Fast check after a change: `tools\quick-check.ps1`.
