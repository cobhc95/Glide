using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;

namespace Glide.App.Services;

/// <summary>Safe, cancellable file mutations; UI supplies confirmation and refresh callbacks.</summary>
public sealed class FileOperationController
{
    public bool TryOpenContainingFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
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

    public string? ValidateRename(string source, string requestedName)
    {
        if (!File.Exists(source) || string.IsNullOrWhiteSpace(requestedName)) return null;
        var name = Path.GetFileName(requestedName.Trim());
        if (!string.Equals(name, requestedName.Trim(), StringComparison.Ordinal) || name is "." or ".." ||
            name.IndexOfAny(new[] { '/', '\\' }) >= 0) return null;
        var target = Path.Combine(Path.GetDirectoryName(source)!, name);
        return string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || File.Exists(target) ? null : target;
    }

    public Task<string?> RenameAsync(string source, string requestedName, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var target = ValidateRename(source, requestedName);
        if (target is null) return Task.FromResult<string?>(null);
        File.Move(source, target);
        return Task.FromResult<string?>(target);
    }

    public Task<bool> RecycleAsync(string source, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(source)) return Task.FromResult(false);
        FileSystem.DeleteFile(source, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        return Task.FromResult(true);
    }
}
