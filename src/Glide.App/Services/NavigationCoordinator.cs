using Glide.Core;

namespace Glide.App.Services;

/// <summary>UI-facing navigation boundary; keeps folder indexing and tab orchestration out of the window.</summary>
public sealed class NavigationCoordinator
{
    private readonly ImageNavigator _inner = new();
    public IReadOnlyList<string> Paths => _inner.Paths;
    public int Index => _inner.Index;
    public int Count => _inner.Count;
    public string? Current => _inner.Current;
    public string? First() => _inner.First();
    public string? Last() => _inner.Last();
    public string? Move(int delta) => _inner.Move(delta);
    public void OpenSingleOnly(string path) => _inner.OpenSingleOnly(path);
    public void ReplaceFolderIndex(IEnumerable<string> paths, string current) => _inner.ReplaceFolderIndex(paths, current);
    public string? SelectIndex(int index) => _inner.SelectIndex(index);
}
