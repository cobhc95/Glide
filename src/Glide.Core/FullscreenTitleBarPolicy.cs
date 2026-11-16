namespace Glide.Core;

/// <summary>How Glide's 44-DIP title/tab row is laid out while the window is fullscreen.</summary>
public enum FullscreenTitleBarMode
{
    /// <summary>The bar takes its own row and stays visible; the active view starts directly beneath it (like windowed mode).</summary>
    Docked,
    /// <summary>Edge-to-edge: the bar overlays the top of the view, hidden until the pointer reaches the top edge.</summary>
    AutoHide,
    /// <summary>Edge-to-edge: the bar overlays the top of the view and is always shown ("Auto-hide chrome in fullscreen" off).</summary>
    Overlay,
}

/// <summary>
/// Headless decision for the fullscreen title/tab bar (5.0.2). The image viewer stays edge-to-edge by default, but a view
/// with its own navigation row at the top (the Explorer tab's Back / Forward / Up / address / filter row, or any future
/// docked Explorer panel) must never sit under the overlaid bar, where those controls would be covered.
/// </summary>
public static class FullscreenTitleBarPolicy
{
    /// <summary>The normal hot edge (DIP) at the top of a fullscreen window that reveals the auto-hidden bar.</summary>
    public const double RevealEdge = 14;

    /// <summary>
    /// The narrower hot edge used while a navigation row is visible at the top of the view and the bar is still
    /// auto-hidden (the "keep visible while Explorer is active" setting is off), so that row's controls stay clickable
    /// and only the very top of the screen reveals the bar.
    /// </summary>
    public const double NavigationRowRevealEdge = 4;

    /// <param name="keepTabBarOpen">"Keep tab/title bar open in fullscreen" (pins the bar for every view).</param>
    /// <param name="autoHideChrome">"Auto-hide chrome in fullscreen".</param>
    /// <param name="keepVisibleForExplorer">"Keep title bar visible in fullscreen while Explorer is active".</param>
    /// <param name="navigationRowVisible">
    /// The active view shows an Explorer-style navigation row at its top (the selected tab is an Explorer tab; an
    /// Explorer open in a background tab does not count).
    /// </param>
    public static FullscreenTitleBarMode Resolve(bool keepTabBarOpen, bool autoHideChrome, bool keepVisibleForExplorer, bool navigationRowVisible)
    {
        if (keepTabBarOpen) return FullscreenTitleBarMode.Docked;
        // A bar that is always shown would permanently cover the navigation row as an overlay, so it docks above it
        // whatever the Explorer setting says; with auto-hide the Explorer setting decides.
        if (navigationRowVisible && (keepVisibleForExplorer || !autoHideChrome)) return FullscreenTitleBarMode.Docked;
        return autoHideChrome ? FullscreenTitleBarMode.AutoHide : FullscreenTitleBarMode.Overlay;
    }

    /// <summary>The top hot edge (DIP) that reveals the auto-hidden bar.</summary>
    /// <param name="renderScaling">The window's render scaling (1.0 = 96 DPI).</param>
    /// <param name="navigationRowVisible">See <see cref="Resolve"/>.</param>
    public static double RevealEdgeFor(double renderScaling, bool navigationRowVisible) =>
        navigationRowVisible
            ? NavigationRowRevealEdge
            : Math.Max(RevealEdge, 18 / Math.Max(1.0, double.IsFinite(renderScaling) ? renderScaling : 1.0));
}
