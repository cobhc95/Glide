namespace Glide.Core.Reset;

/// <summary>The folders and facts the planner needs; the app fills it from the real machine, tests from scratch folders.</summary>
/// <param name="SettingsDirectory">Where glide.settings.json lives (portable: beside Glide.exe, installed: %LOCALAPPDATA%\Glide, tests: GLIDE_SETTINGS_DIRECTORY).</param>
/// <param name="LocalGlideDirectory">%LOCALAPPDATA%\Glide (startup timing log, leftovers of older versions).</param>
/// <param name="TempDirectory">The temp folder Glide's diagnostics write into.</param>
/// <param name="InstallDirectory">The folder Glide.exe runs from; never touched.</param>
/// <param name="ImageExtensions">Every extension Glide registers (ImageFormatRegistry.Extensions).</param>
/// <param name="SettingsSchemaVersion">Schema stamped into a settings file created by the startup reset; null = never create one.</param>
/// <param name="DefaultSettingsJson">
/// A default settings object as glide.settings.json stores it (the app serialises its default settings). "All settings" writes
/// these defaults for every option; null = the options are removed instead (they still load as defaults).
/// </param>
public sealed record ResetEnvironment(
    string SettingsDirectory,
    string LocalGlideDirectory,
    string TempDirectory,
    string InstallDirectory,
    IReadOnlyList<string> ImageExtensions,
    int? SettingsSchemaVersion = null,
    string? DefaultSettingsJson = null);

/// <summary>One concrete thing to remove or change.</summary>
public abstract record ResetTarget
{
    public abstract string Describe();
}

/// <summary>A single file.</summary>
public sealed record FileTarget(string Path) : ResetTarget
{
    public override string Describe() => Path;
}

/// <summary>Files directly inside <see cref="Directory"/> that match a wildcard pattern.</summary>
public sealed record FilesMatchingTarget(string Directory, string Pattern) : ResetTarget
{
    public override string Describe() => System.IO.Path.Combine(Directory, Pattern);
}

/// <summary>A folder and everything in it.</summary>
public sealed record DirectoryTarget(string Path) : ResetTarget
{
    public override string Describe() => Path;
}

/// <summary>Sub-folders directly inside <see cref="Directory"/> that match a wildcard pattern (each deleted with its content).</summary>
public sealed record DirectoriesMatchingTarget(string Directory, string Pattern) : ResetTarget
{
    public override string Describe() => System.IO.Path.Combine(Directory, Pattern);
}

/// <summary>
/// Edits a JSON object file in place: removes the named top-level properties (they fall back to their
/// built-in defaults) and sets the named booleans. A missing file is created only when
/// <see cref="CreateWithSchemaVersion"/> is given and something has to be set.
/// </summary>
public sealed record JsonPropertiesTarget(
    string Path,
    IReadOnlyList<string> RemoveProperties,
    IReadOnlyDictionary<string, bool> SetBooleans,
    int? CreateWithSchemaVersion = null) : ResetTarget
{
    public override string Describe() =>
        $"{Path} ({string.Join(", ", RemoveProperties.Select(x => "remove " + x).Concat(SetBooleans.Select(x => $"{x.Key} = {(x.Value ? "true" : "false")}")))})";
}

/// <summary>
/// Puts every option of glide.settings.json back to its default (<see cref="AllSettingsReset"/>) and leaves
/// <see cref="ResetCatalog.AllSettingsKeptProperties"/> (schema version, hotkeys and gestures, window positions, remembered
/// folders, startup switches...) exactly as they are. A file that is not valid JSON cannot be repaired and is deleted.
/// </summary>
public sealed record SettingsFileResetTarget(string Path, string? DefaultSettingsJson) : ResetTarget
{
    public override string Describe() =>
        $"{Path} (every option back to its default; keeps hotkeys, mouse gestures, window sizes and positions, remembered folders and startup entries)";
}

/// <summary>A registry key (relative to HKEY_CURRENT_USER) with everything below it.</summary>
public sealed record RegistryTreeTarget(string SubKey) : ResetTarget
{
    public override string Describe() => @"HKCU\" + SubKey;
}

/// <summary>
/// A registry value under HKEY_CURRENT_USER. When <see cref="OnlyIfEquals"/> / <see cref="OnlyIfContains"/> is set the value is
/// removed only if the text matches (so a value another program has taken over is left alone).
/// </summary>
public sealed record RegistryValueTarget(string SubKey, string ValueName, string? OnlyIfEquals = null, string? OnlyIfContains = null) : ResetTarget
{
    public override string Describe() => $@"HKCU\{SubKey} [{(ValueName.Length == 0 ? "(Default)" : ValueName)}]";
}

/// <summary>Writes a DWORD under HKEY_CURRENT_USER (used to keep Glide from silently re-registering Open With).</summary>
public sealed record RegistryDwordTarget(string SubKey, string ValueName, int Value) : ResetTarget
{
    public override string Describe() => $@"HKCU\{SubKey} [{ValueName}] = {Value}";
}

public sealed record ResetItemPlan(ResetItemInfo Item, IReadOnlyList<ResetTarget> Targets);

/// <summary>What a selection will do. Items are in execution order.</summary>
public sealed record ResetPlan(ResetEnvironment Environment, IReadOnlyList<ResetItemPlan> Items)
{
    /// <summary>True when any selected item needs Glide to be closed first (the confirmation then says Glide restarts).</summary>
    public bool NeedsRestart => Items.Any(x => x.Item.NeedsRestart);

    public bool TouchesRegistry => Items.Any(x => x.Item.Id is ResetItemId.RegistryEntries or ResetItemId.StartupEntries);

    public bool Includes(ResetItemId id) => Items.Any(x => x.Item.Id == id);

    public IEnumerable<ResetTarget> AllTargets => Items.SelectMany(x => x.Targets);
}
