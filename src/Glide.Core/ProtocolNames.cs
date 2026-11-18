using System.Security.Cryptography;
using System.Text;

namespace Glide.Core;

/// <summary>
/// Single source for the external-open IPC identity strings. The pipe + presence names are a
/// wire-compat surface (changing them orphans old/new coexistence), so they live here rather
/// than scattered across broker code (4.5.2 audit).
///
/// 5.0: both names are scoped to the user (<see cref="UserScoped"/>). Named pipes are machine-global, so
/// with a shared name another account could create the pipe first and stop this user's launches from
/// reaching their running Glide. The presence sentinel moved to v4 together with the pipe so 4.x and 5.x
/// processes never wait on each other's names; each version forwards only to its own kind.
/// </summary>
public static class ProtocolNames
{
    public const string PipeName = "Glide3.ExternalOpen.v2";
    public const string PresenceName = @"Local\Glide3.ProcessPresence.v4";
    public const string ProtocolHeader = "GLIDE3\t2";

    /// <summary>
    /// Appends a short, stable hash of <paramref name="userKey"/> (the user's SID) to <paramref name="baseName"/>.
    /// The hash keeps the name short and avoids putting the raw SID in a kernel object name.
    /// </summary>
    public static string UserScoped(string baseName, string userKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes((userKey ?? string.Empty).ToUpperInvariant()));
        return baseName + "." + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// The key that scopes one single-instance family: the user's SID, plus the settings directory when
    /// GLIDE_SETTINGS_DIRECTORY isolates a profile. An isolated (test/diagnostic) profile therefore elects and
    /// forwards only among processes that share that directory and can never forward to, or receive launches
    /// from, the user's normal Glide. Without the override the key is exactly the user key (wire-compatible).
    /// </summary>
    public static string InstanceKey(string userKey, string? settingsDirectoryOverride)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectoryOverride)) return userKey;
        string normalized;
        try { normalized = Path.GetFullPath(settingsDirectoryOverride).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { normalized = settingsDirectoryOverride.Trim(); }
        return userKey + "|profile:" + normalized.ToUpperInvariant();
    }
}
