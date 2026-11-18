using Glide.Core;

namespace Glide.App.Services;

/// <summary>How MainWindow handles one previous / next request (Stage D step 6).</summary>
public enum NavigationRoute
{
    /// <summary>Nothing to do: empty folder, no movement, or a boundary prompt is open.</summary>
    Ignore,
    /// <summary>The folder index is not ready (or not canonical at a boundary): add the step to the pending buffer.</summary>
    Buffer,
    /// <summary>At the first / last image with an end-of-folder step other than Stay (loop, sibling folder, picker...).</summary>
    EndOfFolder,
    /// <summary>Move inside the folder (re-checked under the navigation gate; a boundary there shows the notice).</summary>
    Move,
}

/// <summary>
/// UI-facing navigation boundary: the folder index plus the routing decisions of a previous / next request that used
/// to be inlined in <c>MainWindow.NavigateCoreAsync</c> (boundary test, pending-step buffering, rapid-browse timing).
/// MainWindow keeps the serialization gate, presentation and the end-of-folder UI.
/// </summary>
public sealed class NavigationCoordinator
{
    /// <summary>Requests closer together than this are rapid browsing (preview-quality presentation).</summary>
    public const double RapidIntervalMs = 150;
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
    public bool ApplyChanges(IEnumerable<string> added, IEnumerable<string> removed) => _inner.ApplyChanges(added, removed);
    public void Resort() => _inner.Resort();

    /// <summary>True when a step of <paramref name="delta"/> would leave the folder.</summary>
    public bool AtBoundary(int delta) => delta > 0 ? Index >= Count - 1 : Index <= 0;

    /// <summary>The route for a request, before any gate wait (MainWindow re-checks the boundary under the gate).</summary>
    public NavigationRoute Route(int delta, bool folderIndexReady, bool folderIndexCanonicalReady, bool boundaryPromptActive,
        Func<bool> endStepIsStay)
    {
        if (Count == 0 || delta == 0 || boundaryPromptActive) return NavigationRoute.Ignore;
        if (!folderIndexReady) return NavigationRoute.Buffer;
        var atBoundary = AtBoundary(delta);
        if (atBoundary && !folderIndexCanonicalReady) return NavigationRoute.Buffer;
        return atBoundary && !endStepIsStay() ? NavigationRoute.EndOfFolder : NavigationRoute.Move;
    }

    /// <summary>Adds a step to the pending buffer, capped at +/- <paramref name="max"/>.</summary>
    public static int BufferDelta(int pending, int delta, int max) => Math.Clamp(pending + delta, -max, max);

    /// <summary>Whether a request <paramref name="elapsedMs"/> after the previous one is rapid browsing (none before = not rapid).</summary>
    public static bool IsRapid(bool hadPrevious, double elapsedMs) => hadPrevious && elapsedMs <= RapidIntervalMs;
}
