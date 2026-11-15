using System.Diagnostics;
using Glide.Core.Updates;

namespace Glide.App.Updates;

/// <summary>
/// Opens a release page in the default browser. The address is validated first (https, github.com, inside the Glide repository;
/// anything else becomes the repository's latest-release page) and handed to the shell as the file name only: no arguments, no
/// command line, no script, so it cannot be turned into anything but "open this web page".
/// </summary>
internal static class UpdateBrowserLauncher
{
    /// <summary>The process start information that <see cref="Open"/> uses (exposed so tests can check it without starting a browser).</summary>
    internal static ProcessStartInfo CreateStartInfo(string? url) => new()
    {
        FileName = UpdateUrlPolicy.ResolveReleaseUrl(url),
        UseShellExecute = true
    };

    /// <summary>Test seam: replaces the real process start (null = start the browser).</summary>
    internal static Action<ProcessStartInfo>? StartOverrideForTests { get; set; }

    public static bool Open(string? url)
    {
        try
        {
            var info = CreateStartInfo(url);
            if (StartOverrideForTests is { } fake) { fake(info); return true; }
            using var process = Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
