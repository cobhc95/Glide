namespace Glide.Core.Workspace;

public enum CustomLaunchKind
{
    /// <summary>No path was configured.</summary>
    NotSet,
    /// <summary>A supported image file: open it in an image tab.</summary>
    Image,
    /// <summary>A readable folder: open it in an Explorer tab.</summary>
    Folder,
    /// <summary>The path does not exist (moved, deleted, drive or share offline).</summary>
    Missing,
    /// <summary>The path is a file Glide cannot open as an image.</summary>
    UnsupportedFile,
    /// <summary>The folder exists but cannot be listed (permissions, offline share).</summary>
    Inaccessible
}

/// <summary>
/// The outcome of resolving the "Custom" path of "When Glide opens" / "New tab opens" (5.0.0).
/// A folder opens as an Explorer tab, an image file opens as an image tab, and anything else falls
/// back to the Welcome page with <see cref="Notice"/> as a short non-blocking explanation.
/// </summary>
public readonly record struct CustomLaunchTarget(CustomLaunchKind Kind, string Path, string? Notice)
{
    public bool IsUsable => Kind is CustomLaunchKind.Image or CustomLaunchKind.Folder;

    /// <summary>Resolves a configured custom path. <paramref name="what"/> names the setting for messages.</summary>
    public static CustomLaunchTarget Resolve(string? configuredPath, string what = "Startup")
    {
        var raw = Normalize(configuredPath);
        if (raw.Length == 0)
            return new(CustomLaunchKind.NotSet, "", $"{what} is set to Custom but no image or folder is chosen. Choose one in Settings > General & startup.");

        string full;
        try { full = System.IO.Path.GetFullPath(raw); }
        catch (Exception)
        {
            return new(CustomLaunchKind.Missing, raw, $"{what} path is not a valid path: {raw}");
        }

        try
        {
            if (Directory.Exists(full))
            {
                if (!IsReadableDirectory(full))
                    return new(CustomLaunchKind.Inaccessible, full, $"{what} folder cannot be opened: {full}");
                return new(CustomLaunchKind.Folder, full, null);
            }
            if (File.Exists(full))
            {
                return ImageNavigator.IsSupported(full)
                    ? new(CustomLaunchKind.Image, full, null)
                    : new(CustomLaunchKind.UnsupportedFile, full, $"{what} file is not a supported image: {full}");
            }
        }
        catch (Exception)
        {
            return new(CustomLaunchKind.Inaccessible, full, $"{what} path cannot be accessed: {full}");
        }
        return new(CustomLaunchKind.Missing, full, $"{what} path was not found: {full}");
    }

    /// <summary>Trims, strips quotes pasted from Explorer's "Copy as path" and expands %VARIABLES%.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var value = path.Trim().Trim('"').Trim();
        if (value.Contains('%')) value = Environment.ExpandEnvironmentVariables(value);
        return value;
    }

    private static bool IsReadableDirectory(string folder)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception) { return false; }
    }
}
