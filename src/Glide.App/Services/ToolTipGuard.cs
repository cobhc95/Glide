using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Glide.App.Services;

/// <summary>
/// Tooltip placement and wheel behaviour. Avalonia opens tooltips at the pointer by default, which put the popup on
/// top of the hovered toolbar button (it could no longer be clicked and a neighbouring button never closed it), so
/// tooltips are anchored to their control instead, opening away from the window edge the control sits on.
/// It also keeps tooltips from getting in the way of the mouse wheel. A tooltip is its own popup window: while the
/// pointer is over it, the wheel goes to the popup instead of the list or page underneath, so scrolling
/// (for example the keyboard shortcut list in Settings) stopped working once a tooltip had appeared under
/// the cursor. <c>IsHitTestVisible=False</c> on the ToolTip (GlideTheme.axaml) does not help there: Windows routes
/// WM_MOUSEWHEEL to the native window under the cursor, which is the tooltip's own popup window, before Avalonia's
/// hit-testing runs. And a tooltip whose show delay started just before a wheel tick still opened between two ticks
/// (5.0.6, Settings > Mouse gestures area rail). These measures work together:
/// <list type="bullet">
/// <item>every wheel tick that reaches a control first closes any tooltip open on it or its parents;</item>
/// <item>no tooltip opens for <see cref="WheelQuietPeriod"/> after a wheel tick (a pending show delay is cancelled);</item>
/// <item>tooltips for rows of a scrolling list open beside the pointer, away from it, instead of hugging the row;</item>
/// <item>the tooltip's popup window is transparent to the mouse (WM_NCHITTEST answers HTTRANSPARENT, so Windows gives
/// the input to the Glide window underneath, and it never activates); a wheel message that still reaches it closes
/// the tooltip and is handed on to the window the tooltip belongs to;</item>
/// <item>the tooltip control itself is not hit-testable (see the ToolTip style in GlideTheme.axaml).</item>
/// </list>
/// </summary>
internal static class ToolTipGuard
{
    /// <summary>Offset (in DIPs) from the pointer for tooltips of scrolling-list rows: clear of the cursor glyph.</summary>
    internal const double PointerOffsetX = 18;
    internal const double PointerOffsetY = 30;

    /// <summary>How long after a wheel tick no tooltip may open (longer than the pause between the ticks of one scroll).</summary>
    internal static readonly TimeSpan WheelQuietPeriod = TimeSpan.FromMilliseconds(600);

    // Threading: every handler below and the popup window hook run on the UI thread (Avalonia's class handlers, and
    // Windows calls a window procedure on the thread that owns the window), so these statics need no locking.
    private static bool s_registered;
    private static long s_lastWheelTicks = long.MinValue / 2;

    internal static bool IsInWheelQuietPeriod(long nowTicks) => nowTicks - s_lastWheelTicks < (long)WheelQuietPeriod.TotalMilliseconds;

    /// <summary>Tests: forgets the last wheel tick, so a test never starts inside the quiet period a previous test's wheel tick began.</summary>
    internal static void ResetWheelQuietPeriodForTests() => s_lastWheelTicks = long.MinValue / 2;

    /// <summary>
    /// Marks a control that sets its own tooltip placement (tab headers: beside a side tab, under a top tab with hover
    /// previews). The guard then leaves its placement and offsets alone; the wheel and stuck-tooltip handling still apply.
    /// </summary>
    internal static readonly AttachedProperty<bool> KeepsOwnPlacementProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("KeepsOwnPlacement", typeof(ToolTipGuard));

    internal static bool GetKeepsOwnPlacement(Control control) => control.GetValue(KeepsOwnPlacementProperty);

    /// <summary>
    /// A control whose open tooltip uses some wheel ticks itself (round 3 #5: the resize key + wheel over a tab resizes
    /// its hover preview). When the predicate is true for a tick, the guard leaves that control's tooltip open.
    /// </summary>
    internal static readonly AttachedProperty<Func<PointerWheelEventArgs, bool>?> KeepsOpenOnWheelProperty =
        AvaloniaProperty.RegisterAttached<Control, Func<PointerWheelEventArgs, bool>?>("KeepsOpenOnWheel", typeof(ToolTipGuard));

    internal static void SetKeepsOpenOnWheel(Control control, Func<PointerWheelEventArgs, bool>? value) => control.SetValue(KeepsOpenOnWheelProperty, value);
    internal static void SetKeepsOwnPlacement(Control control, bool value) => control.SetValue(KeepsOwnPlacementProperty, value);

