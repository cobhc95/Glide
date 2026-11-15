using System.Diagnostics;
using Glide.App.Explorer;

namespace Glide.App.Services;

/// <summary>Outcome of a viewer rename: the new path, or a user-facing reason why nothing was renamed.</summary>
public sealed record FileRenameOutcome(string? NewPath, string? Error)
{
    public bool Renamed => NewPath is not null;
}

/// <summary>
/// Safe, cancellable file mutations for the image viewer; UI supplies confirmation and refresh callbacks.
/// Rename uses the same implementation as the Explorer tab (<see cref="ExplorerFileOperations.Rename"/>), so
/// both accept the same names, both reject the same clashes and both support case-only renames. Every file
/// system call runs on a worker thread, so a slow disk or network share never freezes the viewer.
/// </summary>
public sealed class FileOperationController
{
    /// <summary>Test seam: the rename step (path, new name) => new path. Defaults to the shared Explorer rename.</summary>
    internal Func<string, string, string> Renamer { get; set; } = ExplorerFileOperations.Rename;

    /// <summary>Test seam: sends a file to the Recycle Bin. Defaults to the shared Explorer recycler.</summary>
    internal Func<string, bool> Recycler { get; set; } = path => ExplorerFileOperations.Recycler(path, false);

    public bool TryOpenContainingFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        // Reject quote/CRLF so a crafted file name cannot break out of explorer quoting.
        if (path.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) return false;
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.Arguments = "/select,\"" + path + "\"";
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    public bool TryLaunchExternal(string? program, string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(program) || string.IsNullOrWhiteSpace(imagePath) ||
            !File.Exists(program) || !File.Exists(imagePath)) return false;
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = true };
            psi.ArgumentList.Add(imagePath);
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns null when <paramref name="requestedName"/> is acceptable for renaming the file
    /// <paramref name="source"/>, otherwise a short user-facing reason. An unchanged name is "nothing to do".
    /// Touches the file system: call it off the UI thread (<see cref="TryRenameAsync"/> does).
    /// </summary>
    public static string? RenameProblem(string source, string? requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName)) return "A file name can't be empty.";
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return $"'{Path.GetFileName(source)}' no longer exists.";
        var name = requestedName.Trim();
        var full = Path.GetFullPath(source);
        if (string.Equals(Path.GetFileName(full), name, StringComparison.Ordinal)) return "The name is unchanged.";
        var parent = Path.GetDirectoryName(full);
        if (parent is null) return "A drive can't be renamed here.";
        return ExplorerFileOperations.ValidateName(name, parent, full);
    }

    /// <summary>The new full path when <paramref name="requestedName"/> is a valid rename of <paramref name="source"/>; otherwise null.</summary>
    public string? ValidateRename(string source, string requestedName)
        => RenameProblem(source, requestedName) is null
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, requestedName.Trim())
            : null;

    /// <summary>Renames on a worker thread. Validation problems and file system failures come back as <see cref="FileRenameOutcome.Error"/>.</summary>
    public Task<FileRenameOutcome> TryRenameAsync(string source, string requestedName, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<FileRenameOutcome>(token);
        var renamer = Renamer;
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var problem = RenameProblem(source, requestedName);
            if (problem is not null) return new FileRenameOutcome(null, problem);
            try
            {
                return new FileRenameOutcome(renamer(source, requestedName.Trim()), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new FileRenameOutcome(null, ExplorerFileOperations.DescribeError(ex, source));
            }
        }, token);
    }

    /// <summary>Renames on a worker thread. Null when the name is invalid or unchanged; throws when the file system refuses.</summary>
    public async Task<string?> RenameAsync(string source, string requestedName, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var renamer = Renamer;
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return RenameProblem(source, requestedName) is null ? renamer(source, requestedName.Trim()) : null;
        }, token).ConfigureAwait(true);
    }

    /// <summary>Sends <paramref name="source"/> to the Recycle Bin on a worker thread. False when the file is gone or the bin refused it.</summary>
    public async Task<bool> RecycleAsync(string source, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var recycler = Recycler;
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return File.Exists(source) && recycler(source);
        }, token).ConfigureAwait(true);
    }
}
