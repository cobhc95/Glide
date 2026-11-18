namespace Glide.Core.Commands;

/// <summary>What a surface does with a resolved gesture row.</summary>
public enum GestureOutcome
{
    /// <summary>The row is Default: the surface keeps its own built-in behaviour (scroll, fullscreen toggle, menu...).</summary>
    BuiltIn,
    /// <summary>The input is not claimed at all (side buttons on Default, "Ignore here"): Windows keeps it.</summary>
    PassThrough,
    /// <summary>The configured <see cref="GestureDecision.Action"/> runs and the input is handled.</summary>
    Run,
}

public readonly record struct GestureDecision(GestureOutcome Outcome, string Action)
{
    public bool Runs => Outcome == GestureOutcome.Run;
}

/// <summary>
/// Stage D step 3: the one place that turns (surface, physical input, held key, bindings) into a decision. Surfaces
/// (ImageViewport, the title bar, tab strip, Explorer, side buttons) used to compare against
/// <see cref="GestureCatalog.Default"/> each in their own way; the precedence is documented in
/// <c>docs/audits/stage-d-gesture-precedence.md</c> and pinned by <c>GestureResolverTests</c>.
/// Pure: no UI types, so the precedence is testable without a window.
/// </summary>
public static class GestureResolver
{
    private static bool IsDefault(string action) => string.Equals(action, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase);

    /// <summary>Slot binding with custom gestures first (same as <see cref="GestureCatalog.ResolveAction(string, IReadOnlyDictionary{string, string}?, IReadOnlyList{CustomGesture}?, string?)"/>).</summary>
    public static string Action(string slotId, IReadOnlyDictionary<string, string>? bindings, IReadOnlyList<CustomGesture>? custom, string? modifier) =>
        GestureCatalog.ResolveAction(slotId, bindings, custom, modifier);

    /// <summary>
    /// Rows whose Default means "the surface's own behaviour": title-bar wheel / right-click, tab-strip wheel, Explorer
    /// wheel, double-click rows. Anything else (including Nothing) runs and claims the input.
    /// </summary>
    public static GestureDecision ForBuiltInRow(string action) =>
        IsDefault(action) ? new(GestureOutcome.BuiltIn, action) : new(GestureOutcome.Run, action);

    /// <summary>
    /// Side-button rows (5.0.8): Default falls back to the catalog's default action; if there is none the press is
    /// passed through unhandled so Windows / the mouse software keep it. "Do nothing" blocks; any action runs.
    /// </summary>
    public static GestureDecision ForSideButton(string slotId, string action)
    {
        if (IsDefault(action)) action = GestureCatalog.DefaultActionFor(slotId) ?? action;
        return IsDefault(action) ? new(GestureOutcome.PassThrough, action) : new(GestureOutcome.Run, action);
    }

    /// <summary>
    /// Explorer item rows: double-click (also Enter / Open) and middle-click. Default opens in the current tab /
    /// a new tab. Another action runs instead, except from the context menu, whose explicit Open items always open.
    /// </summary>
    public static string ForExplorerOpen(string action, bool alternate, bool fromMenu)
    {
        var open = alternate ? GestureCatalog.OpenInNewTab : GestureCatalog.OpenInCurrentTab;
        if (action == GestureCatalog.Default) return open;
        if (action is GestureCatalog.OpenInCurrentTab or GestureCatalog.OpenInNewTab) return action;
        return fromMenu ? open : action;
    }

    /// <summary>Which area a side-button press belongs to, by what is under the pointer (first match wins).</summary>
    public static string? SideButtonArea(bool onTabStrip, bool onVisibleExplorer, bool onVisibleImage, bool onVisibleHome, bool fullscreen)
    {
        if (onTabStrip) return GestureAreas.Tabs;
        if (onVisibleExplorer) return GestureAreas.Explorer;
        if (onVisibleImage) return fullscreen ? GestureAreas.Fullscreen : GestureAreas.Windowed;
        // The Welcome / Home page follows the Windowed image rows.
        if (onVisibleHome) return GestureAreas.Windowed;
        return null;
    }

    /// <summary>The viewer double-left-click row: fullscreen, on the picture, or on the empty background.</summary>
    public static string DoubleLeftSlot(bool fullscreen, bool overImage) =>
        fullscreen ? "fullscreen.doubleLeftImage" : overImage ? "windowed.doubleLeftImage" : "background.doubleLeft";

    /// <summary>The wheel row for the held key (Ctrl beats Shift beats Alt).</summary>
    public static string WheelSlot(bool fullscreen, bool ctrl, bool shift, bool alt) =>
        ctrl ? (fullscreen ? "wheel.fullscreenCtrl" : "wheel.windowedCtrl")
        : shift ? (fullscreen ? "wheel.fullscreenShift" : "wheel.windowedShift")
        : alt ? (fullscreen ? "wheel.fullscreenAlt" : "wheel.windowedAlt")
        : fullscreen ? "wheel.fullscreen" : "wheel.windowed";

    /// <summary>
    /// The viewer wheel precedence: the held-key row; Shift / Alt on Default fall back to the plain row ("as wheel");
    /// a non-Default slideshow row overrides while a slideshow is active; a non-Default right-held row overrides
    /// while the right button is down. Returns the action (Default = the viewport's built-in zoom/browse).
    /// </summary>
    public static string WheelAction(Func<string, string> resolve, bool fullscreen, bool ctrl, bool shift, bool alt,
        bool slideshowActive, bool rightHeld)
    {
        var configured = resolve(WheelSlot(fullscreen, ctrl, shift, alt));
        if (!ctrl && (shift || alt) && IsDefault(configured)) configured = resolve(WheelSlot(fullscreen, false, false, false));
        if (slideshowActive)
        {
            var slideshow = resolve("slideshow.wheel");
            if (!IsDefault(slideshow)) configured = slideshow;
        }
        if (rightHeld)
        {
            var held = resolve("wheel.rightHeld");
            if (!IsDefault(held)) configured = held;
        }
        return configured;
    }
}
