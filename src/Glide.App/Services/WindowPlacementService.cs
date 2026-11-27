using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Glide.App.Settings;
using Glide.Core.Windows;

namespace Glide.App.Services;

/// <summary>
/// The one place every Glide window (other than the main window) remembers its placement. A window calls
/// <see cref="Track"/> once, before it is shown, with a stable key from <see cref="WindowPlacementCatalog"/>:
/// <list type="bullet">
/// <item>Restore: the saved geometry for that key is validated against the monitors that exist now
/// (<see cref="WindowPlacementResolver"/>: off-screen, gone monitor, oversized or tiny sizes, DPI) and applied before
/// the window appears. Nothing saved, or nothing valid: the window keeps its own default (centred on its owner).</item>
/// <item>Save: when the window closes (OK, Cancel, Escape or the close button) its normal (not maximized) geometry
/// and maximized state are stored under the key and written to glide.settings.json ("WindowPlacements").</item>
/// </list>
/// Resizable windows (<see cref="WindowPlacementMode.SizeAndPosition"/>) remember size, position and maximized state.
/// Fixed-size and content-sized dialogs (<see cref="WindowPlacementMode.PositionOnly"/>) remember nothing: they always
/// open centred on their owner (<see cref="DialogPlacement"/>), and any position an older build saved for them is dropped.
/// The live store is authoritative at runtime: the main window copies it into its settings on every full save
/// (<see cref="ApplyTo"/>), so an older in-memory copy can never write back a stale placement.
/// </summary>
public static class WindowPlacementService
{
    private static readonly object Gate = new();
    private static Dictionary<string, WindowPlacementState>? _store;
    private static bool _legacyPlacementCleaned;
    private static readonly ConditionalWeakTable<Window, Tracker> Tracked = new();

    /// <summary>Tests: replaces the disk write (glide.settings.json) with a callback. Null = write to disk.</summary>
    internal static Action<IReadOnlyDictionary<string, WindowPlacementState>>? PersistOverrideForTests { get; set; }

    /// <summary>Tests: the monitors to validate against instead of the window's real screens.</summary>
    internal static Func<Window, (IReadOnlyList<ScreenWorkArea> Screens, ScreenWorkArea Primary)>? ScreensOverrideForTests { get; set; }

    /// <summary>Tests: start from <paramref name="seed"/> (or empty) instead of the settings file.</summary>
    internal static void ResetForTests(Dictionary<string, WindowPlacementState>? seed = null)
    {
        lock (Gate) _store = GlideSettingsState.ClonePlacements(seed);
    }

    /// <summary>True when <paramref name="window"/> has been registered with <see cref="Track"/>.</summary>
    public static bool IsTracked(Window window) => Tracked.TryGetValue(window, out _);

    /// <summary>The key <paramref name="window"/> was tracked with, or null.</summary>
    public static string? KeyOf(Window window) => Tracked.TryGetValue(window, out var t) ? t.Key : null;

    /// <summary>The mode <paramref name="window"/> was tracked with, or null.</summary>
    public static WindowPlacementMode? ModeOf(Window window) => Tracked.TryGetValue(window, out var t) ? t.Mode : null;

    private static Dictionary<string, WindowPlacementState> EnsureLoaded()
    {
        // Caller holds Gate.
        return _store ??= SettingsStore.LoadWindowPlacements();
    }

    /// <summary>The remembered placement for <paramref name="key"/> (a copy), or null.</summary>
    public static WindowPlacementState? Get(string key)
    {
        lock (Gate)
            return EnsureLoaded().TryGetValue(key, out var value) && value is not null ? value with { } : null;
    }

    /// <summary>A copy of the whole live store.</summary>
    public static Dictionary<string, WindowPlacementState> Snapshot()
    {
        lock (Gate) return GlideSettingsState.ClonePlacements(EnsureLoaded());
    }

    /// <summary>Stores <paramref name="value"/> for <paramref name="key"/> and (unless <paramref name="persist"/> is false) writes the store to disk.</summary>
    public static void Remember(string key, WindowPlacementState value, bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(key) || value is null) return;
        Dictionary<string, WindowPlacementState> snapshot;
        lock (Gate)
        {
            var store = EnsureLoaded();
            store[key] = value with { LastUsedUtcTicks = DateTime.UtcNow.Ticks };
            if (store.Count > SettingsStore.MaxWindowPlacements)
                foreach (var stale in store.Where(kv => !string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(kv => kv.Value.LastUsedUtcTicks).Take(store.Count - SettingsStore.MaxWindowPlacements).Select(kv => kv.Key).ToArray())
                    store.Remove(stale);
            snapshot = GlideSettingsState.ClonePlacements(store);
        }
        if (!persist) return;
        if (PersistOverrideForTests is { } sink) { sink(snapshot); return; }
        SettingsStore.SaveWindowPlacements(snapshot);
    }

