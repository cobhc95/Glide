using Glide.App.Settings;

namespace Glide.App;

/// <summary>
/// Stage C (C3): applied settings changes are routed to the subsystem that owns them (the
/// <see cref="SettingEffectRegistry"/> owner of each changed id) instead of re-applying ~35 subsystems on every
/// preview. An owner handler lists the ids it applies narrowly; anything else (an id outside its list, an owner
/// without a handler, an unmapped change such as hotkeys or a derived field) falls back to the full
/// <see cref="ApplySettingsVisuals"/>. "Read at use" ids are read from <c>_settings</c> when the feature runs, so their
/// handler has nothing to do. When a new setting needs a live effect, either add its id here with the narrow apply, or
/// do nothing and it keeps the safe full re-apply.
/// </summary>
public partial class MainWindow
{
    // Stage C (C2): read-only per-feature views of the live settings (Glide.Core.Settings.SettingsOptionSlices). Built per
    // access from the current _settings, so they can never be stale; feature code reads these instead of _settings.X.
    private NavigationOptions NavigationOptionsView => new(_settings);
    private SlideshowOptions SlideshowOptionsView => new(_settings);
    private ExplorerOptions ExplorerOptionsView => new(_settings);
    private StatusBarOptions StatusBarOptionsView => new(_settings);
    private OverlayOptions OverlayOptionsView => new(_settings);

