namespace Glide.Core;

/// <summary>
/// Video suffixes Glide accepts as Window-in-Window overlay content. Playback is delegated to
/// Windows Media Foundation, so a listed suffix is a routing promise, not a codec guarantee: H.264,
/// MPEG-4 Part 2, VP8/VP9, WMV and MPEG-2 play on a stock Windows 10/11 install, while HEVC and AV1
/// need the matching Microsoft Store video extension. Videos are overlay-only; they never enter the
/// main viewer's image navigation (see <see cref="ImageFormatRegistry"/>).
/// </summary>
public static class VideoFormatRegistry
{
    public static IReadOnlyList<string> Extensions { get; } = new[]
    {
        ".mp4", ".m4v", ".mov", ".qt", ".mkv", ".webm", ".avi", ".wmv", ".asf",
        ".3gp", ".3g2", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".m2v", ".vob"
    };

    private static readonly HashSet<string> ExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsVideo(string? path) =>
        !string.IsNullOrWhiteSpace(path) && ExtensionSet.Contains(Path.GetExtension(path));

    /// <summary>
    /// The video before/after <paramref name="current"/> among <paramref name="folderFiles"/>, in
    /// <paramref name="order"/>, wrapping around at either end. Non-video files are ignored. Returns
    /// <paramref name="current"/> itself when it is the only video, and null when there is none.
    /// </summary>
    public static string? Sibling(string current, IEnumerable<string> folderFiles, int direction, IComparer<string> order)
    {
        var videos = folderFiles.Where(IsVideo)
            .OrderBy(Path.GetFileName, order)
            .ThenBy(x => x, order)
            .ToList();
        if (videos.Count == 0) return null;
        var index = videos.FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            // The current file was deleted or renamed: continue from where it would sort.
            var name = Path.GetFileName(current);
            var insert = videos.FindIndex(x => order.Compare(Path.GetFileName(x), name) > 0);
            index = insert < 0 ? videos.Count : insert;
            return videos[(direction > 0 ? index : index - 1 + videos.Count) % videos.Count];
        }
        return videos[((index + Math.Sign(direction)) % videos.Count + videos.Count) % videos.Count];
    }
}
