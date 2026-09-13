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
        }
    }

    public static void Unregister(MainWindow window)
    {
        MainWindow[] live;
        lock (Gate)
        {
            Windows.RemoveAll(x => !x.TryGetTarget(out var target) || ReferenceEquals(target, window));
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
