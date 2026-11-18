using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Glide.App.Services;

/// <summary>
/// Highlights the sub-menu item that is already under the pointer when a sub-menu opens (round 3 #24).
///
/// A sub-menu is its own popup window. When it opens beneath a pointer that is not moving (it opened on a click, or
/// flipped to the other side near a screen edge), Windows sends the new popup no mouse-move, so Avalonia raises no
/// pointer-enter and the item under the pointer stays unhighlighted until the mouse moves. After the popup is laid
/// out this selects the item under the cursor, exactly as a pointer-enter would.
/// </summary>
internal static class SubmenuHoverSync
{
    /// <summary>Screen position of the pointer; replaceable by tests.</summary>
    internal static Func<PixelPoint?> CursorPosition { get; set; } = ReadCursor;

    internal static void Attach(ContextMenu menu) =>
        menu.AddHandler(MenuItem.SubmenuOpenedEvent, OnSubmenuOpened, RoutingStrategies.Bubble, handledEventsToo: true);

    private static void OnSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem parent) return;
        // The popup content is measured/arranged on the next layout pass.
        Dispatcher.UIThread.Post(() => SelectItemUnderCursor(parent), DispatcherPriority.Loaded);
    }

    /// <summary>Selects the direct child of <paramref name="parent"/> under the cursor, if any. Returns it.</summary>
    internal static MenuItem? SelectItemUnderCursor(MenuItem parent)
    {
        if (!parent.IsSubMenuOpen || CursorPosition() is not { } cursor) return null;
        foreach (var child in parent.Items.OfType<MenuItem>())
        {
            if (!child.IsVisible || !child.IsEffectivelyEnabled || child.Bounds.Width <= 0 || child.Bounds.Height <= 0) continue;
            if (TopLevel.GetTopLevel(child) is null) continue;
            var topLeft = child.PointToScreen(new Point(0, 0));
            var bottomRight = child.PointToScreen(new Point(child.Bounds.Width, child.Bounds.Height));
            if (cursor.X < topLeft.X || cursor.X >= bottomRight.X || cursor.Y < topLeft.Y || cursor.Y >= bottomRight.Y) continue;
            if (child.IsPointerOver || child.IsSelected) return child;
            child.IsSelected = true;
            return child;
        }
        return null;
    }

    private static PixelPoint? ReadCursor()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return Platform.Native.User32.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null; }
        catch { return null; } // no cursor (session locked, remote desktop edge cases): nothing to sync
    }
}
