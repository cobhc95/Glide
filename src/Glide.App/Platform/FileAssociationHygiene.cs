namespace Glide.App.Platform;

/// <summary>
/// A read-only view of one registry hive's <c>Software\Classes</c> as far as Glide's own registration is concerned.
/// Real implementation: <see cref="WindowsFileAssociationRegistration"/>; tests use a fake so nothing ever touches
/// the real registry.
/// </summary>
internal interface IGlideClassesView
{
    /// <summary>
    /// Glide's class keys present in the hive: <c>Glide.Image</c>, <c>Glide.Image.*</c> and
    /// <c>Applications\Glide.exe</c> (names relative to <c>Software\Classes</c>).
    /// </summary>
    IReadOnlyList<string> GlideClassNames();

    /// <summary>Default value of <c>&lt;class&gt;\shell\open\command</c>, or null.</summary>
    string? OpenCommand(string className);
}

/// <summary>What startup self-healing should remove from the current user's hive.</summary>
/// <param name="StaleClassNames">Class keys whose command points at a missing or dev-folder executable.</param>
/// <param name="RemoveAll">True when no live per-user Glide class remains, so the whole per-user registration goes.</param>
internal sealed record StaleRegistrationPlan(IReadOnlyList<string> StaleClassNames, bool RemoveAll)
{
    public bool IsEmpty => StaleClassNames.Count == 0;
    public static StaleRegistrationPlan None { get; } = new(Array.Empty<string>(), false);
}

/// <summary>
/// Pure policy for keeping a development / portable Glide from hijacking the installed one's file associations.
/// A dev build (<c>dist\Glide.exe</c>, <c>bin\Debug</c>, an agent worktree) that wrote per-user (HKCU)
/// <c>Glide.Image.*</c> ProgIDs overrides the installed machine-wide (HKLM) registration, so double-clicks opened the
/// dev build and its icons vanished when the build folder was cleaned.
/// </summary>
internal static class FileAssociationHygiene
{
    private const int MaxParentLevels = 12;

    /// <summary>
    /// True when the executable lives in a source tree or build output: a parent folder holding both
    /// <c>build.cmd</c> and <c>Glide.sln</c>, an agent worktree (<c>.claude\worktrees</c>), <c>bin\Debug|Release</c> or
    /// <c>obj</c>. <paramref name="exists"/> tells whether a file or folder exists.
    /// </summary>
    internal static bool IsInSourceTree(string? executablePath, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        var segments = executablePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (segment.Equals("obj", StringComparison.OrdinalIgnoreCase)) return true;
            if (segment.Equals(".claude", StringComparison.OrdinalIgnoreCase) &&
                segments[i + 1].Equals("worktrees", StringComparison.OrdinalIgnoreCase)) return true;
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) && i + 2 < segments.Length &&
                (segments[i + 1].Equals("Debug", StringComparison.OrdinalIgnoreCase) ||
                 segments[i + 1].Equals("Release", StringComparison.OrdinalIgnoreCase))) return true;
        }

        try
        {
            var directory = SafeDirectoryName(executablePath);
            for (var level = 0; directory is not null && level < MaxParentLevels; level++)
            {
                if (exists(Combine(directory, "build.cmd")) && exists(Combine(directory, "Glide.sln"))) return true;
                directory = SafeDirectoryName(directory);
            }
        }
        catch { /* unreadable parents: treat as not a source tree */ }
        return false;
    }

    /// <summary>
    /// The executable named by a shell command such as <c>"C:\Program Files\Glide\Glide.exe" "%1"</c>, or null.
    /// </summary>
    internal static string? ExecutableOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : text.Split(' ', 2)[0];
    }

    /// <summary>
    /// True when any Glide ProgID / application key in the machine-wide hive runs an existing Glide.exe. Whatever the
    /// path: a machine-wide registration means per-user auto-registration is never needed.
    /// </summary>
    internal static bool HasMachineWideRegistration(IGlideClassesView machine, Func<string, bool> fileExists)
    {
        foreach (var name in machine.GlideClassNames())
        {
            var exe = ExecutableOf(machine.OpenCommand(name));
            if (exe is not null && IsGlideExecutable(exe) && fileExists(exe)) return true;
        }
        return false;
    }

    /// <summary>
    /// Startup auto-registration (per user). Never from a source tree / dev build, never when a machine-wide
    /// registration exists, plus the existing opt-out and per-version rules.
    /// </summary>
    internal static bool ShouldAutoRegister(bool inSourceTree, bool anyMachineWideRegistration, bool optedOut,
        string? registeredVersion, string currentVersion) =>
        !inSourceTree &&
        !anyMachineWideRegistration &&
        !optedOut &&
        !string.Equals(registeredVersion, currentVersion, StringComparison.Ordinal);

    /// <summary>
    /// Self-healing runs from any Glide that is not itself a dev build (the installed one above all; a portable copy
    /// also replaces a registration whose copy vanished). A dev build never touches the user's registration.
    /// </summary>
    internal static bool ShouldHealUserRegistration(bool inSourceTree) => !inSourceTree;

    /// <summary>
    /// Which per-user Glide classes are stale: the command points at an executable that is not this one and that is
    /// either gone or inside a source tree / dist folder. Classes pointing at another existing, non-dev Glide.exe
    /// (a portable copy the user chose) are left alone.
    /// </summary>
    internal static StaleRegistrationPlan PlanStaleCleanup(IGlideClassesView user, string thisExecutable,
        Func<string, bool> fileExists, Func<string, bool> pathExists)
    {
        var stale = new List<string>();
        var live = 0;
        foreach (var name in user.GlideClassNames())
        {
            var exe = ExecutableOf(user.OpenCommand(name));
            if (exe is null) { live++; continue; } // nothing to judge; leave it
            if (SamePath(exe, thisExecutable)) { live++; continue; }
            if (!fileExists(exe) || IsInSourceTree(exe, pathExists)) stale.Add(name);
            else live++;
        }
        return stale.Count == 0
            ? StaleRegistrationPlan.None
            : new StaleRegistrationPlan(stale, RemoveAll: live == 0);
    }

    private static bool IsGlideExecutable(string path) =>
        string.Equals(Path.GetFileName(path.Replace('/', '\\')), "Glide.exe", StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string Combine(string directory, string name) => directory.TrimEnd('\\', '/') + "\\" + name;

    private static string? SafeDirectoryName(string path)
    {
        var trimmed = path.Replace('/', '\\').TrimEnd('\\');
        var index = trimmed.LastIndexOf('\\');
        if (index <= 0) return null;
        var parent = trimmed[..index];
        return parent.EndsWith(':') ? parent + "\\" : parent;
    }
}