    /// <summary>The text overlay's look: a preview of only these restyles the overlay and skips everything else.</summary>
    internal static readonly IReadOnlySet<string> TextOverlayStyleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "overlay.pictureTemplate", "overlay.pictureFontSize", "overlay.pictureBold", "overlay.pictureOpacity",
        "overlay.picturePositionTopTabs", "overlay.picturePositionSideTabs", "overlay.pictureAlignmentTopTabs", "overlay.pictureAlignmentSideTabs", "overlay.pictureOffsetX", "overlay.pictureOffsetY", "overlay.pictureColor", "overlay.pictureShadow", "overlay.pictureBorder"
    };

    private sealed record SettingEffectHandler(IReadOnlySet<string> Ids, Action<MainWindow, IReadOnlyCollection<string>> Apply);

    private static HashSet<string> Ids(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);

    private static void ReadAtUse(MainWindow _, IReadOnlyCollection<string> __) { }

    /// <summary>One handler per owner that has narrow applies; owners not listed always use the full re-apply.</summary>
    private static readonly IReadOnlyDictionary<SettingEffectOwner, SettingEffectHandler> SettingEffectHandlers = new Dictionary<SettingEffectOwner, SettingEffectHandler>
    {
        // Rate limit, skip hint and end-of-folder are read per navigation; the sort order is applied by
        // ApplyNavigationSortOrder (always called before routing, it re-sorts and refreshes counter and stats itself).
        [SettingEffectOwner.Navigation] = new(Ids(
            "navigation.rateLimitMs", "navigation.rateLimitInput", "navigation.rateLimitIndicator", "navigation.rateLimitIndicatorPosition",
            "navigation.rateLimitWindowed", "navigation.rateLimitFullscreen", "navigation.rateLimitSlideshow", "navigation.skipHint",
            "navigation.endOfFolder", "navigation.sortOrder", "navigation.sortDescending"), ReadAtUse),
        // Sibling-folder traversal options are read when a folder boundary is crossed.
        [SettingEffectOwner.FolderNavigation] = new(Ids(
            "navigation.hierarchicalFolders", "navigation.confirmHierarchicalBoundary", "navigation.hierarchicalPreviousEntry"), ReadAtUse),
        // The slideshow controller reads its options through lambdas over _settings.
        [SettingEffectOwner.Slideshow] = new(Ids(
            "slideshow.interval", "slideshow.loop", "slideshow.crossFolders", "slideshow.shuffle", "slideshow.direction",
            "slideshow.startFullscreen", "slideshow.pauseInactive"), ReadAtUse),
        // Status-bar statistics only feed UpdateStatusStats.
        [SettingEffectOwner.StatusBar] = new(Ids(
            "status.statIndex", "status.statResolution", "status.statZoom", "status.statFileSize", "status.statFormat", "status.statFileName"),
            static (w, _) => w.UpdateStatusStats()),
        [SettingEffectOwner.Overlay] = new(TextOverlayStyleIds, static (w, _) => w.ApplyPictureCounterVisuals()),
        // Escape keys are read by HandleEscapeAsync; wheel inversion and the selection buffer are viewport properties.
        [SettingEffectOwner.Input] = new(Ids(
            "escape.stopSlideshow", "escape.exitFullscreen", "escape.confirmWindowedClose", "mouse.invertWheel",
            "selection.buffer", "selection.bufferAllModes", "selection.bufferWindowedPercent", "selection.bufferMaximizedPercent"),
            static (w, ids) =>
            {
                if (ids.Contains("mouse.invertWheel")) w.Viewport.InvertWheelDirection = w._settings.InvertWheelDirection;
                if (ids.Any(id => id.StartsWith("selection.", StringComparison.Ordinal))) w.ApplySelectionBuffer();
            }),
        [SettingEffectOwner.Viewing] = new(Ids("view.pointerZoom", "view.keepZoom", "view.zoomStep"), static (w, _) =>
        {
            w.Viewport.PointerCenteredZoomEnabled = w._settings.PointerZoom;
            w.Viewport.PreserveManualZoomOnBitmapChange = w._settings.PreserveManualZoomOnNavigate;
            w.Viewport.ZoomStepPercent = Glide.Core.ZoomStepPolicy.Clamp(w._settings.ViewportZoomStepPercent);
        }),
        [SettingEffectOwner.Developer] = new(Ids("developer.reverseSelectionZoomOut"),
            static (w, _) => w.Viewport.ReverseSelectionZoomOutScale = w._settings.ReverseSelectionZoomOutScale),
        // New-tab placement, tab cycling, last-tab behaviour and the Explorer wheel are read when used.
        [SettingEffectOwner.TabsWorkspace] = new(Ids(
            "tabs.cycleOrder", "tabs.lastTabBehavior", "tabs.newTabPosition", "tabs.switchToNewTabs", "tabs.newTabPositionKey", "tabs.newTabForegroundKey", "tabs.middleClickSide", "tabs.middleClickPosition", "tabs.afterCloseCustomTarget",
            "tabs.afterCloseActivate", "explorer.scrollSpeed", "explorer.scrollMode", "explorer.scrollRows", "explorer.scrollPixels"), ReadAtUse),
        // Settings-window-only preferences: the main window never reads them.
        [SettingEffectOwner.Fullscreen] = new(Ids("fullscreen.hideCursor", "fullscreen.hideCursorWindowed", "fullscreen.hideCursorDelay", "fullscreen.hideCursorMode"),
            static (w, _) => w.ResetFullscreenCursorTimer()),
        [SettingEffectOwner.General] = new(Ids("general.confirmDiscardSettings", "search.showRelatedResults"), ReadAtUse),
    };

    /// <summary>Applies changes narrowly so far (tests: a routed change must not cause a full <see cref="ApplySettingsVisuals"/>).</summary>
    internal int SettingsRoutedPassesForTests { get; private set; }

    /// <summary>The owner handlers, for the contract test that every listed id belongs to its owner.</summary>
    internal static IEnumerable<(SettingEffectOwner Owner, IReadOnlySet<string> Ids)> SettingEffectRoutesForTests =>
        SettingEffectHandlers.Select(kv => (kv.Key, kv.Value.Ids));

    /// <summary>
    /// Runs the owners' narrow handlers when every changed id has one and returns true; returns false (caller re-applies
    /// everything) for an empty or unmapped delta or any id without a narrow handler.
    /// </summary>
    private bool TryApplyRoutedSettingEffects(SettingsDelta delta)
    {
        if (delta.IsEmpty || delta.HasUnmappedChanges) return false;
        var byOwner = new Dictionary<SettingEffectOwner, List<string>>();
        foreach (var id in delta.ChangedIds)
        {
            if (SettingEffectRegistry.Find(id) is not { } binding) return false;
            if (!SettingEffectHandlers.TryGetValue(binding.EffectOwner, out var handler) || !handler.Ids.Contains(id)) return false;
            if (!byOwner.TryGetValue(binding.EffectOwner, out var list)) byOwner[binding.EffectOwner] = list = new List<string>();
            list.Add(id);
        }
        foreach (var (owner, ids) in byOwner) SettingEffectHandlers[owner].Apply(this, ids);
        SettingsRoutedPassesForTests++;
        _diagnostics.Write("settings", "routed", new { ids = string.Join(",", delta.ChangedIds), owners = string.Join(",", byOwner.Keys) });
        return true;
    }
}
