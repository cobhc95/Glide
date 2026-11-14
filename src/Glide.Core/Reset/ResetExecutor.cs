using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glide.Core.Reset;

public enum ResetTargetOutcome { Removed, NothingToRemove, Failed, Refused }

public sealed record ResetTargetResult(ResetTarget Target, ResetTargetOutcome Outcome, string? Detail = null)
{
    public bool IsFailure => Outcome is ResetTargetOutcome.Failed or ResetTargetOutcome.Refused;
}

public sealed record ResetItemResult(ResetItemInfo Item, IReadOnlyList<ResetTargetResult> Targets)
{
    public IReadOnlyList<ResetTargetResult> Failures => Targets.Where(x => x.IsFailure).ToArray();
    public bool Succeeded => Failures.Count == 0;
    /// <summary>Targets that actually existed and were removed or changed.</summary>
    public int ChangedCount => Targets.Count(x => x.Outcome == ResetTargetOutcome.Removed);
}

public sealed record ResetReport(IReadOnlyList<ResetItemResult> Items)
{
    public bool AllSucceeded => Items.All(x => x.Succeeded);
    public IReadOnlyList<ResetItemResult> FailedItems => Items.Where(x => !x.Succeeded).ToArray();

    /// <summary>One line per item ("Image and thumbnail caches: 3 removed" / "... failed: reason"), then the details of failures.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        foreach (var item in Items)
        {
            if (item.Succeeded)
                text.Append(item.Item.Title).Append(": ").AppendLine(item.ChangedCount == 0 ? "nothing to remove" : $"{item.ChangedCount} removed or changed");
            else
            {
                text.Append(item.Item.Title).AppendLine(": not completed");
                foreach (var failure in item.Failures)
                    text.Append("    ").Append(failure.Target.Describe()).Append(" - ").AppendLine(failure.Detail ?? failure.Outcome.ToString());
            }
        }
        return text.ToString().TrimEnd();
    }
}

public sealed class ResetExecutorOptions
{
    /// <summary>How often a delete is tried when the file is in use (another Glide window or process, a virus scanner).</summary>
    public int Attempts { get; init; } = 5;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(300);
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;
}

/// <summary>
/// Runs a <see cref="ResetPlan"/>. Every target is attempted on its own: a locked file fails that item (reported with
/// the reason) and the others still run. Nothing outside Glide's own locations is ever touched (<see cref="ResetPathGuard"/>).
/// </summary>
public static class ResetExecutor
{
    public static ResetReport Execute(ResetPlan plan, IResetFileSystem fs, IResetRegistry registry, ResetExecutorOptions? options = null)
    {
        options ??= new ResetExecutorOptions();
        var results = new List<ResetItemResult>();
        foreach (var item in plan.Items)
        {
            var targetResults = new List<ResetTargetResult>();
            foreach (var target in item.Targets)
            {
                if (ResetPathGuard.Refusal(target, plan.Environment) is { } refusal)
                {
                    targetResults.Add(new ResetTargetResult(target, ResetTargetOutcome.Refused, "refused: " + refusal));
                    continue;
                }
                targetResults.Add(ExecuteTarget(target, plan.Environment, fs, registry, options));
            }
            results.Add(new ResetItemResult(item.Item, targetResults));
        }
        return new ResetReport(results);
    }

    private static ResetTargetResult ExecuteTarget(ResetTarget target, ResetEnvironment env, IResetFileSystem fs, IResetRegistry registry, ResetExecutorOptions options)
    {
        try
        {
            return target switch
            {
                FileTarget f => DeleteFile(f, f.Path, fs, options),
                FilesMatchingTarget m => DeleteMatches(m, isDirectory: false, env, fs, options),
                DirectoryTarget d => DeleteDirectory(d, d.Path, fs, options),
                DirectoriesMatchingTarget m => DeleteMatches(m, isDirectory: true, env, fs, options),
                JsonPropertiesTarget j => EditJson(j, fs, options),
                RegistryTreeTarget r => registry.DeleteKeyTree(r.SubKey)
                    ? new ResetTargetResult(r, ResetTargetOutcome.Removed) : new ResetTargetResult(r, ResetTargetOutcome.NothingToRemove),
                RegistryValueTarget v => DeleteRegistryValue(v, registry),
                RegistryDwordTarget w => SetDword(w, registry),
                _ => new ResetTargetResult(target, ResetTargetOutcome.Refused, "refused: unknown target type"),
            };
        }
        catch (Exception ex)
        {
            return new ResetTargetResult(target, ResetTargetOutcome.Failed, Explain(ex));
        }
    }

    private static ResetTargetResult DeleteFile(ResetTarget target, string path, IResetFileSystem fs, ResetExecutorOptions options)
    {
        if (!fs.FileExists(path)) return new ResetTargetResult(target, ResetTargetOutcome.NothingToRemove);
        var error = Retry(() => fs.DeleteFile(path), options);
        return error is null
            ? new ResetTargetResult(target, ResetTargetOutcome.Removed)
            : new ResetTargetResult(target, ResetTargetOutcome.Failed, error);
    }

    private static ResetTargetResult DeleteDirectory(ResetTarget target, string path, IResetFileSystem fs, ResetExecutorOptions options)
    {
        if (!fs.DirectoryExists(path)) return new ResetTargetResult(target, ResetTargetOutcome.NothingToRemove);
        var error = Retry(() => fs.DeleteDirectory(path), options);
        return error is null
            ? new ResetTargetResult(target, ResetTargetOutcome.Removed)
            : new ResetTargetResult(target, ResetTargetOutcome.Failed, error);
    }

