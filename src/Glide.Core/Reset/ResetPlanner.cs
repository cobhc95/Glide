using Glide.Core.Data;
namespace Glide.Core.Reset;

/// <summary>
/// Turns a selection of reset items into concrete targets. Pure: it only builds paths and names, it never touches the disk
/// or the registry. Every target lies in Glide's own locations (settings folder, %LOCALAPPDATA%\Glide, Glide's temp files,
/// HKCU Glide / Open With / thumbnail / Run entries); <see cref="ResetPathGuard"/> re-checks that before anything is deleted.
/// </summary>
public static class ResetPlanner
{
    public static ResetPlan Plan(ResetEnvironment environment, IEnumerable<ResetItemId> selected)
    {
        var chosen = selected.ToHashSet();
        var items = new List<ResetItemPlan>();
        // Catalogue order = execution order: "All settings" first, so the startup switches written last survive it.
        foreach (var info in ResetCatalog.Items)
            if (chosen.Contains(info.Id))
                items.Add(new ResetItemPlan(info, TargetsFor(environment, info.Id)));
        return new ResetPlan(environment, items);
    }

    public static IReadOnlyList<ResetTarget> TargetsFor(ResetEnvironment env, ResetItemId id)
    {
        var settingsFile = Path.Combine(env.SettingsDirectory, ResetCatalog.SettingsFileName);
        var targets = new List<ResetTarget>();
        var none = (IReadOnlyDictionary<string, bool>)new Dictionary<string, bool>();
        switch (id)
        {
            case ResetItemId.AllSettings:
                // Options only: hotkeys, window positions, remembered folders, the schema version... are kept (they belong to
                // the other items or are not settings). The Settings window's Restore defaults runs the same AllSettingsReset.
                targets.Add(new SettingsFileResetTarget(settingsFile, env.DefaultSettingsJson));
                targets.Add(new FileTarget(Path.Combine(env.SettingsDirectory, ResetCatalog.LaunchPolicyFileName)));
                targets.Add(new FileTarget(Path.Combine(env.SettingsDirectory, ResetCatalog.PrintSettingsFileName)));
                targets.Add(new FileTarget(Path.Combine(env.SettingsDirectory, ResetCatalog.ThumbnailSettingsFileName)));
                // Leftovers of an interrupted atomic save (settings file + ".<pid>.<guid>.tmp").
                targets.Add(new FilesMatchingTarget(env.SettingsDirectory, ResetCatalog.SettingsFileName + ".*.tmp"));
                break;

            case ResetItemId.HotkeysAndGestures:
                targets.Add(new JsonPropertiesTarget(settingsFile, ResetCatalog.HotkeyGestureProperties, none));
                break;

            case ResetItemId.ProfilesAndPresets:
                targets.Add(new FileTarget(Path.Combine(env.SettingsDirectory, ResetCatalog.ProfilesFileName)));
                targets.Add(new JsonPropertiesTarget(settingsFile, ResetCatalog.ProfileProperties, none));
                break;

            case ResetItemId.WindowPlacements:
                targets.Add(new JsonPropertiesTarget(settingsFile, ResetCatalog.WindowPlacementProperties, none));
                break;

            case ResetItemId.RecentHistory:
                targets.Add(new FileTarget(Path.Combine(env.SettingsDirectory, ResetCatalog.HistoryFileName)));
                break;

            case ResetItemId.LastSession:
                targets.Add(new FileTarget(Path.Combine(env.SettingsDirectory, ResetCatalog.SessionFileName)));
                targets.Add(new FileTarget(Path.Combine(env.SettingsDirectory, ResetCatalog.OverlayLayoutFileName)));
                targets.Add(new FileTarget(Path.Combine(env.SettingsDirectory, ResetCatalog.OverlayLayoutFileName + ".bak")));
                targets.Add(new JsonPropertiesTarget(settingsFile, ResetCatalog.SessionLocationProperties, none));
                break;

            case ResetItemId.Caches:
                // Glide keeps no on-disk image cache (decoded frames live in memory): what is on disk is the thumbnail
                // settings file, the launch-policy sidecar (rebuilt on the next save), the startup timing log and temp files.
                targets.Add(new FileTarget(Path.Combine(env.SettingsDirectory, ResetCatalog.ThumbnailSettingsFileName)));
                targets.Add(new FileTarget(Path.Combine(env.SettingsDirectory, ResetCatalog.LaunchPolicyFileName)));
                targets.Add(new DirectoryTarget(Path.Combine(env.LocalGlideDirectory, ResetCatalog.StartupDiagnosticsFolderName)));
                targets.Add(new FileTarget(Path.Combine(env.TempDirectory, "glide-diagnostic-transfer.jpg")));
                targets.Add(new DirectoryTarget(Path.Combine(env.TempDirectory, "glide_diagnostic_fixtures")));
                targets.Add(new FilesMatchingTarget(env.TempDirectory, "glide_bench_*.tsv"));
                // Files of the optional video decoder older Glide versions downloaded (no longer used; see LegacyVideoDecoderCleanup).
                targets.Add(LegacyVideoDecoderCleanup.Target(env));
                break;

            case ResetItemId.RegistryEntries:
                targets.Add(new RegistryTreeTarget(ResetCatalog.ApplicationKey));
                targets.Add(new RegistryTreeTarget(ResetCatalog.ProgIdKey));
                targets.Add(new RegistryTreeTarget(ResetCatalog.CapabilitiesKey));
                targets.Add(new RegistryTreeTarget(ResetCatalog.ThumbnailConfigKey));
                targets.Add(new RegistryTreeTarget($@"Software\Classes\CLSID\{ResetCatalog.ThumbnailProviderClsid}"));
                targets.Add(new RegistryValueTarget(ResetCatalog.RegisteredApplicationsKey, ResetCatalog.RegisteredApplicationName,
                    OnlyIfEquals: ResetCatalog.CapabilitiesKey));
                foreach (var extension in env.ImageExtensions)
                {
                    targets.Add(new RegistryValueTarget($@"Software\Classes\{extension}\OpenWithProgids", ResetCatalog.ProgId));
                    targets.Add(new RegistryValueTarget($@"Software\Classes\{extension}\ShellEx\{ResetCatalog.ThumbnailHandlerGuid}", string.Empty,
                        OnlyIfEquals: ResetCatalog.ThumbnailProviderClsid));
                }
                targets.Add(new RegistryValueTarget(ResetCatalog.StateKey, ResetCatalog.OpenWithVersionValue));
                // Same as "Remove Glide Open With entries": remember the removal, otherwise the next start registers Glide again.
                targets.Add(new RegistryDwordTarget(ResetCatalog.StateKey, ResetCatalog.OpenWithOptOutValue, 1));
                break;

            case ResetItemId.StartupEntries:
                targets.Add(new RegistryValueTarget(ResetCatalog.RunKey, ResetCatalog.RunValueName));
                // Without these two switches Glide would stay resident / re-add itself to the Run key on the next Apply.
                targets.Add(new JsonPropertiesTarget(settingsFile, [],
                    ResetCatalog.StartupSwitchProperties.ToDictionary(x => x, _ => false),
                    env.SettingsSchemaVersion));
                break;
        }
        return targets;
    }
}

