namespace Glide.App.Explorer;

/// <summary>Progress of a batch operation. <c>Done</c> counts finished top-level items.</summary>
internal sealed record ExplorerProgress(int Done, int Total, string CurrentName, string Verb);

/// <summary>Failure whose message is already suitable for showing to the user.</summary>
internal sealed class ExplorerOperationException : IOException
{
    public ExplorerOperationException(string message) : base(message) { }
}

internal sealed record ExplorerOperationError(string Path, string Message);

/// <summary><c>Created</c> holds the final destination full paths of the top-level items.</summary>
internal sealed record ExplorerTransferResult(IReadOnlyList<string> Created, IReadOnlyList<ExplorerOperationError> Errors, bool Cancelled);

internal sealed record ExplorerDeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<ExplorerOperationError> Errors, bool Cancelled);

/// <summary>
/// UI-free file operations behind the built-in Explorer: validated rename, new folder, copy/move
/// with Explorer-style conflict naming, and delete (Recycle Bin or permanent). Nothing here ever
/// overwrites or silently removes existing user data; per-item failures are collected and the
/// batch continues.
/// </summary>
internal static class ExplorerFileOperations
{
    private const string InvalidCharsMessage = "A file name can't contain any of the following characters: \\ / : * ? \" < > |";

    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static Func<string, bool, bool> _recycler = DefaultRecycler;

    /// <summary>Test seam: forces the copy-then-delete-source path for moves (as used across volumes).</summary>
    internal static bool ForceCrossVolumeMoves { get; set; }

    /// <summary>
    /// Seam for sending an item to the Recycle Bin: (path, isDirectory) => true when moved.
    /// Tests replace it so they never touch the real Recycle Bin.
    /// </summary>
    public static Func<string, bool, bool> Recycler
    {
        get => _recycler;
        set => _recycler = value ?? DefaultRecycler;
    }

    private static bool DefaultRecycler(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (isDirectory)
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        return true;
    }

    // ---------------------------------------------------------------- naming

    /// <summary>Returns null when <paramref name="name"/> is acceptable, otherwise a short user-facing message.</summary>
    public static string? ValidateName(string name, string parentFolder, string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A file name can't be empty.";
        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.Any(char.IsControl)) return InvalidCharsMessage;
        if (name is "." or "..") return "A file name can't be \".\" or \"..\".";
        if (name[^1] is '.' or ' ') return "A file name can't end with a period or a space.";
        if (name.Length > 255) return "The file name is too long (255 characters maximum).";

        var dot = name.IndexOf('.');
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
        if (ReservedNames.Contains(stem)) return $"\"{name}\" is a reserved name and can't be used.";

        string candidate;
        try { candidate = Path.Combine(parentFolder, name); }
        catch (ArgumentException) { return InvalidCharsMessage; }