    private static ResetTargetResult DeleteMatches(ResetTarget target, bool isDirectory, ResetEnvironment env, IResetFileSystem fs, ResetExecutorOptions options)
    {
        var (directory, pattern) = target switch
        {
            FilesMatchingTarget f => (f.Directory, f.Pattern),
            DirectoriesMatchingTarget d => (d.Directory, d.Pattern),
            _ => (string.Empty, string.Empty),
        };
        var matches = isDirectory ? fs.EnumerateDirectories(directory, pattern) : fs.EnumerateFiles(directory, pattern);
        var removed = 0;
        var problems = new List<string>();
        foreach (var match in matches)
        {
            if (ResetPathGuard.MatchRefusal(match, isDirectory, env) is { } refusal)
            {
                problems.Add($"{Path.GetFileName(match)}: refused: {refusal}");
                continue;
            }
            var error = Retry(() => { if (isDirectory) fs.DeleteDirectory(match); else fs.DeleteFile(match); }, options);
            if (error is null) removed++;
            else problems.Add($"{Path.GetFileName(match)}: {error}");
        }
        if (problems.Count > 0)
            return new ResetTargetResult(target, ResetTargetOutcome.Failed, string.Join("; ", problems) + (removed > 0 ? $" ({removed} others removed)" : string.Empty));
        return new ResetTargetResult(target, removed > 0 ? ResetTargetOutcome.Removed : ResetTargetOutcome.NothingToRemove,
            removed > 0 ? $"{removed} removed" : null);
    }

    private static ResetTargetResult EditJson(JsonPropertiesTarget target, IResetFileSystem fs, ResetExecutorOptions options)
    {
        JsonObject root;
        var created = false;
        if (fs.FileExists(target.Path))
        {
            try
            {
                root = JsonNode.Parse(fs.ReadAllText(target.Path)) as JsonObject
                       ?? throw new InvalidDataException("not a JSON object");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
            {
                return new ResetTargetResult(target, ResetTargetOutcome.Failed, "the file is not valid JSON (" + ex.Message + "); choose All settings to replace it");
            }
        }
        else if (target.SetBooleans.Count > 0 && target.CreateWithSchemaVersion is { } schema)
        {
            root = new JsonObject { ["SettingsSchemaVersion"] = schema };
            created = true;
        }
        else return new ResetTargetResult(target, ResetTargetOutcome.NothingToRemove);

        var changed = created;
        foreach (var name in target.RemoveProperties)
            if (root.Remove(name)) changed = true;
        foreach (var (name, value) in target.SetBooleans)
        {
            if (root[name] is JsonValue existing && existing.TryGetValue<bool>(out var current) && current == value) continue;
            root[name] = value;
            changed = true;
        }
        if (!changed) return new ResetTargetResult(target, ResetTargetOutcome.NothingToRemove);

        var text = Serialize(root);
        var error = Retry(() => fs.WriteAllTextAtomic(target.Path, text), options);
        return error is null
            ? new ResetTargetResult(target, ResetTargetOutcome.Removed)
            : new ResetTargetResult(target, ResetTargetOutcome.Failed, error);
    }

    private static string Serialize(JsonObject root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            root.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ResetTargetResult DeleteRegistryValue(RegistryValueTarget target, IResetRegistry registry)
    {
        if (!registry.ValueExists(target.SubKey, target.ValueName)) return new ResetTargetResult(target, ResetTargetOutcome.NothingToRemove);
        if (target.OnlyIfEquals is not null || target.OnlyIfContains is not null)
        {
            var current = registry.GetStringValue(target.SubKey, target.ValueName) ?? string.Empty;
            if (target.OnlyIfEquals is not null && !string.Equals(current, target.OnlyIfEquals, StringComparison.OrdinalIgnoreCase))
                return new ResetTargetResult(target, ResetTargetOutcome.NothingToRemove, "left alone: the value belongs to something else");
            if (target.OnlyIfContains is not null && !current.Contains(target.OnlyIfContains, StringComparison.OrdinalIgnoreCase))
                return new ResetTargetResult(target, ResetTargetOutcome.NothingToRemove, "left alone: the value belongs to something else");
        }
        return registry.DeleteValue(target.SubKey, target.ValueName)
            ? new ResetTargetResult(target, ResetTargetOutcome.Removed)
            : new ResetTargetResult(target, ResetTargetOutcome.NothingToRemove);
    }

    private static ResetTargetResult SetDword(RegistryDwordTarget target, IResetRegistry registry)
    {
        registry.SetDword(target.SubKey, target.ValueName, target.Value);
        return new ResetTargetResult(target, ResetTargetOutcome.Removed);
    }

    /// <summary>Runs <paramref name="action"/>, retrying while the file is in use; returns null on success or the reason.</summary>
    private static string? Retry(Action action, ResetExecutorOptions options)
    {
        string? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, options.Attempts); attempt++)
        {
            try { action(); return null; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; } // already gone
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = Explain(ex);
                if (attempt < options.Attempts) options.Sleep(options.RetryDelay);
            }
            catch (Exception ex) { return Explain(ex); }
        }
        return last;
    }

    private static string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access was denied (the file may be read-only or in use)",
        IOException io when IsSharingViolation(io) => "the file is in use by another program or another Glide window",
        _ => ex.Message,
    };

    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
    }
}
