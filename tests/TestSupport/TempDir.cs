namespace Glide.Tests.Support;

/// <summary>
/// Shared temp-folder helper for tests (linked into each test project). <c>using var dir = new TempDir();</c> creates a
/// unique folder under %TEMP% and deletes it on dispose; <see cref="TryDelete"/> is the best-effort recursive delete
/// that used to be copied as <c>try { Directory.Delete(path, true); } catch { }</c>.
/// </summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string prefix = "glide-test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose() => TryDelete(Path);

    public override string ToString() => Path;

    public static implicit operator string(TempDir dir) => dir.Path;

    /// <summary>Best-effort recursive delete with a short retry for files a test just released. Never throws.</summary>
    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (Exception) { return; }
            Thread.Sleep(50);
        }
    }
}