        if (Exists(candidate) && !(currentPath is not null && SamePath(candidate, currentPath)))
            return $"There is already a file or folder named \"{name}\" in this location. Choose a different name.";
        return null;
    }

    /// <summary>Renames a file or folder inside its own parent. Returns the new full path.</summary>
    public static string Rename(string path, string newName)
    {
        var full = Normalize(path);
        var isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full))
            throw new ExplorerOperationException($"'{SafeName(full)}' no longer exists.");
        var parent = Path.GetDirectoryName(full) ?? throw new ExplorerOperationException("A drive can't be renamed here.");

        var error = ValidateName(newName, parent, full);
        if (error is not null) throw new ExplorerOperationException(error);

        var currentName = Path.GetFileName(full);
        if (string.Equals(currentName, newName, StringComparison.Ordinal)) return full;
        var target = Path.Combine(parent, newName);

        try
        {
            if (!string.Equals(currentName, newName, StringComparison.OrdinalIgnoreCase))
                MoveEntry(full, target, isDirectory);
            else
            {
                // Case-only rename: a direct move can be a no-op on case-insensitive volumes.
                var temp = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".glide-rename");
                MoveEntry(full, temp, isDirectory);
                try { MoveEntry(temp, target, isDirectory); }
                catch
                {
                    try { MoveEntry(temp, full, isDirectory); } catch { /* best effort restore */ }
                    throw;
                }
            }
        }
        catch (ExplorerOperationException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExplorerOperationException(DescribeError(ex, full));
        }
        return target;
    }

    /// <summary>Creates "New folder" (or "New folder (2)", ...) in <paramref name="parentFolder"/>.</summary>
    public static string CreateFolder(string parentFolder)
    {
        if (!Directory.Exists(parentFolder)) throw new ExplorerOperationException("The folder no longer exists.");
        for (var i = 1; i < 100_000; i++)
        {
            var path = Path.Combine(parentFolder, i == 1 ? "New folder" : $"New folder ({i})");
            if (Exists(path)) continue;
            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ExplorerOperationException(DescribeError(ex, path));
            }
        }
        throw new ExplorerOperationException("Couldn't find a free name for the new folder.");
    }

    /// <summary>The name itself when free in <paramref name="destFolder"/>, otherwise "name - Copy.ext", "name - Copy (2).ext", ...</summary>
    public static string UniqueCopyName(string destFolder, string name, bool isDirectory)
    {
        if (!Exists(Path.Combine(destFolder, name))) return name;
        var stem = name;
        var extension = "";
        if (!isDirectory)
        {
            var withoutExtension = Path.GetFileNameWithoutExtension(name);
            if (withoutExtension.Length > 0) // ".gitignore" has no stem: treat the whole name as the stem
            {
                stem = withoutExtension;
                extension = Path.GetExtension(name);
            }
        }
        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? $"{stem} - Copy{extension}" : $"{stem} - Copy ({i}){extension}";
            if (!Exists(Path.Combine(destFolder, candidate))) return candidate;
        }
    }

    // ---------------------------------------------------------------- copy / move

    public static Task<ExplorerTransferResult> TransferAsync(
        IReadOnlyList<string> sources,
        string destinationFolder,
        bool move,
        IProgress<ExplorerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var snapshot = sources.ToArray();
        return Task.Run(() => Transfer(snapshot, destinationFolder, move, progress, cancellationToken));
    }

    private static ExplorerTransferResult Transfer(string[] sources, string destinationFolder, bool move, IProgress<ExplorerProgress>? progress, CancellationToken ct)
    {
        var verb = move ? "Moving" : "Copying";
        var created = new List<string>();
        var errors = new List<ExplorerOperationError>();

        string destination;
        try { destination = Normalize(destinationFolder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add(new(destinationFolder ?? "", "The destination folder isn't valid."));
            return new(created, errors, false);
        }
        if (!Directory.Exists(destination))
        {
            errors.Add(new(destination, $"The destination folder '{SafeName(destination)}' no longer exists."));
            return new(created, errors, false);
        }

        var seen = new HashSet<string>(PathComparer);
        var cancelled = false;
        for (var i = 0; i < sources.Length; i++)
        {
            progress?.Report(new ExplorerProgress(i, sources.Length, SafeName(sources[i]), verb));
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            try
            {
                TransferOne(sources[i], destination, move, seen, created, errors, ct);
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception ex)
            {
                errors.Add(new(sources[i], DescribeError(ex, sources[i])));
            }
        }
        if (!cancelled) progress?.Report(new ExplorerProgress(sources.Length, sources.Length, "", verb));
        return new(created, errors, cancelled);
    }

    private static void TransferOne(string source, string destination, bool move, HashSet<string> seen, List<string> created, List<ExplorerOperationError> errors, CancellationToken ct)
    {
        var full = Normalize(source);
        var name = SafeName(full);
        var action = move ? "move" : "copy";

        if (!seen.Add(full))
        {
            errors.Add(new(full, $"'{name}' was listed more than once and was skipped."));
            return;
        }
        var isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full))
        {
            errors.Add(new(full, $"'{name}' no longer exists."));
            return;
        }
        var parent = Path.GetDirectoryName(full);
        if (parent is null)
        {
            errors.Add(new(full, $"Can't {action} a drive."));
            return;
        }
        if (isDirectory && IsSameOrDescendant(destination, full))
        {
            errors.Add(new(full, $"Can't {action} the folder '{name}' into itself or one of its subfolders."));
            return;
        }
        if (move && SamePath(parent, destination)) return; // already there: nothing to do

        var target = Path.Combine(destination, UniqueCopyName(destination, name, isDirectory));
        if (!isDirectory)
        {
            if (!move) File.Copy(full, target, overwrite: false);
            else MoveFile(full, target);
            created.Add(target);
            return;
        }

        if (!move)
        {
            Directory.CreateDirectory(target);
            CopyTree(full, target, errors, ct);
            created.Add(target);
            return;
        }

        if (!ForceCrossVolumeMoves && SameRoot(full, target))
        {
            Directory.Move(full, target);
            created.Add(target);
            return;
        }

        // Cross-volume folder move: copy everything, remove the source only after a fully successful copy.
        Directory.CreateDirectory(target);
        var copied = CopyTree(full, target, errors, ct);
        created.Add(target);
        if (!copied)
        {
            errors.Add(new(full, $"'{name}' was copied, but the original was kept because some items couldn't be copied."));
            return;
        }
        try { DeleteTree(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new(full, $"'{name}' was copied, but the original couldn't be removed. {DescribeError(ex, full)}"));
        }
    }

    private static void MoveFile(string source, string target)
    {
        if (!ForceCrossVolumeMoves)
        {
            File.Move(source, target, overwrite: false);
            return;
        }
        File.Copy(source, target, overwrite: false);
        DeleteFilePermanent(source);
    }

    /// <summary>Copies the contents of <paramref name="source"/> into the existing <paramref name="target"/>. Returns false when any item failed (recorded in <paramref name="errors"/>).</summary>
    private static bool CopyTree(string source, string target, List<ExplorerOperationError> errors, CancellationToken ct)
    {
        List<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(source).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new(source, DescribeError(ex, source)));
            return false;
        }

        var ok = true;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            var destination = Path.Combine(target, name);
            try
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        errors.Add(new(entry, $"'{name}' is a link and was skipped."));
                        ok = false;
                        continue;
                    }
                    Directory.CreateDirectory(destination);
                    if (!CopyTree(entry, destination, errors, ct)) ok = false;
                }
                else
                {
                    File.Copy(entry, destination, overwrite: false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add(new(entry, DescribeError(ex, entry)));
                ok = false;
            }
        }
        return ok;
    }

    // ---------------------------------------------------------------- delete

    public static Task<ExplorerDeleteResult> DeleteAsync(
        IReadOnlyList<string> paths,
        bool permanent,
        IProgress<ExplorerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshot = paths.ToArray();
        return Task.Run(() => Delete(snapshot, permanent, progress, cancellationToken));
    }

    private static ExplorerDeleteResult Delete(string[] paths, bool permanent, IProgress<ExplorerProgress>? progress, CancellationToken ct)
    {
        var deleted = new List<string>();
        var errors = new List<ExplorerOperationError>();

        // Normalise, drop duplicates and items nested inside another listed item (they go with their parent).
        var normalized = new List<string>();
        var all = new HashSet<string>(PathComparer);
        foreach (var path in paths)
        {
            try
            {
                var full = Normalize(path);
                if (all.Add(full)) normalized.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add(new(path ?? "", DescribeError(ex, path ?? "")));
            }
        }
        var work = normalized.Where(p => !HasListedAncestor(p, all)).ToList();

        var cancelled = false;
        for (var i = 0; i < work.Count; i++)
        {
            var full = work[i];
            var name = SafeName(full);
            progress?.Report(new ExplorerProgress(i, work.Count, name, "Deleting"));
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            try
            {
                var isDirectory = Directory.Exists(full);
                if (!isDirectory && !File.Exists(full))
                {
                    errors.Add(new(full, $"'{name}' no longer exists."));
                    continue;
                }
                if (permanent)
                {
                    if (isDirectory) DeleteTree(full); else DeleteFilePermanent(full);
                    deleted.Add(full);
                }
                else if (Recycler(full, isDirectory))
                {
                    deleted.Add(full);
                }
                else
                {
                    errors.Add(new(full, $"'{name}' couldn't be moved to the Recycle Bin."));
                }
            }
            catch (Exception ex)
            {
                errors.Add(new(full, DescribeError(ex, full)));
            }
        }
        if (!cancelled) progress?.Report(new ExplorerProgress(work.Count, work.Count, "", "Deleting"));
        return new(deleted, errors, cancelled);
    }

    private static bool HasListedAncestor(string path, HashSet<string> listed)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (listed.Contains(Path.TrimEndingDirectorySeparator(parent)) || listed.Contains(parent)) return true;
        }
        return false;
    }

    private static void DeleteFilePermanent(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    private static void DeleteTree(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (UnauthorizedAccessException)
        {
            // Read-only files block recursive deletes: clear the flag and retry once.
            ClearReadOnly(path);
            Directory.Delete(path, recursive: true);
        }
    }

    private static void ClearReadOnly(string directory)
    {
        // The item being deleted may itself be read-only, and it may be a junction or directory symlink.
        // Clear the flag on the item, but never walk through a link: enumerating it would strip the
        // read-only flag from files in the link's target, which is not being deleted (only the link is).
        var rootAttributes = File.GetAttributes(directory);
        if ((rootAttributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(directory, rootAttributes & ~FileAttributes.ReadOnly);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0) return;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", options))
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- errors

    /// <summary>User-facing description of a failed operation on <paramref name="path"/>, including the item name.</summary>
    public static string DescribeError(Exception ex, string path)
    {
        if (ex is ExplorerOperationException) return ex.Message;
        var name = SafeName(path);
        string reason = ex switch
        {
            UnauthorizedAccessException => "Access is denied",
            PathTooLongException => "The path is too long",
            DirectoryNotFoundException => "The folder or path can't be found",
            FileNotFoundException => "The file can't be found",
            DriveNotFoundException => "The drive can't be found",
            IOException io => DescribeIo(io),
            _ => ex.Message,
        };
        reason = reason.TrimEnd('.', ' ');
        if (name.Length == 0 || reason.Contains(name, StringComparison.OrdinalIgnoreCase)) return reason + ".";
        return $"'{name}': {reason}.";
    }

    private static string DescribeIo(IOException io) => (uint)io.HResult switch
    {
        0x80070020 or 0x80070021 => "The file is in use by another program",
        0x80070070 or 0x80070027 => "There isn't enough space on the disk",
        0x800700B7 or 0x80070050 => "An item with that name already exists",
        0x80070005 => "Access is denied",
        0x800700CE => "The path is too long",
        _ => io.Message,
    };

    // ---------------------------------------------------------------- helpers

    private static void MoveEntry(string source, string target, bool isDirectory)
    {
        if (isDirectory) Directory.Move(source, target);
        else File.Move(source, target, overwrite: false);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Normalize(a), Normalize(b), PathComparison); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool SameRoot(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), PathComparison);

    private static bool IsSameOrDescendant(string candidate, string ancestor)
    {
        if (string.Equals(candidate, ancestor, PathComparison)) return true;
        var prefix = Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static string SafeName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;
    }
}
