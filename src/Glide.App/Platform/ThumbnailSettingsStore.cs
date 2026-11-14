using System.Text.Json;
using Glide.Core;
using Microsoft.Win32;

namespace Glide.App.Platform;

/// <summary>User-facing Explorer thumbnail configuration. Mirrors the Settings UI section.</summary>
/// <param name="CustomExtensions">
/// Extensions routed to Glide in <see cref="ThumbnailProviderMode.Custom"/> mode. There is no per-format
/// editor yet, so Custom keeps the selection that was active when it was first applied (see
/// <see cref="ThumbnailSettingsStore.Apply(ThumbnailSettings, out string, out ThumbnailSettings)"/>).
/// </param>
internal sealed record ThumbnailSettings(
    bool Enabled,
    ThumbnailProviderMode Mode,
    string Quality,
    bool PreferEmbedded,
    int MaxSourceSize,
    IReadOnlyList<string>? CustomExtensions = null)
{
    public static ThumbnailSettings Default { get; } = new(
        Enabled: true,
        Mode: ThumbnailProviderMode.Recommended,
        Quality: "Balanced",
        PreferEmbedded: true,
        MaxSourceSize: 0);
}

/// <summary>
/// The values the native provider (Glide.ShellThumbnail.dll) reads on every thumbnail request, from
/// HKCU\Software\Glide\ExplorerThumbnails. Before 4.8.0 nothing was written here and the provider
/// hard-coded Balanced/embedded/automatic, so the quality, decode-size and embedded-preview
/// controls had no effect at all.
/// </summary>
internal readonly record struct ThumbnailProviderConfig(int Quality, int MaxSourceSize, bool PreferEmbedded)
{
    /// <summary>Must match kConfigKey in native/Glide.ShellThumbnail/GlideThumbnailProvider.cpp.</summary>
    public const string KeyPath = @"Software\Glide\ExplorerThumbnails";

    /// <summary>Native quality codes: 0 = Fast, 1 = Balanced, 2 = High (glide::thumb::Quality).</summary>
    public static int QualityCode(string? quality) => quality switch
    {
        "Fast" => 0,
        "High" => 2,
        _ => 1,
    };

    public static ThumbnailProviderConfig From(ThumbnailSettings settings) => new(
        QualityCode(settings.Quality),
        settings.MaxSourceSize is 512 or 1024 or 2048 ? settings.MaxSourceSize : 0,
        settings.PreferEmbedded);

    public bool Write()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key is null) return false;
            key.SetValue("Quality", Quality, RegistryValueKind.DWord);
            key.SetValue("MaxSourceSize", MaxSourceSize, RegistryValueKind.DWord);
            key.SetValue("PreferEmbedded", PreferEmbedded ? 1 : 0, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>
/// Persists and applies Explorer thumbnail settings. Like printing, this is a self-contained
/// integration feature with its own settings file so it never has to ride the launch-policy path.
/// </summary>
internal static class ThumbnailSettingsStore
{
    public const string CacheHint =
        "Windows keeps thumbnails it has already made; use Refresh thumbnails to rebuild them with these settings.";

    public static string SettingsPath =>
        Path.Combine(Path.GetDirectoryName(Settings.SettingsStore.GetSettingsPath()) ?? AppContext.BaseDirectory,
                     "thumbnail.settings.json");

    public static ThumbnailSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return ThumbnailSettings.Default;
            var settings = JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), AppJsonContext.Default.ThumbnailSettings);
            return Normalize(settings);
        }
        catch { return ThumbnailSettings.Default; }
    }

    public static void Save(ThumbnailSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Normalize(settings), AppJsonContext.Default.ThumbnailSettings));
        }
        catch { /* Persistence is best-effort; the registry is the source of truth at runtime. */ }
    }

    /// <summary>Applies the settings to the per-user registry and reports a human-readable status.</summary>
    public static bool Apply(ThumbnailSettings settings, out string message)
        => Apply(settings, out message, out _);

    /// <summary>
    /// Writes the provider presets, then (re)registers the per-user handler set for the mode.
    /// <paramref name="applied"/> is what was actually applied (Custom mode records its selection) and
    /// is what callers should persist.
    /// </summary>
    public static bool Apply(ThumbnailSettings settings, out string message, out ThumbnailSettings applied)
    {
        settings = Normalize(settings);
        applied = settings;

        // Presets first: they are read by the provider on every request, whatever the mode.
        var configWritten = ThumbnailProviderConfig.From(settings).Write();
        var configNote = configWritten ? " " + CacheHint : " Could not save the thumbnail quality settings for Explorer.";

        if (!settings.Enabled)
        {
            WindowsThumbnailRegistration.Unregister(perUser: true, ImageFormatRegistry.Extensions, out message);
            message += configNote;
            return true;
        }

        IReadOnlyList<string> extensions;
        var modeNote = string.Empty;
        if (settings.Mode == ThumbnailProviderMode.Custom)
        {
            // Read the current selection before the stale entries are cleared below.
            extensions = ResolveCustomExtensions(settings.CustomExtensions,
                                                 () => WindowsThumbnailRegistration.ExtensionsRoutedToGlide(perUserOnly: true),
                                                 () => WindowsThumbnailRegistration.ExtensionsFor(ThumbnailProviderMode.Recommended),
                                                 out modeNote);
            applied = settings with { CustomExtensions = extensions };
        }
        else
        {
            extensions = WindowsThumbnailRegistration.ExtensionsFor(settings.Mode);
        }

        // Clear any stale per-user entries first so a mode change removes formats it no longer owns.
        WindowsThumbnailRegistration.Unregister(perUser: true, ImageFormatRegistry.Extensions, out _);
        var ok = WindowsThumbnailRegistration.Register(perUser: true, extensions, out message);
        message += modeNote + configNote;
        return ok;
    }

    /// <summary>
    /// Custom mode used to call ExtensionsFor(Custom) with no list, which registered zero formats and
    /// silently switched Glide thumbnails off. It now keeps the saved selection, else the formats
    /// currently routed to Glide for this user, else starts from the Recommended set.
    /// </summary>
    internal static IReadOnlyList<string> ResolveCustomExtensions(
        IReadOnlyList<string>? saved,
        Func<IReadOnlyList<string>> currentlyRouted,
        Func<IReadOnlyList<string>> recommended,
        out string note)
    {
        note = string.Empty;
        var selection = WindowsThumbnailRegistration.ExtensionsFor(ThumbnailProviderMode.Custom, saved);
        if (selection.Count > 0) return selection;
        selection = WindowsThumbnailRegistration.ExtensionsFor(ThumbnailProviderMode.Custom, currentlyRouted());
        if (selection.Count > 0)
        {
            note = $" Custom keeps the {selection.Count} formats currently routed to Glide.";
            return selection;
        }
        note = " Custom had no saved selection, so it starts from the Recommended formats.";
        return recommended();
    }

    /// <summary>One-line diagnostics summary for the Settings UI.</summary>
    public static string Describe(ThumbnailSettings settings)
        => ThumbnailProviderStatus.Describe(WindowsThumbnailRegistration.ProbeFacts(), Normalize(settings));

    internal static ThumbnailSettings Normalize(ThumbnailSettings? settings)
    {
        settings ??= ThumbnailSettings.Default;
        var quality = settings.Quality is "Fast" or "Balanced" or "High" ? settings.Quality : "Balanced";
        var mode = Enum.IsDefined(settings.Mode) ? settings.Mode : ThumbnailProviderMode.Recommended;
        var maxSource = settings.MaxSourceSize is 0 or 512 or 1024 or 2048 ? settings.MaxSourceSize : 0;
        var custom = settings.CustomExtensions is { Count: > 0 }
            ? WindowsThumbnailRegistration.ExtensionsFor(ThumbnailProviderMode.Custom, settings.CustomExtensions)
            : null;
        return settings with { Quality = quality, Mode = mode, MaxSourceSize = maxSource, CustomExtensions = custom };
    }
}
