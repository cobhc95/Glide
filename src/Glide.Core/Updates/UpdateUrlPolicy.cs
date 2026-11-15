namespace Glide.Core.Updates;

/// <summary>
/// Everything Glide accepts about where an update lives. The release page is only ever opened in the user's browser,
/// so the one thing that matters is that the address really is a GitHub page of the Glide repository: https, the host
/// github.com exactly, the default port, no user name or password, and a path inside /cobhc95/Glide/. Anything else
/// (including look-alike hosts and "userinfo" tricks) falls back to the repository's own releases page.
/// </summary>
public static class UpdateUrlPolicy
{
    public const string Owner = "cobhc95";
    public const string Repository = "Glide";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/cobhc95/Glide/releases/latest";
    public const string FallbackReleaseUrl = "https://github.com/cobhc95/Glide/releases/latest";
    private const string RepositoryPathPrefix = "/cobhc95/Glide/";

    /// <summary>True when <paramref name="url"/> is an https GitHub address inside the Glide repository.</summary>
    public static bool IsSafeReleaseUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
        // Spaces, control characters, backslashes and escaped dots/slashes are never part of a real release address.
        foreach (var c in url)
            if (c <= ' ' || c == '\\' || c == '\u007f') return false;
        if (url.Contains("%2e", StringComparison.OrdinalIgnoreCase) || url.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("%5c", StringComparison.OrdinalIgnoreCase) || url.Contains("/..", StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return false;
        if (!string.Equals(parsed.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo) || !parsed.IsDefaultPort) return false;
        if (!parsed.AbsolutePath.StartsWith(RepositoryPathPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>The address to open for a release: the validated <paramref name="htmlUrl"/>, otherwise the repository's latest-release page.</summary>
    public static string ResolveReleaseUrl(string? htmlUrl) =>
        IsSafeReleaseUrl(htmlUrl, out var uri) ? uri.AbsoluteUri : FallbackReleaseUrl;

    /// <summary>Redirects of the update request are followed only to https on api.github.com or github.com (default port, no user info).</summary>
    public static bool IsAllowedRedirect(Uri? target)
    {
        if (target is null || !target.IsAbsoluteUri) return false;
        if (!string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(target.UserInfo) || !target.IsDefaultPort) return false;
        return string.Equals(target.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target.Host, "github.com", StringComparison.OrdinalIgnoreCase);
    }
}
