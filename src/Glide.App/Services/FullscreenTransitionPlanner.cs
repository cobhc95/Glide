using Avalonia;
using Avalonia.Controls;

namespace Glide.App.Services;

/// <summary>How the window is kept from showing intermediate frames while its geometry changes.</summary>
internal enum FullscreenTransitionFence
{
    /// <summary>No hiding: the geometry change is small (maximized and fullscreen are almost the same rectangle).</summary>
    None,
    /// <summary>DWM-cloak the HWND for the native change and uncloak once the first frame at the final geometry is composed.</summary>
    Cloak,
}

/// <summary>
/// The native steps of leaving fullscreen. <see cref="StepThroughNormal"/> is the legacy path (an intermediate
/// <see cref="WindowState.Normal"/> state whose bounds are re-applied before the target state); it is only needed
/// when the native restore rectangle is not already the remembered ordinary-window bounds.
/// </summary>
internal readonly record struct FullscreenExitPlan(WindowState TargetState, bool StepThroughNormal, FullscreenTransitionFence Fence)
{
    public string Path => StepThroughNormal ? "via_normal" : "direct";
}

/// <summary>
/// Pure planning for fullscreen enter/exit (5.0.2). Avalonia's Win32 fullscreen keeps the window's native
/// maximized/normal state underneath (WS_MAXIMIZE and the WINDOWPLACEMENT restore rectangle survive fullscreen), and
/// leaving fullscreen restores the rectangle saved on entry. So:
/// <list type="bullet">
/// <item>Maximized -> fullscreen -> exit goes straight back to Maximized: one native step, no intermediate Normal
/// geometry, and the restore rectangle is untouched (un-maximizing later returns to the real normal bounds).</item>
/// <item>Normal -> fullscreen -> exit lands on the saved rectangle, which is the remembered normal bounds; they are
/// only re-applied when they really differ.</item>
/// <item>The legacy Normal step is kept for Minimized targets (un-minimizing returns to a normal window, as before)
/// and when the remembered normal bounds differ from the rectangle Avalonia saved on entry.</item>
/// </list>
/// Only transitions whose geometry changes a lot (to or from an ordinary window) are fenced. Cloaking a
/// maximized&lt;-&gt;fullscreen change would blank the whole screen for a frame to hide a few pixels of difference.
/// </summary>
internal static class FullscreenTransitionPlanner
{
    public const string RestoreBehavior = "Restore size and location";
    public const string MinimizedBehavior = "Minimized";
    public const string MaximizedBehavior = "Maximized";

    /// <summary>Safety net: a cloaked window is always revealed after this long, even if no frame is reported.</summary>
    public const int RevealTimeoutMs = 250;

    public static FullscreenTransitionFence PlanEnterFence(WindowState priorState) =>
        priorState == WindowState.Normal ? FullscreenTransitionFence.Cloak : FullscreenTransitionFence.None;

    /// <summary>
    /// Title-bar maximize/restore (caption button, title-bar double-click, hotkey). Normal&lt;-&gt;Maximized is a large
    /// geometry change, so it is fenced exactly like Normal&lt;-&gt;fullscreen; a no-op or fullscreen source is not.
    /// </summary>
    public static FullscreenTransitionFence PlanMaximizeToggleFence(WindowState currentState, WindowState targetState) =>
        currentState != targetState &&
        currentState is WindowState.Normal or WindowState.Maximized &&
        targetState is WindowState.Normal or WindowState.Maximized
            ? FullscreenTransitionFence.Cloak
            : FullscreenTransitionFence.None;

    /// <param name="capturedState">The window state captured when fullscreen was entered.</param>
    /// <param name="exitBehavior">The <c>FullscreenExitBehavior</c> setting (null = restore).</param>
    /// <param name="forceMaximized">"Exit fullscreen maximized" caption action.</param>
    /// <param name="nativeRestoreRectIsRememberedNormal">
    /// True when the rectangle Avalonia restores on leaving fullscreen already equals the remembered normal bounds
    /// (see <see cref="SameGeometry"/>). Only consulted for a window that entered fullscreen from Normal.
    /// </param>
    public static FullscreenExitPlan PlanExit(WindowState capturedState, string? exitBehavior, bool forceMaximized, bool nativeRestoreRectIsRememberedNormal)
    {
        var behavior = forceMaximized ? MaximizedBehavior : (exitBehavior ?? RestoreBehavior);

        if (string.Equals(behavior, MinimizedBehavior, StringComparison.OrdinalIgnoreCase))
            return new FullscreenExitPlan(WindowState.Minimized, StepThroughNormal: true, FullscreenTransitionFence.Cloak);

        if (string.Equals(behavior, RestoreBehavior, StringComparison.OrdinalIgnoreCase))
        {
            return capturedState switch
            {
                WindowState.Maximized => new FullscreenExitPlan(WindowState.Maximized, StepThroughNormal: false, FullscreenTransitionFence.None),
                WindowState.Minimized => new FullscreenExitPlan(WindowState.Minimized, StepThroughNormal: true, FullscreenTransitionFence.Cloak),
                _ => new FullscreenExitPlan(WindowState.Normal, StepThroughNormal: false, FullscreenTransitionFence.Cloak),
            };
        }

        // "Maximized" (and any unknown value, as before).
        if (capturedState == WindowState.Maximized || nativeRestoreRectIsRememberedNormal)
            return new FullscreenExitPlan(WindowState.Maximized, StepThroughNormal: false, FullscreenTransitionFence.None);
        return new FullscreenExitPlan(WindowState.Maximized, StepThroughNormal: true, FullscreenTransitionFence.Cloak);
    }

    /// <summary>
    /// Same window placement within one physical pixel. Sizes are DIPs; <paramref name="scaling"/> converts them so
    /// floating-point noise (1000.8 DIP at 125 %) never counts as a difference that would trigger another resize.
    /// </summary>
    public static bool SameGeometry(PixelPoint positionA, Size sizeA, PixelPoint positionB, Size sizeB, double scaling)
    {
        if (positionA != positionB) return false;
        var scale = double.IsFinite(scaling) && scaling > 0 ? scaling : 1.0;
        return Math.Abs((sizeA.Width - sizeB.Width) * scale) < 1.0 &&
               Math.Abs((sizeA.Height - sizeB.Height) * scale) < 1.0;
    }
}
