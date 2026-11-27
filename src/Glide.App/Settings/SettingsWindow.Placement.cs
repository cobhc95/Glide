using Avalonia;
using Avalonia.Controls;

namespace Glide.App.Settings;

/// <summary>
/// The Settings window geometry that is remembered between openings. Position is in device pixels and
/// size in DIPs (the size when not maximized), exactly like the main window's persisted placement.
/// A non-positive width or height means nothing is saved.
/// </summary>
public readonly record struct SettingsWindowPlacement(int X, int Y, double Width, double Height, bool Maximized)
{
    public static SettingsWindowPlacement None { get; } = new(int.MinValue, int.MinValue, 0, 0, false);
    public bool HasSize => Width > 0 && Height > 0;
    public bool HasPosition => X != int.MinValue && Y != int.MinValue;

    /// <summary>Key of the Settings window in the shared per-window placement store (<see cref="GlideSettingsState.WindowPlacements"/>).</summary>
    public const string StoreKey = "settings";

    /// <summary>
    /// Reads the remembered placement: the shared per-window store first, then the older SettingsWindow* properties
    /// (files written before the shared store existed), so an existing saved placement is never lost.
    /// </summary>
    public static SettingsWindowPlacement From(GlideSettingsState state)
    {
        if (state.WindowPlacements is { } store && store.TryGetValue(StoreKey, out var shared) && shared is not null && shared.HasSize)
            return new SettingsWindowPlacement(shared.X, shared.Y, shared.Width, shared.Height, shared.Maximized);
        return new SettingsWindowPlacement(
            state.SettingsWindowX, state.SettingsWindowY, state.SettingsWindowWidth, state.SettingsWindowHeight, state.SettingsWindowWasMaximized);
    }

    /// <summary>Writes the shared store entry and mirrors it into the older SettingsWindow* properties (so an older Glide build still finds it).</summary>
    public void WriteTo(GlideSettingsState state)
    {
        state.SettingsWindowX = X;
        state.SettingsWindowY = Y;
        state.SettingsWindowWidth = Width;
        state.SettingsWindowHeight = Height;
        state.SettingsWindowWasMaximized = Maximized;
        state.WindowPlacements ??= new Dictionary<string, WindowPlacementState>(StringComparer.OrdinalIgnoreCase);
        state.WindowPlacements[StoreKey] = ToState();
    }

    public WindowPlacementState ToState() => new()
    {
        X = X, Y = Y, Width = Width, Height = Height, Maximized = Maximized, LastUsedUtcTicks = DateTime.UtcNow.Ticks
    };

    public Glide.Core.Windows.SavedWindowPlacement ToShared() => new(X, Y, Width, Height, Maximized);
}

/// <summary>A monitor's usable area (device pixels) and its DPI scale.</summary>
public readonly record struct SettingsScreenArea(PixelRect Work, double Scale)
{
    public Glide.Core.Windows.ScreenWorkArea ToShared() => new(Work.X, Work.Y, Work.Width, Work.Height, Scale);
}

public partial class SettingsWindow
{
    // Window chrome (title bar and borders) that sits outside the client size, as an allowance when the
    // window has not been shown yet and its frame size is unknown.
    private const double ChromeAllowanceWidth = 16;
    private const double ChromeAllowanceHeight = 40;

    /// <summary>
    /// Called once when the window has closed, with the geometry to remember (also when closed through OK,
    /// Cancel or the close button). The owner persists it; the window never writes settings itself.
    /// </summary>
    public Action<SettingsWindowPlacement>? PlacementChanged { get; set; }

    private bool _placementRestored;
    private bool _placementCaptured;
    private SettingsWindowPlacement _lastPlacement = SettingsWindowPlacement.None;
    private Size _normalSize;
    private PixelPoint _normalPosition = new(int.MinValue, int.MinValue);

