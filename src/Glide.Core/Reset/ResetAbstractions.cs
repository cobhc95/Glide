namespace Glide.Core.Reset;

/// <summary>The file operations the reset needs. Tests use scratch folders through <see cref="PhysicalResetFileSystem"/> or a failing wrapper.</summary>
public interface IResetFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void DeleteFile(string path);
    /// <summary>Deletes the folder and everything in it.</summary>
    void DeleteDirectory(string path);
    IReadOnlyList<string> EnumerateFiles(string directory, string pattern);
    IReadOnlyList<string> EnumerateDirectories(string directory, string pattern);
    string ReadAllText(string path);
    /// <summary>Writes through a temporary file and a move so a crash never leaves half a file.</summary>
    void WriteAllTextAtomic(string path, string text);
    void CopyFile(string source, string destination);
    void CreateDirectory(string path);
    long FileLength(string path);
}

/// <summary>
/// HKEY_CURRENT_USER only: the interface has no way to name another hive, so a reset can never touch HKLM.
/// Paths are relative to HKCU and use backslashes.
/// </summary>
public interface IResetRegistry
{
    bool KeyExists(string subKey);
    /// <summary>Deletes the key and everything below it; false when it did not exist.</summary>
    bool DeleteKeyTree(string subKey);
    string? GetStringValue(string subKey, string valueName);
    bool ValueExists(string subKey, string valueName);
    /// <summary>False when the value did not exist.</summary>
    bool DeleteValue(string subKey, string valueName);
    void SetDword(string subKey, string valueName, int value);
}

/// <summary>Process access for the close-wipe-restart handover.</summary>
public interface IResetProcessHost
{
    /// <summary>Waits until the process has exited (or does not exist); false when it is still running after <paramref name="timeout"/>.</summary>
    bool WaitForExit(int processId, TimeSpan timeout);
    /// <summary>Starts a detached process; <paramref name="error"/> says why not when false is returned.</summary>
    bool Start(string executable, IReadOnlyList<string> arguments, out string? error);
}

/// <summary>The real file system.</summary>
public sealed class PhysicalResetFileSystem : IResetFileSystem
{
    public static PhysicalResetFileSystem Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void DeleteFile(string path)
    {
        // A read-only flag must not keep a settings file alive.
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    public IReadOnlyList<string> EnumerateFiles(string directory, string pattern) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToArray() : [];

    public IReadOnlyList<string> EnumerateDirectories(string directory, string pattern) =>
        Directory.Exists(directory) ? Directory.EnumerateDirectories(directory, pattern, SearchOption.TopDirectoryOnly).ToArray() : [];

    public string ReadAllText(string path)
    {
        // FileShare.ReadWrite|Delete: another Glide process may be saving at the same moment.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void WriteAllTextAtomic(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    public void CopyFile(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public long FileLength(string path) => new FileInfo(path).Length;
}
