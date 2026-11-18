using Avalonia;

namespace Glide.App.Platform;

/// <summary>
/// The one in-process list of Glide windows (Stage B: merged with the list <see cref="Glide.App.Services.ExternalLaunchBroker"/>
/// used to keep). Weak references, so closing a window never creates a lifetime leak. Screen hit-testing is physical-pixel
/// based to stay correct across mixed-DPI monitors.
/// <para>
/// Each entry carries two independent memberships, kept exactly as the two old lists did:
/// <b>open window</b> (<see cref="Register"/>/<see cref="Unregister"/>: tab transfer and docking, exit, updates, topmost) and
/// <b>external-open receiver</b> (<see cref="SetLaunchReceiver"/>: the broker's single-instance pipe hands paths to these;
/// a window parked for Speed Boost standby is an open window but not a receiver). Receivers keep the order in which they
/// became receivers, which the broker uses to pick the most recent one.
/// </para>
/// </summary>
internal static class GlideWindowRegistry
{
    private sealed class Entry(MainWindow window)
    {
        public WeakReference<MainWindow> Window { get; } = new(window);
        public bool IsOpenWindow { get; set; }
        public long LaunchSequence { get; set; }   // 0 = not an external-open receiver
    }

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static long _nextLaunchSequence;

    public static void Register(MainWindow window)
    {
        lock (Gate)
        {
            Cleanup();
            GetOrAdd(window).IsOpenWindow = true;
        }
    }

    public static void Unregister(MainWindow window)
    {
        MainWindow[] live;
        lock (Gate)
        {
            if (Find(window) is { } entry)
            {
                entry.IsOpenWindow = false;
                RemoveIfUnused(entry);
            }
            Cleanup();
            live = OpenWindowsLocked();
        }

        // A target can close while another window is still in the native move loop. Notify the
        // surviving source immediately so its accent preview cannot remain stranded until the
        // next pointer sample.
        foreach (var source in live)
            source.OnAttachTargetClosed(window);
    }

    /// <summary>Open windows (see <see cref="Register"/>), in registration order.</summary>
    public static IReadOnlyList<MainWindow> Snapshot()
    {
        lock (Gate)
        {
            Cleanup();
            return OpenWindowsLocked();
        }
    }

    /// <summary>Adds (<paramref name="receives"/> true) or removes the window as a receiver of externally opened paths.</summary>
    public static void SetLaunchReceiver(MainWindow window, bool receives)
    {
        lock (Gate)
        {
            Cleanup();
            if (receives)
            {
                var entry = GetOrAdd(window);
                if (entry.LaunchSequence == 0) entry.LaunchSequence = ++_nextLaunchSequence;
            }
            else if (Find(window) is { } entry)
            {
                entry.LaunchSequence = 0;
                RemoveIfUnused(entry);
            }
        }
    }

    /// <summary>Removes every window as an external-open receiver (the broker stopped); open-window membership is kept.</summary>
    public static void ClearLaunchReceivers()
    {
        lock (Gate)
        {
            foreach (var entry in Entries) entry.LaunchSequence = 0;
            Entries.RemoveAll(e => !e.IsOpenWindow);
        }
    }

    /// <summary>External-open receivers, oldest first (the last one is the most recent).</summary>
    public static IReadOnlyList<MainWindow> LaunchReceivers()
    {
        lock (Gate)
        {
            Cleanup();
            return Entries.Where(e => e.LaunchSequence != 0).OrderBy(e => e.LaunchSequence)
                .Select(e => e.Window.TryGetTarget(out var w) ? w : null).Where(w => w is not null).Cast<MainWindow>().ToArray();
        }
    }

    /// <summary>
    /// Retained for call-site compatibility. Glide no longer elects a single taskbar representative:
    /// every visible window owns its own taskbar button (and a distinct AppUserModelID for secondary
    /// windows), so multiple Glide windows appear as separate taskbar entries. Hidden windows manage
    /// their own taskbar eligibility during the Speed Boost standby transition.
    /// </summary>
    public static void RefreshTaskbarRepresentative()
    {
        lock (Gate) Cleanup();
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

    private static MainWindow[] OpenWindowsLocked() =>
        Entries.Where(e => e.IsOpenWindow).Select(e => e.Window.TryGetTarget(out var w) ? w : null)
            .Where(w => w is not null).Cast<MainWindow>().ToArray();

    private static Entry? Find(MainWindow window) =>
        Entries.FirstOrDefault(e => e.Window.TryGetTarget(out var existing) && ReferenceEquals(existing, window));

    private static Entry GetOrAdd(MainWindow window)
    {
        if (Find(window) is { } entry) return entry;
        entry = new Entry(window);
        Entries.Add(entry);
        return entry;
    }

    private static void RemoveIfUnused(Entry entry)
    {
        if (!entry.IsOpenWindow && entry.LaunchSequence == 0) Entries.Remove(entry);
    }

    private static void Cleanup() => Entries.RemoveAll(e => !e.Window.TryGetTarget(out _));
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
