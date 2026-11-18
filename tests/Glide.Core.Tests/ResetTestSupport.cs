using Glide.Core.Reset;

namespace Glide.Core.Tests;

/// <summary>
/// Scratch machine for the Reset Glide tests: a settings folder, a local-app-data folder, a temp folder and an
/// install folder, all inside one scratch directory. Nothing here ever touches the real user data or registry.
/// </summary>
internal sealed class ResetScratch : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "glide-reset-tests-" + Guid.NewGuid().ToString("N"));
    public string Settings => Path.Combine(Root, "settings");
    public string Local => Path.Combine(Root, "localappdata", "Glide");
    public string Temp => Path.Combine(Root, "temp");
    public string Install => Path.Combine(Root, "install");
    public string Backups => Path.Combine(Root, "documents", "Glide backups");
    public string[] Extensions { get; } = [".jpg", ".png", ".webp"];

    public ResetScratch()
    {
        foreach (var dir in new[] { Settings, Local, Temp, Install }) Directory.CreateDirectory(dir);
    }

    public ResetEnvironment Environment => new(Settings, Local, Temp, Install, Extensions, SettingsSchemaVersion: 7);

    public string Write(string directory, string name, string content = "x")
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public ResetServices Services(FakeRegistry? registry = null, FakeProcessHost? processes = null, IResetFileSystem? fs = null, string? exe = "C:\\scratch\\Glide.exe") => new()
    {
        Environment = Environment,
        FileSystem = fs ?? PhysicalResetFileSystem.Instance,
        Registry = registry ?? new FakeRegistry(),
        Processes = processes ?? new FakeProcessHost(),
        BackupRoot = Backups,
        ExecutablePath = exe,
        ProcessId = 4242,
        AppVersion = "5.0.0-test",
        Now = () => new DateTime(2026, 11, 14, 16, 30, 5),
        ExecutorOptions = new ResetExecutorOptions { Attempts = 3, RetryDelay = TimeSpan.Zero, Sleep = _ => { } },
    };

    public void Dispose()
    {
        TempDir.TryDelete(Root);
    }
}

/// <summary>HKCU in memory: key path (lower-case) -> value name -> value.</summary>
internal sealed class FakeRegistry : IResetRegistry
{
    private readonly Dictionary<string, Dictionary<string, object>> _keys = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Writes { get; } = [];

    public void Set(string subKey, string name, object value)
    {
        // Creating a child creates its parents, like the real registry.
        var parts = subKey.Split('\\');
        for (var i = 1; i <= parts.Length; i++)
        {
            var path = string.Join('\\', parts.Take(i));
            if (!_keys.ContainsKey(path)) _keys[path] = new(StringComparer.OrdinalIgnoreCase);
        }
        _keys[subKey][name] = value;
    }

    public object? Get(string subKey, string name) => _keys.TryGetValue(subKey, out var v) && v.TryGetValue(name, out var o) ? o : null;
    public IEnumerable<string> Keys => _keys.Keys;

    public bool KeyExists(string subKey) => _keys.ContainsKey(subKey);

    public bool DeleteKeyTree(string subKey)
    {
        if (!_keys.ContainsKey(subKey)) return false;
        foreach (var key in _keys.Keys.Where(k => k.Equals(subKey, StringComparison.OrdinalIgnoreCase) || k.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase)).ToArray())
            _keys.Remove(key);
        Writes.Add("deleteTree " + subKey);
        return true;
    }

    public string? GetStringValue(string subKey, string valueName) => Get(subKey, valueName) as string;
    public bool ValueExists(string subKey, string valueName) => Get(subKey, valueName) is not null;

    public bool DeleteValue(string subKey, string valueName)
    {
        if (!_keys.TryGetValue(subKey, out var values) || !values.Remove(valueName)) return false;
        Writes.Add("deleteValue " + subKey + " " + valueName);
        return true;
    }

    public void SetDword(string subKey, string valueName, int value)
    {
        Set(subKey, valueName, value);
        Writes.Add($"setDword {subKey} {valueName}={value}");
    }
}

internal sealed class FakeProcessHost : IResetProcessHost
{
    public bool GlideExits { get; set; } = true;
    public bool StartSucceeds { get; set; } = true;
    public List<(string Exe, string[] Args)> Started { get; } = [];
    public List<int> Waited { get; } = [];

    public bool WaitForExit(int processId, TimeSpan timeout) { Waited.Add(processId); return GlideExits; }

    public bool Start(string executable, IReadOnlyList<string> arguments, out string? error)
    {
        error = StartSucceeds ? null : "scratch refusal";
        if (StartSucceeds) Started.Add((executable, arguments.ToArray()));
        return StartSucceeds;
    }
}

internal sealed class FakeMessages : IResetUserMessage
{
    public List<(string Title, string Message)> Shown { get; } = [];
    public void Show(string title, string message) => Shown.Add((title, message));
}

/// <summary>Real files, but chosen paths fail like a file in use (sharing violation) for the first <see cref="FailTimes"/> attempts.</summary>
internal sealed class FlakyFileSystem(IResetFileSystem inner, Func<string, bool> fails, int failTimes) : IResetFileSystem
{
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    public int FailTimes { get; } = failTimes;
    public int DeleteAttempts { get; private set; }

    public bool FileExists(string path) => inner.FileExists(path);
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public void DeleteFile(string path)
    {
        DeleteAttempts++;
        if (fails(path))
        {
            _attempts[path] = _attempts.GetValueOrDefault(path) + 1;
            if (_attempts[path] <= FailTimes)
                throw new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));
        }
        inner.DeleteFile(path);
    }

    public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
    public IReadOnlyList<string> EnumerateFiles(string directory, string pattern) => inner.EnumerateFiles(directory, pattern);
    public IReadOnlyList<string> EnumerateDirectories(string directory, string pattern) => inner.EnumerateDirectories(directory, pattern);
    public string ReadAllText(string path) => inner.ReadAllText(path);
    public void WriteAllTextAtomic(string path, string text) => inner.WriteAllTextAtomic(path, text);
    public void CopyFile(string source, string destination) => inner.CopyFile(source, destination);
    public void CreateDirectory(string path) => inner.CreateDirectory(path);
    public long FileLength(string path) => inner.FileLength(path);
}