/// <summary>Refuses any path outside Glide's own folders; the executor calls it before every delete.</summary>
public static class ResetPathGuard
{
    private static readonly string[] NeverDeleteExtensions = [".exe", ".dll", ".manifest", ".msi"];

    public static string? Refusal(ResetTarget target, ResetEnvironment env) => target switch
    {
        FileTarget f => FileRefusal(f.Path, env),
        FilesMatchingTarget m => ListingRefusal(m.Directory, env) ?? PatternRefusal(m.Pattern),
        DirectoryTarget d => DirectoryRefusal(d.Path, env),
        DirectoriesMatchingTarget m => ListingRefusal(m.Directory, env) ?? PatternRefusal(m.Pattern),
        JsonPropertiesTarget j => FileRefusal(j.Path, env),
        SettingsFileResetTarget r => FileRefusal(r.Path, env),
        _ => null,
    };

    /// <summary>The check for one file or folder found by a wildcard target.</summary>
    public static string? MatchRefusal(string path, bool isDirectory, ResetEnvironment env) =>
        isDirectory ? DirectoryRefusal(path, env) : FileRefusal(path, env);

    // A wildcard target only lists a folder; each match is checked on its own (MatchRefusal).
    private static string? ListingRefusal(string directory, ResetEnvironment env) =>
        !IsRooted(directory) ? "the path is not absolute"
        : InsideAllowedRoot(Full(directory), env) ? null : "the folder is outside Glide's own folders";

    private static string? PatternRefusal(string pattern) =>
        pattern.Contains('\\') || pattern.Contains('/') || pattern.Length < 3 || pattern.All(c => c is '*' or '?' or '.')
            ? "the pattern is too broad" : null;

    private static string? FileRefusal(string path, ResetEnvironment env)
    {
        if (!IsRooted(path)) return "the path is not absolute";
        var extension = Path.GetExtension(path);
        if (NeverDeleteExtensions.Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase)))
            return "program files are never removed";
        var directory = Path.GetDirectoryName(Full(path));
        return directory is not null && InsideAllowedRoot(directory, env) ? null : "the file is outside Glide's own folders";
    }

    private static string? DirectoryRefusal(string path, ResetEnvironment env)
    {
        if (!IsRooted(path)) return "the path is not absolute";
        var full = Full(path);
        // Never a root folder, and never a folder that contains the program or Glide's settings folder.
        if (Path.GetPathRoot(full) is { } root && string.Equals(Trim(root), Trim(full), StringComparison.OrdinalIgnoreCase)) return "a drive root is never removed";
        if (Contains(full, Full(env.InstallDirectory)) || Contains(full, Full(env.SettingsDirectory)))
            return "it contains the Glide program or settings folder";
        if (IsSame(full, Full(env.LocalGlideDirectory)) || IsSame(full, Full(env.TempDirectory)))
            return "Glide's data folder or the temp folder itself is never removed";
        return InsideAllowedRoot(full, env) ? null : "the folder is outside Glide's own folders";
    }

    private static bool InsideAllowedRoot(string directory, ResetEnvironment env) =>
        Contains(Full(env.SettingsDirectory), directory) || Contains(Full(env.LocalGlideDirectory), directory) || Contains(Full(env.TempDirectory), directory);

    private static bool IsRooted(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);
    private static string Full(string path) => Trim(Path.GetFullPath(path));
    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool IsSame(string a, string b) => string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="inner"/> equals or lies below <paramref name="outer"/>.</summary>
    private static bool Contains(string outer, string inner) =>
        IsSame(outer, inner) || Trim(inner).StartsWith(Trim(outer) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