    /// <summary>Anchoring for a tooltip: the control's edge, or beside the pointer for rows of a scrolling list.</summary>
    internal static (PlacementMode Mode, double HorizontalOffset, double VerticalOffset) Placement(bool insideScrollingList, bool lowerHalf) =>
        insideScrollingList ? (PlacementMode.Pointer, PointerOffsetX, PointerOffsetY)
        : lowerHalf ? (PlacementMode.Top, 0, -6)
        : (PlacementMode.Bottom, 0, 6);

    internal static bool IsInsideScrollingList(Control control)
    {
        for (var current = control as Visual; current is not null; current = current.GetVisualParent())
            if (current is ListBox or ListBoxItem) return true;
        return false;
    }

    /// <summary>Idempotent; called once from application start-up (and by headless tests).</summary>
    internal static void Register()
    {
        if (s_registered) return;
        s_registered = true;

        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, e) =>
        {
            // The show delay can expire after a click opened a modal dialog: the owner window is then disabled, never
            // sees the pointer leave, and the tip would float over the dialog blocking every other tip. A window that is
            // merely inactive (the user is in another app, or a menu / popup / drag just ended) still shows tooltips, as
            // Windows does; blocking those left every tooltip dead until a click re-activated Glide (5.0.6).
            // A tip whose show delay ran out while the user was scrolling would appear under the pointer between two ticks.
            if (IsBlockedByModal(TopLevel.GetTopLevel(control) as Window) || IsInWheelQuietPeriod(Environment.TickCount64))
            {
                if (e is Avalonia.Interactivity.CancelRoutedEventArgs cancel) cancel.Cancel = true;
                return;
            }
            Track(control);
            if (GetKeepsOwnPlacement(control) || TopLevel.GetTopLevel(control) is not { } top) return;
            var origin = control.TranslatePoint(default, top);
            if (origin is null) return;
            var lowerHalf = origin.Value.Y + control.Bounds.Height / 2 > top.Bounds.Height / 2;
            var (mode, x, y) = Placement(IsInsideScrollingList(control), lowerHalf);
            ToolTip.SetPlacement(control, mode);
            ToolTip.SetHorizontalOffset(control, x);
            ToolTip.SetVerticalOffset(control, y);
        }, handledEventsToo: true);

        // Tunnel + handledEventsToo: runs before the list/scroll viewer consumes the tick, so a tooltip that
        // is open when the wheel turns is gone before the content moves underneath it.
        InputElement.PointerWheelChangedEvent.AddClassHandler<Control>((control, e) =>
        {
            s_lastWheelTicks = Environment.TickCount64;
            CloseOpenToolTips(control, e);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        // Each tooltip opens in a new native popup window: make that window transparent to the mouse once it is shown.
        Control.LoadedEvent.AddClassHandler<ToolTip>((tip, _) => MakePopupInputTransparent(tip));

        // Stuck tooltips: the ToolTipService closes a tip only when it sees the pointer leave its owner. A click
        // that opens a modal dialog (ShowDialog disables the owner window, so it never gets the leave/move) or
        // rebuilds the owner leaves the tip open, floating over the dialog, and while the service still holds
        // that owner no other tooltip opens anywhere (footer change summary, customiser rows). So every open tip
        // is closed on any pointer press, whenever a window stops being active, and whenever a window opens.
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, _) => CloseAllOpenToolTips(),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        WindowBase.IsActiveProperty.Changed.AddClassHandler<Window>((_, e) =>
        {
            if (e.NewValue is false) CloseAllOpenToolTips();
        });
        Window.WindowOpenedEvent.AddClassHandler<Window>((_, _) => CloseAllOpenToolTips());
    }

    /// <summary>
    /// True when <paramref name="window"/> cannot take input because a modal dialog owns it: Avalonia disabled it, or
    /// Windows did (ShowDialog disables the owner's native window), or one of its owned windows is a dialog still open.
    /// An inactive window is not blocked.
    /// </summary>
    internal static bool IsBlockedByModal(Window? window)
    {
        if (window is null) return false;
        if (!window.IsEnabled) return true;
        if (window.OwnedWindows.Any(w => w.IsVisible && IsModalDialog(w))) return true;
        return OperatingSystem.IsWindows() && window.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero && !IsWindowEnabled(hwnd);
    }

    // Avalonia keeps whether a window is showing with ShowDialog in a non-public field; read it when present.
    private static readonly System.Reflection.FieldInfo? s_showingAsDialogField =
        typeof(Window).GetField("_showingAsDialog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static bool IsModalDialog(Window window) => s_showingAsDialogField?.GetValue(window) is true;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    private static readonly List<WeakReference<Control>> s_openOwners = new();

    private static readonly ConditionalWeakTable<Control, object> s_detachHooked = new();

    private static void Track(Control control)
    {
        // The service closes a tip only when it sees the pointer leave. An owner removed from the tree while its tip is open
        // (Settings search results rebuilt by the next keystroke, an Explorer tile recycled) never sees that, so close the tip
        // with it (5.1.7 UI test bug 6).
        if (s_detachHooked.TryAdd(control, s_detachHooked))
            control.DetachedFromVisualTree += (_, _) => { if (ToolTip.GetIsOpen(control)) ToolTip.SetIsOpen(control, false); };
        s_openOwners.RemoveAll(r => !r.TryGetTarget(out var c) || ReferenceEquals(c, control) || !c.CheckAccess());
        s_openOwners.Add(new WeakReference<Control>(control));
    }

    /// <summary>Closes every tooltip that has opened and may still be open, in any window.</summary>
    internal static void CloseAllOpenToolTips()
    {
        foreach (var r in s_openOwners)
            if (r.TryGetTarget(out var c) && c.CheckAccess() && ToolTip.GetIsOpen(c)) ToolTip.SetIsOpen(c, false);
        s_openOwners.Clear();
    }

    // ---- the tooltip's native popup window -------------------------------------------------------------------------

    internal enum PopupMessageAction { Default, HitTestTransparent, NoActivate, ForwardWheel }

    private const uint WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    private const int HTTRANSPARENT = -1, MA_NOACTIVATE = 3;

    /// <summary>What the tooltip popup window does with a message (pure, so the rule is testable without a desktop).</summary>
    internal static PopupMessageAction ClassifyPopupMessage(uint message) => message switch
    {
        WM_NCHITTEST => PopupMessageAction.HitTestTransparent,
        WM_MOUSEACTIVATE => PopupMessageAction.NoActivate,
        WM_MOUSEWHEEL or WM_MOUSEHWHEEL => PopupMessageAction.ForwardWheel,
        _ => PopupMessageAction.Default
    };

    // One hook per popup window (a tooltip gets a new popup each time it opens; the table lets them be collected).
    private static readonly ConditionalWeakTable<TopLevel, Win32Properties.CustomWndProcHookCallback> s_hookedPopups = new();

    private static void MakePopupInputTransparent(ToolTip tip)
    {
        if (!OperatingSystem.IsWindows() || TopLevel.GetTopLevel(tip) is not { } popup || popup is Window || s_hookedPopups.TryGetValue(popup, out _)) return;
        // The window the tooltip belongs to receives a wheel message that reaches the popup anyway.
        var owner = (tip.Parent as Popup)?.PlacementTarget is { } target ? TopLevel.GetTopLevel(target) : null;
        Win32Properties.CustomWndProcHookCallback hook = (IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            switch (ClassifyPopupMessage(message))
            {
                case PopupMessageAction.HitTestTransparent:
                    handled = true;
                    return new IntPtr(HTTRANSPARENT);
                case PopupMessageAction.NoActivate:
                    handled = true;
                    return new IntPtr(MA_NOACTIVATE);
                case PopupMessageAction.ForwardWheel:
                    handled = true;
                    var ownerHandle = owner?.TryGetPlatformHandle()?.Handle ?? GetWindow(hwnd, GW_OWNER);
                    if (ownerHandle != IntPtr.Zero) PostMessage(ownerHandle, message, wParam, lParam);
                    s_lastWheelTicks = Environment.TickCount64;
                    // Not from inside the popup's own window procedure: closing the tip destroys this window.
                    Dispatcher.UIThread.Post(CloseAllOpenToolTips);
                    return IntPtr.Zero;
                default:
                    return IntPtr.Zero;
            }
        };
        s_hookedPopups.Add(popup, hook);
        Win32Properties.AddWndProcHookCallback(popup, hook);
    }

    private const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Closes the tooltip of <paramref name="source"/> and of every visual parent that has one open.</summary>
    internal static void CloseOpenToolTips(Visual? source, PointerWheelEventArgs? wheel = null)
    {
        for (var current = source; current is not null; current = current.GetVisualParent())
            if (current is Control control && ToolTip.GetIsOpen(control)
                && !(wheel is not null && control.GetValue(KeepsOpenOnWheelProperty) is { } keep && keep(wheel)))
                ToolTip.SetIsOpen(control, false);
    }
}
