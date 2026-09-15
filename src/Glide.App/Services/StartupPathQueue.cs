namespace Glide.App.Services;

/// <summary>Normalizes, validates and de-duplicates startup paths before the window opens.</summary>
public sealed class StartupPathQueue
{
    private readonly List<string> _paths = new();
    public IReadOnlyList<string> Paths => _paths;

    public void Add(IEnumerable<string> paths, Func<string, bool> isAccepted, Action<string, string>? rejected = null)
    {
        foreach (var raw in paths.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            string path;
            try { path = Path.GetFullPath(raw); }
            catch (Exception ex) { rejected?.Invoke(raw, "invalid path: " + ex.GetType().Name); continue; }
            if (isAccepted(path))
            {
                if (!_paths.Contains(path, StringComparer.OrdinalIgnoreCase)) _paths.Add(path);
            }
            else rejected?.Invoke(path, "unsupported or missing");
        }
    }

    public string[] Drain()
    {
        var result = _paths.ToArray();
        _paths.Clear();
        return result;
    }
}
