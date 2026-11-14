using Glide.App.Services;

namespace Glide.App;

/// <summary>
/// Keeps the pointer visible while any menu / sub-menu / drop-down / flyout opened from this window is showing.
/// Popups are separate top-level windows: this window gets no pointer events while the pointer is inside one, and
/// its idle "hide cursor in fullscreen" timer used to hide the pointer over an already-open menu (and menus opened
/// while the pointer was hidden inherited the hidden cursor). See <see cref="PopupCursorGuard"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>Called once from the constructor.</summary>
    private void AttachPopupCursorGuard()
    {
        PopupCursorGuard.Register();
        void Changed() => SyncPopupCursorState();
        PopupCursorGuard.OpenPopupsChanged += Changed;
        Closed += (_, _) => PopupCursorGuard.OpenPopupsChanged -= Changed;
    }

    /// <summary>True while a non-tooltip popup owned by this window is open.</summary>
    internal bool HasOpenPopupForCursor => PopupCursorGuard.HasOpenPopup(this);

    /// <summary>
    /// Lifts idle cursor hiding while a popup is open and restarts the idle countdown once the last one closes.
    /// Also called from the idle timer so a missed open/close notification can never leave the pointer hidden over a menu.
    /// </summary>
    internal void SyncPopupCursorState()
    {
        if (_cursorAutoHide.SetPopupOpen(HasOpenPopupForCursor, DateTime.UtcNow)) ApplyFullscreenCursorVisibility();
    }
}
