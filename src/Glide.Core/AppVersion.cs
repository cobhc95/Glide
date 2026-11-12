using System.Reflection;

namespace Glide.Core;

/// <summary>
/// Single source for product version strings. Release bump = <c>Directory.Build.props</c>
/// (&lt;Version&gt;/&lt;InformationalVersion&gt;) only; everything else reads the entry
/// assembly at runtime with a <see cref="Fallback"/> for design-time/test contexts.
/// Replaces the scattered "Glide 4.x.y — …" title literals (4.5.2 audit).
/// </summary>
public static class AppVersion
{
    public static string Fallback => typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static string Informational
    {
        get
        {
            try
            {
                var entry = Assembly.GetEntryAssembly();
                var informational = entry
                    ?.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                    .OfType<AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational))
                    return informational;
                var shortVersion = entry?.GetName().Version?.ToString(3);
                if (!string.IsNullOrWhiteSpace(shortVersion))
                    return shortVersion;
            }
            catch { }
            return Fallback;
        }
    }

    /// <summary>Major.Minor.Patch derived from <see cref="Informational"/>.</summary>
    public static string Short
    {
        get
        {
            var info = Informational;
            var dash = info.IndexOfAny(new[] { '-', '+' });
            var core = (dash >= 0 ? info.Substring(0, dash) : info).Trim();
            var parts = core.Split('.');
            return parts.Length >= 3
                ? string.Join(".", parts[0], parts[1], parts[2])
                : core;
        }
    }

    public static string TitleFor(string page) => "Glide " + Short + " — " + page;

    public static string HomeTitle => TitleFor("Home");

    public static string UnsupportedImageTitle => TitleFor("Unsupported or missing image");

    public static string RecentTitle => TitleFor("Recent pictures");

    public static string ExplorerTitle(string folder) => "Glide " + Short + " — Explorer — " + folder;

    public static string ViewerTitle(string display, string index, int zoomPercent) =>
        "Glide " + Short + " — " + display + "  " + index + "  " + zoomPercent + "%";

    public static string ViewerTitle(string prefix, string display) =>
        "Glide " + Short + " — " + prefix + " — " + display;

    public static string OpenFailedTitle(string reason) => TitleFor("Open failed: " + reason);

    public static string NoDecoderTitle(string extension) =>
        TitleFor("No decoder for " + extension + " (optional codec required)");

    public static string UserAgent => "GlideImageViewer/" + Short;

    public static string DiagnosticTag => Short + "-diagnostic";
}
