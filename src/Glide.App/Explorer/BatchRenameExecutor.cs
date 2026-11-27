namespace Glide.App.Explorer;

/// <summary>Outcome of <see cref="BatchRenameExecutor.Execute"/>. When it failed, nothing was renamed unless <see cref="RollbackIncomplete"/>.</summary>
internal sealed record BatchRenameResult(bool Success, IReadOnlyList<(string From, string To)> Renamed, string? Error, bool RollbackIncomplete = false)
{
    public static BatchRenameResult Ok(IReadOnlyList<(string From, string To)> renamed) => new(true, renamed, null);
}

/// <summary>
/// Renames many files and folders as one unit (batch rename dialog, tag sidebar). Every item first moves to a unique
/// temporary name in its own folder, then to its final name, so swaps (a to b and b to a), chains and case-only changes
/// all work. When any step fails everything that already moved goes back to where it was.
/// </summary>
internal static class BatchRenameExecutor
{
    /// <summary>Test seam: moves one file or folder (from, to, isDirectory). Defaults to File.Move / Directory.Move.</summary>
    internal static Action<string, string, bool> MoveEntry { get; set; } = DefaultMove;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void DefaultMove(string from, string to, bool isDirectory)
    {
        if (isDirectory) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private sealed record Step(string From, string Temp, string To, bool IsDirectory);

    public static BatchRenameResult Execute(IReadOnlyList<(string From, string To)> moves)
    {
        if (moves.Count == 0) return BatchRenameResult.Ok([]);

        var sources = new HashSet<string>(moves.Select(m => Path.GetFullPath(m.From)), PathComparer);
        var targets = new HashSet<string>(PathComparer);
        var steps = new List<Step>(moves.Count);
        for (var i = 0; i < moves.Count; i++)
        {
            var from = Path.GetFullPath(moves[i].From);
            var to = Path.GetFullPath(moves[i].To);
            var isDirectory = Directory.Exists(from);
            if (!isDirectory && !File.Exists(from))
                return Fail($"'{Path.GetFileName(from)}' no longer exists.");
            if (!targets.Add(to))
                return Fail($"More than one item would be named '{Path.GetFileName(to)}'.");
            var problem = Glide.Core.Renaming.FileNameRules.Validate(Path.GetFileName(to));
            if (problem is not null) return Fail(problem);
            // A name taken by another item of this batch is fine (it is moved away first); anything else must not be overwritten.
            if (!sources.Contains(to) && (File.Exists(to) || Directory.Exists(to)))
                return Fail($"There is already a file or folder named '{Path.GetFileName(to)}'.");
            var temp = Path.Combine(Path.GetDirectoryName(from) ?? "", ".glide-batch-" + Guid.NewGuid().ToString("N")[..12] + "-" + i + ".tmp");
            steps.Add(new Step(from, temp, to, isDirectory));
        }

        var staged = new List<Step>(steps.Count);
        var finished = new List<Step>(steps.Count);
        try
        {
            foreach (var step in steps) { MoveEntry(step.From, step.Temp, step.IsDirectory); staged.Add(step); }
            foreach (var step in steps) { MoveEntry(step.Temp, step.To, step.IsDirectory); finished.Add(step); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var failedAt = staged.Count < steps.Count ? steps[staged.Count] : steps[finished.Count];
            var incomplete = Rollback(staged, finished);
            var reason = ex is ExplorerOperationException ? ex.Message : ExplorerFileOperations.DescribeError(ex, failedAt.From);
            return new BatchRenameResult(false, [], incomplete
                ? "Renaming stopped and some items could not be put back: " + reason
                : "Nothing was renamed: " + reason, incomplete);
        }
        return BatchRenameResult.Ok(steps.Select(s => (s.From, s.To)).ToArray());
    }

    /// <summary>Undoes what already moved: finals back to their temporary names, then every temporary name back to the original. Returns true when something could not be restored.</summary>
    private static bool Rollback(List<Step> staged, List<Step> finished)
    {
        var failed = false;
        for (var i = finished.Count - 1; i >= 0; i--)
            try { MoveEntry(finished[i].To, finished[i].Temp, finished[i].IsDirectory); } catch { failed = true; }
        for (var i = staged.Count - 1; i >= 0; i--)
            try { MoveEntry(staged[i].Temp, staged[i].From, staged[i].IsDirectory); } catch { failed = true; }
        return failed;
    }

    private static BatchRenameResult Fail(string message) => new(false, [], message);
}
