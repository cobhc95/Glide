using Avalonia.Controls;

namespace Glide.App.Services;

/// <summary>
/// Older entry point kept for the many dialog call sites: forwards to <see cref="WindowPlacementService.Track"/>, which
/// restores the dialog's remembered placement (size and position for resizable dialogs, position only for fixed-size
/// ones) and saves it when it closes. With nothing saved a dialog opens centred on its owner, as before.
/// </summary>
internal static class PopupPlacementStore
{
    /// <summary>Registers <paramref name="window"/> with the shared placement service under <paramref name="key"/>.</summary>
    public static void Track(Window window, string key) => WindowPlacementService.Track(window, key);
}
