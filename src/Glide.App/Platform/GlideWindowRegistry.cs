using Avalonia;
using Avalonia.Controls;
using Glide.Core.Workspace;

namespace Glide.App.Platform;

/// <summary>
/// In-process registry used only for tab transfer/docking. It contains weak references so closing a
/// window never creates a lifetime leak. Screen hit-testing is physical-pixel based to stay correct
/// across mixed-DPI monitors.
/// </summary>
internal static class GlideWindowRegistry
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<MainWindow>> Windows = new();

    public static void Register(MainWindow window)
    {
        lock (Gate)
        {
            Cleanup();
            if (Windows.Any(x => x.TryGetTarget(out var existing) && ReferenceEquals(existing, window))) return;
            Windows.Add(new WeakReference<MainWindow>(window));
            RefreshTaskbarRepresentativeLocked();
        }
    }

    public static void Unregister(MainWindow window)
    {
        MainWindow[] live;
        lock (Gate)
        {
            Windows.RemoveAll(x => !x.TryGetTarget(out var target) || ReferenceEquals(target, window));
            RefreshTaskbarRepresentativeLocked();
            live = Windows.Select(x => x.TryGetTarget(out var target) ? target : null)
                .Where(target => target is not null).Cast<MainWindow>().ToArray();
        }

        // A target can close while another window is still in the native move loop. Notify the
        // surviving source immediately so its accent preview cannot remain stranded until the
        // next pointer sample.
        foreach (var source in live)
            source.OnAttachTargetClosed(window);
    }

    public static IReadOnlyList<MainWindow> Snapshot()
    {
        lock (Gate)
        {
            Cleanup();
            return Windows.Select(x => x.TryGetTarget(out var w) ? w : null).Where(x => x is not null).Cast<MainWindow>().ToArray();
        }
    }

    /// <summary>
    /// Keeps one visible Glide window as the taskbar representative. Secondary windows remain
    /// ordinary top-level windows for independent navigation, but do not create extra taskbar
    /// buttons. When the representative closes or enters Speed Boost standby, another visible
    /// Glide window is promoted automatically.
    /// </summary>
    public static void RefreshTaskbarRepresentative()
    {
        lock (Gate)
        {
            Cleanup();
            RefreshTaskbarRepresentativeLocked();
        }
    }

    public static MainWindow? FindAttachTarget(PixelPoint screenPoint, MainWindow source)
    {
        foreach (var window in Snapshot())
        {
            if (ReferenceEquals(window, source) || !window.IsVisible || !window.CanAcceptTabAttach) continue;
            // The destination zone deliberately extends below the chrome. This makes the
            // interaction forgiving when the source window overlaps the destination, while the
            // small top inset keeps the native title bar out of the merge target.
            if (window.GetTabAttachZoneScreenRect().Contains(screenPoint)) return window;
        }
        return null;
    }

    private static void Cleanup() => Windows.RemoveAll(x => !x.TryGetTarget(out _));

    private static void RefreshTaskbarRepresentativeLocked()
    {
        var live = Windows
            .Select(x => x.TryGetTarget(out var window) ? window : null)
            .Where(window => window is not null)
            .Cast<MainWindow>()
            .ToArray();
        // Prefer the primary render window whenever it is visible. A secondary window can be the
        // temporary fallback while Speed Boost has the primary hidden, but it must not reclaim the
        // taskbar slot when the primary is restored after the last-window standby cycle.
        var representative = live.FirstOrDefault(window => window.IsVisible && !window.IsSecondaryWindow)
            ?? live.FirstOrDefault(window => window.IsVisible);
        foreach (var window in live)
        {
            var shouldShow = representative is not null && ReferenceEquals(window, representative);
            if (window.ShowInTaskbar != shouldShow)
                window.ShowInTaskbar = shouldShow;
        }
    }
}

internal readonly record struct ScreenPixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(PixelPoint point) =>
        point.X >= X && point.Y >= Y && point.X < Right && point.Y < Bottom;

    public ScreenPixelRect Inflate(int horizontal, int top, int bottom) =>
        new(X - horizontal, Y - top, Width + (horizontal * 2), Height + top + bottom);
}