    /// <summary>Drops the remembered placement for <paramref name="key"/> (and writes the store) when there is one.</summary>
    public static void Forget(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        Dictionary<string, WindowPlacementState> snapshot;
        lock (Gate)
        {
            var store = EnsureLoaded();
            if (!store.Remove(key)) return;
            snapshot = GlideSettingsState.ClonePlacements(store);
        }
        if (PersistOverrideForTests is { } sink) { sink(snapshot); return; }
        SettingsStore.SaveWindowPlacements(snapshot);
    }

    /// <summary>
    /// Back up and restore: replaces the remembered placement of every window named in <paramref name="placements"/> (already
    /// validated against this PC's monitors by the importer) and writes the store. Windows not in the backup keep theirs.
    /// </summary>
    public static void Import(IReadOnlyDictionary<string, WindowPlacementState> placements)
    {
        if (placements is null || placements.Count == 0) return;
        Dictionary<string, WindowPlacementState> snapshot;
        lock (Gate)
        {
            var store = EnsureLoaded();
            var now = DateTime.UtcNow.Ticks;
            foreach (var (key, value) in placements)
                if (!string.IsNullOrWhiteSpace(key) && value is not null) store[key] = value with { LastUsedUtcTicks = now };
            if (store.Count > SettingsStore.MaxWindowPlacements)
                foreach (var stale in store.OrderBy(kv => kv.Value.LastUsedUtcTicks).Take(store.Count - SettingsStore.MaxWindowPlacements).Select(kv => kv.Key).ToArray())
                    store.Remove(stale);
            snapshot = GlideSettingsState.ClonePlacements(store);
        }
        if (PersistOverrideForTests is { } sink) { sink(snapshot); return; }
        SettingsStore.SaveWindowPlacements(snapshot);
    }

    /// <summary>Copies the live store into <paramref name="state"/> (called right before the main window saves settings).</summary>
    public static void ApplyTo(GlideSettingsState state)
    {
        if (state is null) return;
        lock (Gate)
        {
            // First use in this process: the state being saved was loaded from the same file, so it is the seed.
            _store ??= GlideSettingsState.ClonePlacements(state.WindowPlacements);
            state.WindowPlacements = GlideSettingsState.ClonePlacements(_store);
        }
    }

    /// <summary>
    /// Registers <paramref name="window"/> under <paramref name="key"/>: restores the remembered placement now (call it
    /// before the window is shown, after its default size and minimum size are set) and saves it when the window closes.
    /// <paramref name="mode"/> defaults to size-and-position for resizable windows and position-only for fixed-size or
    /// content-sized ones. Calling it twice for the same window is a no-op.
    /// </summary>
    public static void Track(Window window, string key, WindowPlacementMode? mode = null)
    {
        if (window is null || string.IsNullOrWhiteSpace(key) || IsTracked(window)) return;
        CleanLegacyPopupPlacementFile();
        // Default (nothing saved or nothing valid): centred on the owner, as every Glide dialog always opened.
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var resolvedMode = mode ?? (window.CanResize && window.SizeToContent == SizeToContent.Manual
            ? WindowPlacementMode.SizeAndPosition
            : WindowPlacementMode.PositionOnly);
        var tracker = new Tracker(window, key, resolvedMode);
        Tracked.AddOrUpdate(window, tracker);
        if (resolvedMode == WindowPlacementMode.PositionOnly)
        {
            // Dialogs never remember a position: a restored one overrode the centring (5.0.6: "Unsaved settings changes"
            // always opened at a saved 0,0, the screen's top-left). They always open centred on their owner, and a
            // position an older build saved for them is dropped.
            Forget(key);
            DialogPlacement.CenterOnOwner(window);
            return;
        }
        tracker.Restore();
        tracker.Hook();
        // Nothing remembered (first open, or the saved spot was off-screen): centre on the owner and keep it wholly on
        // the owner's monitor, shrinking a too-large resizable window to the working area.
        if (window.WindowStartupLocation == WindowStartupLocation.CenterOwner) DialogPlacement.CenterOnOwner(window);
    }

