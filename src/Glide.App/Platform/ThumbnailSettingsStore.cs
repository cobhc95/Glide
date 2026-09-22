using System.Text.Json;
using Glide.Core;

namespace Glide.App.Platform;

/// <summary>User-facing Explorer thumbnail configuration. Mirrors the Settings UI section.</summary>
internal sealed record ThumbnailSettings(
    bool Enabled,
    ThumbnailProviderMode Mode,
    string Quality,
    bool PreferEmbedded,
    int MaxSourceSize)
{
    public static ThumbnailSettings Default { get; } = new(
        Enabled: true,
        Mode: ThumbnailProviderMode.Recommended,
        Quality: "Balanced",
        PreferEmbedded: true,
        MaxSourceSize: 0);
}

/// <summary>
/// Persists and applies Explorer thumbnail settings. Like printing, this is a self-contained
/// integration feature with its own settings file so it never has to ride the launch-policy path.
/// </summary>
internal static class ThumbnailSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath =>
        Path.Combine(Path.GetDirectoryName(Settings.SettingsStore.GetSettingsPath()) ?? AppContext.BaseDirectory,
                     "thumbnail.settings.json");

    public static ThumbnailSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return ThumbnailSettings.Default;
            var settings = JsonSerializer.Deserialize<ThumbnailSettings>(File.ReadAllText(SettingsPath), JsonOptions);
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
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Normalize(settings), JsonOptions));
        }
        catch { /* Persistence is best-effort; the registry is the source of truth at runtime. */ }
    }

    /// <summary>Applies the settings to the per-user registry and reports a human-readable status.</summary>
    public static bool Apply(ThumbnailSettings settings, out string message)
    {
        settings = Normalize(settings);
        if (!settings.Enabled)
        {
            WindowsThumbnailRegistration.Unregister(perUser: true, ImageFormatRegistry.Extensions, out message);
            return true;
        }

        // Clear any stale per-user entries first so a mode change removes formats it no longer owns.
        WindowsThumbnailRegistration.Unregister(perUser: true, ImageFormatRegistry.Extensions, out _);
        var extensions = WindowsThumbnailRegistration.ExtensionsFor(settings.Mode);
        return WindowsThumbnailRegistration.Register(perUser: true, extensions, out message);
    }

    /// <summary>One-line diagnostics summary for the Settings UI.</summary>
    public static string Describe(ThumbnailSettings settings)
    {
        var architecture = Environment.Is64BitProcess ? "x64" : "x86";
        var dll = WindowsThumbnailRegistration.ProviderDllPresent ? "present" : "missing";
        var registered = WindowsThumbnailRegistration.IsRegistered() ? "yes" : "no";
        var registeredCount = settings.Enabled ? WindowsThumbnailRegistration.ExtensionsFor(settings.Mode).Count : 0;
        var version = typeof(ThumbnailSettingsStore).Assembly.GetName().Version?.ToString() ?? "unknown";
        return $"Provider: {dll} | Registered (this user): {registered} | Architecture: {architecture} | " +
               $"Formats: {registeredCount} | Mode: {settings.Mode} | Quality: {settings.Quality} | Provider version: {version}";
    }

    private static ThumbnailSettings Normalize(ThumbnailSettings? settings)
    {
        settings ??= ThumbnailSettings.Default;
        var quality = settings.Quality is "Fast" or "Balanced" or "High" ? settings.Quality : "Balanced";
        var mode = Enum.IsDefined(settings.Mode) ? settings.Mode : ThumbnailProviderMode.Recommended;
        var maxSource = settings.MaxSourceSize is 0 or 512 or 1024 or 2048 ? settings.MaxSourceSize : 0;
        return settings with { Quality = quality, Mode = mode, MaxSourceSize = maxSource };
    }
}
