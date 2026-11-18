namespace Glide.App.Settings;

/// <summary>Stored values of the "Explorer default view" setting, in view-box order (the Settings combo shows the Explorer tab's labels).</summary>
public static class ExplorerDefaultViews
{
    public static readonly string[] All = Glide.App.Explorer.ExplorerViewModes.Names;
    public const string Default = "Large thumbnails";

    /// <summary>Maps a saved value to a current choice; 5.0.3 renamed "... icons" to "... thumbnails".</summary>
    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.EndsWith(" icons", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^" icons".Length] + " thumbnails";
        return All.FirstOrDefault(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)) ?? Default;
    }
}
