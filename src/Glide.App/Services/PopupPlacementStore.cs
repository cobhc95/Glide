using Avalonia.Controls;

namespace Glide.App.Services;

/// <summary>
/// Glide has one placement authority: the main Glide window. Child dialogs do not persist independent
/// coordinates; they always start centred on their owner. Kept behind this helper so future features
/// inherit the invariant automatically even when older call sites still pass a descriptive key.
/// </summary>
internal static class PopupPlacementStore
{
    private static bool _legacyPlacementCleaned;
    public static void Restore(Window window, string key) => Prepare(window);
    public static void Track(Window window, string key) => Prepare(window);
    public static void Save(Window window, string key) { /* intentionally no per-popup persistence */ }

    private static void Prepare(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (_legacyPlacementCleaned) return;
        _legacyPlacementCleaned = true;
        try
        {
            var legacy = System.IO.Path.Combine(Glide.App.Settings.SettingsStore.GetSettingsDirectory(), "popup-placement.json");
            if (System.IO.File.Exists(legacy)) System.IO.File.Delete(legacy);
        }
        catch { }
    }
}
