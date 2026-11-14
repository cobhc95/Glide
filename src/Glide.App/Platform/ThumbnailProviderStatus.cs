using System.Diagnostics;

namespace Glide.App.Platform;

/// <summary>
/// Facts gathered (read-only) about the Explorer thumbnail provider. Kept separate from the
/// registry/file probing so the status wording can be unit-tested for the installed, portable and
/// developer layouts without touching the machine.
/// </summary>
internal sealed record ThumbnailProviderFacts(
    string LocalDllPath,
    bool LocalDllExists,
    string? LocalDllVersion,
    string? UserRegisteredPath,
    string? MachineRegisteredPath,
    bool EffectiveDllExists,
    string? EffectiveDllVersion,
    int RoutedFormats,
    string GlideVersion);

/// <summary>
/// Builds the one-line Explorer thumbnail status. The old line only checked for the DLL next to the
/// running Glide.exe, so a portable/standalone exe or a dev build reported "Provider: missing" even
/// though Explorer was happily loading the installed provider, and "Provider version" was really
/// Glide.exe's version. Explorer loads whatever DLL the CLSID's InprocServer32 points at (per-user
/// registration wins over machine-wide), so that is what the status reports.
/// </summary>
internal static class ThumbnailProviderStatus
{
    public static string? EffectiveRegisteredPath(ThumbnailProviderFacts facts)
        => !string.IsNullOrWhiteSpace(facts.UserRegisteredPath) ? facts.UserRegisteredPath : facts.MachineRegisteredPath;

    public static string Describe(ThumbnailProviderFacts facts, ThumbnailSettings settings)
    {
        var effective = EffectiveRegisteredPath(facts);
        string provider;
        if (string.IsNullOrWhiteSpace(effective))
        {
            provider = facts.LocalDllExists
                ? "Provider: available, not registered"
                : $"Provider: not registered ({Path.GetFileName(facts.LocalDllPath)} is not next to this Glide.exe)";
        }
        else if (!facts.EffectiveDllExists)
        {
            provider = $"Provider: missing (registered path {effective} does not exist)";
        }
        else
        {
            var version = string.IsNullOrWhiteSpace(facts.EffectiveDllVersion) ? "unversioned, pre-5.0" : facts.EffectiveDllVersion;
            provider = $"Provider: active {version} ({effective})";
            if (!SamePath(effective, facts.LocalDllPath))
                provider += facts.LocalDllExists ? " - a different Glide copy than this one" : " - this Glide.exe has no provider of its own";
        }

        var user = string.IsNullOrWhiteSpace(facts.UserRegisteredPath) ? "no" : "yes";
        var machine = string.IsNullOrWhiteSpace(facts.MachineRegisteredPath) ? "no" : "yes";
        var architecture = Environment.Is64BitProcess ? "x64" : "x86";
        var maxSource = settings.MaxSourceSize > 0 ? $"{settings.MaxSourceSize} px" : "Automatic";
        var embedded = settings.PreferEmbedded ? "on" : "off";
        return $"{provider} | Registered: this user {user}, all users {machine} | Architecture: {architecture} | " +
               $"Formats routed to Glide: {facts.RoutedFormats} | Mode: {settings.Mode} | Quality: {settings.Quality} | " +
               $"Max decode: {maxSource} | Embedded previews: {embedded} | Glide: {facts.GlideVersion}";
    }

    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left.Trim().Trim('"')), Path.GetFullPath(right.Trim().Trim('"')),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Product version of a provider DLL, or null when it has no version resource (pre-4.8 builds).</summary>
    public static string? ReadDllVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!File.Exists(expanded)) return null;
            var info = FileVersionInfo.GetVersionInfo(expanded);
            var version = info.ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch { return null; }
    }

    public static bool RegisteredFileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
        catch { return false; }
    }
}
