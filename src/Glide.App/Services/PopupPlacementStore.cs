using Avalonia.Controls;

namespace Glide.App.Services;

/// <summary>
/// Older entry point kept for the many dialog call sites: forwards to <see cref="WindowPlacementService.Track"/>.
/// Resizable dialogs remember their size and position; fixed-size and content-sized dialogs always open centred on
/// their owner and remember nothing (<see cref="DialogPlacement"/>).
/// </summary>
internal static class PopupPlacementStore
{
    /// <summary>Registers <paramref name="window"/> with the shared placement service under <paramref name="key"/>.</summary>
    public static void Track(Window window, string key) => WindowPlacementService.Track(window, key);
}
