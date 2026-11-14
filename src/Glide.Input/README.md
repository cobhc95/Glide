# Glide.Input

PURPOSE: translate physical input into semantic Glide commands.

OWNS: input mapping. The mouse gesture rows, custom gestures and their default actions live in `Glide.Core/Commands/GestureCatalog.cs` (see `docs/ACTIONS-AND-HOTKEYS.md`).

DOES NOT OWN: navigation, decoding, rendering, tab business logic, window geometry.

INVARIANT: input handlers dispatch semantic commands; they never directly mutate unrelated subsystems.

COMMON FAILURE MODE: a new feature handles a raw key/mouse event directly in a view. Fix by routing it here.

TEST: `dotnet test tests/Glide.Input.Tests/Glide.Input.Tests.csproj`.