    /// <summary>
    /// Validates <paramref name="saved"/> against the monitors. Returns null when nothing is saved. The size is
    /// clamped to the monitor's working area (never below the minimum size the area allows), and the position is
    /// moved fully inside the working area of the monitor the window was on; a window saved on a monitor that is
    /// gone (or entirely off-screen) gets no position, so it opens centred on its owner instead.
    /// </summary>
    public static (Size Size, Size Minimum, PixelPoint? Position)? ResolveSavedPlacement(SettingsWindowPlacement saved, IReadOnlyList<SettingsScreenArea> screens, SettingsScreenArea primary)
    {
        // The rules are shared by every Glide window that remembers its placement (Glide.Core WindowPlacementResolver).
        var resolved = Glide.Core.Windows.WindowPlacementResolver.Resolve(
            saved.ToShared(),
            screens.Select(x => x.ToShared()).ToList(),
            primary.ToShared(),
            Glide.Core.Windows.WindowPlacementMode.SizeAndPosition,
            MinimumWidth, MinimumHeight);
        if (resolved is not { Width: { } width, Height: { } height } r) return null;
        PixelPoint? position = r.HasPosition ? new PixelPoint(r.X!.Value, r.Y!.Value) : null;
        return (new Size(width, height), new Size(r.MinWidth, r.MinHeight), position);
    }

    /// <summary>Restores the remembered placement before the window is shown; the compact default stays when nothing valid is saved.</summary>
    private void RestoreSavedPlacement(GlideSettingsState state)
    {
        try
        {
            var saved = SettingsWindowPlacement.From(state);
            if (!saved.HasSize) return;
            var screens = Screens.All.Select(s => new SettingsScreenArea(s.WorkingArea, s.Scaling)).ToList();
            var primary = Screens.Primary is { } p ? new SettingsScreenArea(p.WorkingArea, p.Scaling) : default;
            if (ResolveSavedPlacement(saved, screens, primary) is not { } resolved) return;
            Width = resolved.Size.Width;
            Height = resolved.Size.Height;
            MinWidth = resolved.Minimum.Width;
            MinHeight = resolved.Minimum.Height;
            _normalSize = resolved.Size;
            if (resolved.Position is { } position)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = position;
                _normalPosition = position;
            }
            if (saved.Maximized) WindowState = WindowState.Maximized;
            _placementRestored = true;
        }
        catch
        {
            // A broken saved placement must never stop Settings from opening; the default size applies.
        }
    }

    private void TrackNormalPlacement()
    {
        Opened += (_, _) =>
        {
            if (WindowState == WindowState.Normal) RememberNormalBounds();
        };
        PositionChanged += (_, _) => { if (WindowState == WindowState.Normal) RememberNormalBounds(); };
        Resized += (_, _) => { if (WindowState == WindowState.Normal) RememberNormalBounds(); };
        Closing += (_, _) => { CapturePlacement(); CaptureViewMemory(); };
        Closed += (_, _) =>
        {
            // The view first: the owner's placement callback is the one that saves the settings.
            if (_viewMemoryCaptured) ViewMemoryChanged?.Invoke(_lastViewMemory);
            if (_placementCaptured) PlacementChanged?.Invoke(_lastPlacement);
        };
    }

    private void RememberNormalBounds()
    {
        if (ClientSize.Width > 0 && ClientSize.Height > 0) _normalSize = ClientSize;
        _normalPosition = Position;
    }

    /// <summary>Takes the geometry to remember. Also stores it in the committed state, so a Cancel rollback that re-applies that state cannot bring back a stale placement.</summary>
    private void CapturePlacement()
    {
        try
        {
            if (WindowState == WindowState.Normal) RememberNormalBounds();
            if (_normalSize.Width <= 0 || _normalSize.Height <= 0) return;
            _lastPlacement = new SettingsWindowPlacement(_normalPosition.X, _normalPosition.Y, Math.Round(_normalSize.Width, 1), Math.Round(_normalSize.Height, 1), WindowState == WindowState.Maximized);
            _placementCaptured = true;
            _lastPlacement.WriteTo(_committed);
        }
        catch { /* placement capture is best effort; the last captured placement stays */ }
    }
}
