namespace Glide.App.Services;

/// <summary>
/// Builds the small, privacy-safe record that explains why an external launch opened a new window,
/// a new tab, or reused an existing view. Only the file extension is recorded, never the path.
/// </summary>
internal static class LaunchDecisionLog
{
    internal static object Build(string kind, string? path, string externalOpenBehavior, string? duplicateBehavior,
        bool reuseSingleInstance, bool pendingWarmReceiver, bool speedBoostStandby, bool duplicateOpen,
        bool replacedDefaultHome, string outcome)
    {
        var ext = string.Empty;
        try { ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant(); } catch (ArgumentException) { /* path with invalid characters just logs no extension */ }
        return new
        {
            kind,
            ext,
            isGif = ext == ".gif",
            forwarded = true,
            reuseSingleInstance,
            externalOpenBehavior,
            duplicateBehavior,
            duplicateOpen,
            pendingWarmReceiver,
            speedBoostStandby,
            replacedDefaultHome,
            outcome
        };
    }
}