    private static void CleanLegacyPopupPlacementFile()
    {
        if (_legacyPlacementCleaned) return;
        _legacyPlacementCleaned = true;
        try
        {
            // A very old build kept per-popup coordinates in its own file; that data is not migrated.
            var legacy = Path.Combine(SettingsStore.GetSettingsDirectory(), "popup-placement.json");
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch (Exception loggedEx) { Glide.Diagnostics.Runtime.LiveDiagnosticTrace.WriteException("placement", "legacy_placement_cleanup_failed", loggedEx); }
    }

    internal static (IReadOnlyList<ScreenWorkArea> Screens, ScreenWorkArea Primary) ScreensOf(Window window)
    {
        if (ScreensOverrideForTests is { } fake) return fake(window);
        var screens = new List<ScreenWorkArea>();
        var primary = default(ScreenWorkArea);
        try
        {
            foreach (var screen in window.Screens.All)
                screens.Add(new ScreenWorkArea(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height, screen.Scaling));
            if (window.Screens.Primary is { } p)
                primary = new ScreenWorkArea(p.WorkingArea.X, p.WorkingArea.Y, p.WorkingArea.Width, p.WorkingArea.Height, p.Scaling);
        }
        catch { /* screen enumeration can fail during display changes; placement then uses its defaults */ }
        return (screens, primary);
    }

    private sealed class Tracker
    {
        private readonly WeakReference<Window> _window;
        private Size _normalSize;
        private PixelPoint _normalPosition = new(int.MinValue, int.MinValue);
        private bool _hasNormalPosition;
        private WindowPlacementState? _captured;

        public Tracker(Window window, string key, WindowPlacementMode mode)
        {
            _window = new WeakReference<Window>(window);
            Key = key;
            Mode = mode;
        }

        public string Key { get; }
        public WindowPlacementMode Mode { get; }

        public void Restore()
        {
            if (!_window.TryGetTarget(out var window)) return;
            try
            {
                if (Get(Key) is not { } saved) return;
                var (screens, primary) = ScreensOf(window);
                var currentWidth = Known(window.Width) ?? Known(window.MinWidth) ?? 400;
                var currentHeight = Known(window.Height) ?? Known(window.MinHeight) ?? 200;
                var resolved = WindowPlacementResolver.Resolve(
                    new SavedWindowPlacement(saved.X, saved.Y, saved.Width, saved.Height, saved.Maximized),
                    screens, primary, Mode,
                    Known(window.MinWidth) ?? 0, Known(window.MinHeight) ?? 0,
                    currentWidth, currentHeight);
                if (resolved is not { } r) return;
                if (r.Width is { } width && r.Height is { } height)
                {
                    window.MinWidth = r.MinWidth;
                    window.MinHeight = r.MinHeight;
                    window.Width = width;
                    window.Height = height;
                    _normalSize = new Size(width, height);
                }
                if (r.HasPosition)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Position = new PixelPoint(r.X!.Value, r.Y!.Value);
                    _normalPosition = window.Position;
                    _hasNormalPosition = true;
                }
                if (r.Maximized) window.WindowState = WindowState.Maximized;
            }
            catch
            {
                // A broken saved placement must never stop a window from opening; its defaults apply.
            }
        }

        private static double? Known(double value) => double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? null : value;

        public void Hook()
        {
            if (!_window.TryGetTarget(out var window)) return;
            window.Opened += (_, _) => RememberNormalBounds();
            window.PositionChanged += (_, _) => RememberNormalBounds();
            window.Resized += (_, _) => RememberNormalBounds();
            window.Closing += (_, _) => Capture();
            window.Closed += (_, _) =>
            {
                if (_captured is { } placement) Remember(Key, placement);
            };
        }

        private void RememberNormalBounds()
        {
            if (!_window.TryGetTarget(out var window) || window.WindowState != WindowState.Normal) return;
            if (window.ClientSize.Width > 0 && window.ClientSize.Height > 0) _normalSize = window.ClientSize;
            _normalPosition = window.Position;
            _hasNormalPosition = true;
        }

        private void Capture()
        {
            try
            {
                if (!_window.TryGetTarget(out var window)) return;
                RememberNormalBounds();
                if (!_hasNormalPosition) return;
                var maximized = window.WindowState == WindowState.Maximized;
                if (Mode == WindowPlacementMode.SizeAndPosition)
                {
                    if (_normalSize.Width <= 0 || _normalSize.Height <= 0) return;
                    _captured = new WindowPlacementState
                    {
                        X = _normalPosition.X, Y = _normalPosition.Y,
                        Width = Math.Round(_normalSize.Width, 1), Height = Math.Round(_normalSize.Height, 1),
                        Maximized = maximized
                    };
                }
                else
                {
                    _captured = new WindowPlacementState { X = _normalPosition.X, Y = _normalPosition.Y };
                }
            }
            catch { /* window metrics unavailable mid-close; the previously captured placement is kept */ }
        }
    }
}
