namespace Glide.Core.Renaming;

/// <summary>One file or folder to rename. A folder's whole name is its "name without extension".</summary>
public sealed record BatchRenameSource(string Path, bool IsDirectory);

public enum BatchRenameProblem
{
    None,
    /// <summary>The new name is not a legal file name.</summary>
    InvalidName,
    /// <summary>Two items of the batch would get the same name.</summary>
    DuplicateInBatch,
    /// <summary>Another file or folder that is not part of the batch already has the new name.</summary>
    ExistsOnDisk,
    /// <summary>The expression itself failed for this item.</summary>
    ExpressionFailed
}

/// <summary>One row of the preview: the old name, the new name and what (if anything) is wrong with it.</summary>
public sealed record BatchRenameItem(BatchRenameSource Source, string OldName, string NewName, BatchRenameProblem Problem, string? Message)
{
    public string OldPath => Source.Path;
    public bool Changed => !string.Equals(OldName, NewName, StringComparison.Ordinal);
    public bool HasProblem => Problem != BatchRenameProblem.None;

    /// <summary>The new name split into typed text and token parts, for the highlighted preview (rebuilt dialog only).</summary>
    public IReadOnlyList<RenameSegment>? Segments { get; init; }

    /// <summary>Why the name was left as it is ("no date taken"), or null. A skipped item is not a problem.</summary>
    public string? SkipReason { get; init; }

    public bool Skipped => SkipReason is not null;

    /// <summary>The item changes and nothing is wrong with it: it takes part in the rename.</summary>
    public bool WillRename => Changed && !HasProblem;

    /// <summary>The full path the item will have. Only meaningful without a problem.</summary>
    public string NewPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(OldPath) ?? "", NewName);
}

public sealed record BatchRenamePlan(IReadOnlyList<BatchRenameItem> Items, string? ExpressionError)
{
    public int ChangedCount => Items.Count(i => i.Changed);
    public int ProblemCount => Items.Count(i => i.HasProblem);

    /// <summary>How many items the rebuilt dialog renames: those that change and have no problem (the others stay as they are).</summary>
    public int WillRenameCount => Items.Count(i => i.WillRename);

    /// <summary>The moves of the items that change and have no problem, in order.</summary>
    public IReadOnlyList<(string From, string To)> SafeMoves => Items.Where(i => i.WillRename).Select(i => (i.OldPath, i.NewPath)).ToArray();

    /// <summary>Rename may run: the expression is valid, nothing conflicts and at least one name changes.</summary>
    public bool CanApply => ExpressionError is null && ProblemCount == 0 && ChangedCount > 0;

    /// <summary>The (old path, new path) pairs of the items that change, in order.</summary>
    public IReadOnlyList<(string From, string To)> Moves => Items.Where(i => i.Changed).Select(i => (i.OldPath, i.NewPath)).ToArray();

    /// <summary>Why Rename is disabled, for the status line of the dialog; null when it is enabled or nothing was typed.</summary>
    public string? Summary
    {
        get
        {
            if (ExpressionError is not null) return ExpressionError;
            var problems = ProblemCount;
            if (problems > 0) return problems == 1 ? "1 name can't be used (shown in red)." : $"{problems} names can't be used (shown in red).";
            return ChangedCount == 0 ? null : ChangedCount == 1 ? "1 item will be renamed." : $"{ChangedCount} items will be renamed.";
        }
    }
}

/// <summary>
/// Turns a batch rename expression (or any "new name for every item" function) into a checked preview: illegal names,
/// two items with one name and names already taken by files outside the batch are flagged, so Rename can be disabled.
/// Pure: the file system is only asked through <c>pathExists</c>.
/// </summary>
public static class BatchRenamePlanner
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static BatchRenamePlan Plan(IReadOnlyList<BatchRenameSource> sources, string? expressionText, bool regex, bool matchCase, Func<string, bool> pathExists)
    {
        var expression = BatchRenameExpression.Parse(expressionText, regex, matchCase);
        if (expression.Error is not null)
            return new BatchRenamePlan(sources.Select(s => new BatchRenameItem(s, NameOf(s), NameOf(s), BatchRenameProblem.None, null)).ToArray(), expression.Error);

        var failures = new Dictionary<BatchRenameSource, string>();
        var plan = PlanNames(sources, (source, index) =>
        {
            var name = NameOf(source);
            var (stem, extension) = Split(name, source.IsDirectory);
            if (!expression.TryApply(stem, index, sources.Count, out var newStem, out var error))
            {
                failures[source] = error ?? "The expression failed.";
                return name;
            }
            return newStem + extension;
        }, pathExists);
        if (failures.Count == 0) return plan;
        var items = plan.Items.Select(i => failures.TryGetValue(i.Source, out var message) ? i with { Problem = BatchRenameProblem.ExpressionFailed, Message = message } : i).ToArray();
        return plan with { Items = items };
    }

    /// <summary>The checked plan for an arbitrary new-name function (the tag sidebar uses it).</summary>
    public static BatchRenamePlan PlanNames(IReadOnlyList<BatchRenameSource> sources, Func<BatchRenameSource, int, string> newNameOf, Func<string, bool> pathExists)
    {
        var rows = new List<(BatchRenameSource Source, string Old, string New)>(sources.Count);
        for (var i = 0; i < sources.Count; i++) rows.Add((sources[i], NameOf(sources[i]), newNameOf(sources[i], i)));

        // How many items end up with each full path (unchanged items keep theirs, so a rename can't take their name).
        var finalPaths = rows.GroupBy(r => FinalPath(r.Source, r.New), PathComparer).ToDictionary(g => g.Key, g => g.Count(), PathComparer);
        var batchPaths = new HashSet<string>(rows.Select(r => r.Source.Path), PathComparer);

        var items = new List<BatchRenameItem>(rows.Count);
        foreach (var (source, oldName, newName) in rows)
        {
            var problem = BatchRenameProblem.None;
            string? message = null;
            if (!string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                var invalid = FileNameRules.Validate(newName);
                if (invalid is not null) { problem = BatchRenameProblem.InvalidName; message = invalid; }
                else if (finalPaths[FinalPath(source, newName)] > 1)
                {
                    problem = BatchRenameProblem.DuplicateInBatch;
                    message = $"More than one item would be named \"{newName}\".";
                }
                else
                {
                    var target = FinalPath(source, newName);
                    // A case-only change (a.jpg to A.jpg) "exists" as the item itself; another batch item's old name is free.
                    if (!batchPaths.Contains(target) && pathExists(target))
                    {
                        problem = BatchRenameProblem.ExistsOnDisk;
                        message = $"There is already a file or folder named \"{newName}\".";
                    }
                }
            }
            items.Add(new BatchRenameItem(source, oldName, newName, problem, message));
        }
        return new BatchRenamePlan(items, null);
    }

    private static string FinalPath(BatchRenameSource source, string name) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source.Path) ?? "", name);

    /// <summary>The item's current file or folder name.</summary>
    public static string NameOf(BatchRenameSource source) => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(source.Path));

    /// <summary>Splits a name into the part an expression may change and the extension (with its dot) that is kept.</summary>
    public static (string Stem, string Extension) Split(string name, bool isDirectory)
    {
        if (isDirectory) return (name, "");
        var dot = name.LastIndexOf('.');
        // ".gitignore" and "name." have no extension: the whole name is the stem.
        return dot <= 0 || dot == name.Length - 1 ? (name, "") : (name[..dot], name[dot..]);
    }
}
