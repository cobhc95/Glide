# Glide.Input

PURPOSE: translate physical input into semantic Glide commands.

OWNS: input mapping, future gesture-slot resolution, future explicit drag ownership state.

DOES NOT OWN: navigation, decoding, rendering, tab business logic, window geometry.

INVARIANT: input handlers dispatch semantic commands; they never directly mutate unrelated subsystems.

COMMON FAILURE MODE: a new feature handles a raw key/mouse event directly in a view. Fix by routing it here.

TEST: `dotnet test tests/Glide.Input.Tests/Glide.Input.Tests.csproj`.
