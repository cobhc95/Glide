using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Styling;

namespace Glide.App.Services;

/// <summary>
/// One central mechanism that keeps the mouse pointer visible over every menu, sub-menu, drop-down and flyout.
///
/// Root cause it fixes: <c>InputElement.Cursor</c> is an inherited property and every popup (context menu,
/// sub-menu, ComboBox drop-down, flyout) is a separate top-level <c>PopupRoot</c> whose inheritance parent is the
/// window it was opened from. MainWindow hides the pointer by setting <c>Cursor = None</c> on itself (fullscreen
/// "hide cursor when idle"), so every popup opened from it inherited the "no cursor" value and the pointer vanished
/// inside the menu. The main window also receives no pointer events while the pointer is inside a popup, so the
/// idle timer kept running and hid the pointer over a menu that was already open.
///
/// Two measures, both global:
/// <list type="number">
/// <item>Application styles give <c>PopupRoot</c>, <c>OverlayPopupHost</c>, <c>ContextMenu</c> and <c>MenuItem</c>
/// an explicit Arrow cursor, so nothing inside a popup resolves to the inherited "None" (or a stale Hand/Cross cursor
/// of the control it was opened from). Controls that set their own cursor (text boxes, grips) still win.</item>
/// <item>A registry of open popups lets the main window lift idle cursor hiding while any popup it owns is open
/// (see <c>MainWindow.PopupCursor.cs</c>) and restart the idle countdown afterwards.</item>
/// </list>
/// </summary>
internal static class PopupCursorGuard
{
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly object Gate = new();
    private static readonly List<Popup> OpenPopups = new();
    private static bool _registered;
    private static Styles? _styles;

    /// <summary>Raised on the UI thread whenever a popup opens or closes.</summary>
    internal static event Action? OpenPopupsChanged;

    /// <summary>The cursor every popup surface resolves to unless a control picks its own.</summary>
    internal static Cursor Arrow => ArrowCursor;

    /// <summary>Idempotent; called from application start-up (and by headless tests).</summary>
    internal static void Register()
    {
        lock (Gate)
        {
            if (!_registered)
            {
                _registered = true;
                Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, e) =>
                {
                    if (e.NewValue is true) Track(popup);
                    else Untrack(popup);
                });
            }
        }
        InstallStyles();
    }

    /// <summary>
    /// (Re)adds the cursor styles to the current Application. Safe to call repeatedly; used by tests whose
    /// application object is created after the first registration.
    /// </summary>
    internal static void InstallStyles()
    {
        if (Application.Current is not { } app) return;
        lock (Gate)
        {
            if (_styles is not null && app.Styles.Contains(_styles)) return;
            _styles = BuildStyles();
            app.Styles.Add(_styles);
        }
    }

    internal static Styles BuildStyles()
    {
        var styles = new Styles();
        void Add(Func<Selector?, Selector> selector)
        {
            var style = new Style(selector);
            style.Setters.Add(new Setter(InputElement.CursorProperty, ArrowCursor));
            styles.Add(style);
        }
        Add(x => x.OfType<PopupRoot>());
        Add(x => x.OfType<OverlayPopupHost>());
        Add(x => x.OfType<ContextMenu>());
        Add(x => x.OfType<MenuItem>());
        // Items of an ItemsControl inherit from their *logical* parent (the ComboBox), not from the popup that
        // displays them, so drop-down rows and flyout content need the explicit cursor as well.
        Add(x => x.OfType<ComboBoxItem>());
        Add(x => x.OfType<FlyoutPresenter>());
        Add(x => x.OfType<MenuFlyoutPresenter>());
        return styles;
    }

    /// <summary>True while at least one non-tooltip popup belonging to <paramref name="owner"/> is open.</summary>
    internal static bool HasOpenPopup(TopLevel owner) => CountOpen(owner) > 0;

    internal static int CountOpen(TopLevel owner)
    {
        lock (Gate)
        {
            OpenPopups.RemoveAll(p => !p.IsOpen);
            var count = 0;
            foreach (var popup in OpenPopups)
                if (OwnerOf(popup) is { } top && ReferenceEquals(top, owner)) count++;
            return count;
        }
    }

    private static TopLevel? OwnerOf(Popup popup)
    {
        var anchor = popup.PlacementTarget ?? popup.Parent as Visual ?? popup;
        return TopLevel.GetTopLevel(anchor);
    }

    private static void Track(Popup popup)
    {
        // Tooltips are popups too but they are not hit-testable and open only under a stationary pointer.
        if (popup.Child is ToolTip) return;
        lock (Gate) if (!OpenPopups.Contains(popup)) OpenPopups.Add(popup);
        OpenPopupsChanged?.Invoke();
    }

    private static void Untrack(Popup popup)
    {
        bool removed;
        lock (Gate) removed = OpenPopups.Remove(popup);
        if (removed) OpenPopupsChanged?.Invoke();
    }
}
