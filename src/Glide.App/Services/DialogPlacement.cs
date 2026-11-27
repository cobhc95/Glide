using Avalonia;
using Avalonia.Controls;
using Glide.Core.Windows;

namespace Glide.App.Services;

/// <summary>
/// The one rule for where a Glide dialog opens: centred on its owner (the window that showed it), on the owner's
/// monitor, DPI-aware, and kept wholly inside that monitor's working area. Dialogs never remember a position (a saved
/// position used to override the centring: the 5.0.6 "Unsaved settings changes opens at the top-left" report).
/// <see cref="WindowPlacementService.Track"/> applies this to every position-only dialog; nothing else needs to call it.
/// </summary>
internal static class DialogPlacement
{
    /// <summary>
    /// Opens <paramref name="dialog"/> centred on its owner: Avalonia's CenterOwner places it before it is shown, then,
    /// once it has its real frame size (content-sized dialogs, an owner on a monitor with a different DPI), it is
    /// re-centred and clamped to the owner's working area.
    /// </summary>
    public static void CenterOnOwner(Window dialog)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        EventHandler? opened = null;
        opened = (_, _) =>
        {
            dialog.Opened -= opened;
            Recenter(dialog);
            // The real frame size can settle a layout pass later (DPI change, resize above): fit once more.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Recenter(dialog), Avalonia.Threading.DispatcherPriority.Loaded);
        };
        dialog.Opened += opened;
    }

    /// <summary>Moves <paramref name="dialog"/> to the centre of its owner (no-op without a visible, non-minimized owner).</summary>
    public static void Recenter(Window dialog)
    {
        try
        {
            if (dialog.Owner is not Window owner || !owner.IsVisible || owner.WindowState == WindowState.Minimized) return;
            var ownerSize = owner.FrameSize ?? owner.ClientSize;
            var dialogSize = dialog.FrameSize ?? dialog.ClientSize;
            if (ownerSize.Width <= 0 || ownerSize.Height <= 0 || dialogSize.Width <= 0 || dialogSize.Height <= 0) return;
            var ownerScale = Scale(owner.DesktopScaling);
            var ownerPx = new PixelSize((int)Math.Round(ownerSize.Width * ownerScale), (int)Math.Round(ownerSize.Height * ownerScale));
            var screen = ScreenOf(owner, ownerPx);
            // The dialog lands on the owner's monitor, so it is measured at that monitor's DPI.
            var scale = screen is { } s && s.Scale > 0 ? s.Scale : ownerScale;
            var dialogPx = new PixelSize((int)Math.Round(dialogSize.Width * scale), (int)Math.Round(dialogSize.Height * scale));
            // Title-bar allowance: Position may be the client origin, so keep the frame's top edge on screen too.
            var chromeH = Math.Max(0, dialogSize.Height - dialog.ClientSize.Height);
            var chromeW = Math.Max(0, dialogSize.Width - dialog.ClientSize.Width);
            var topInset = (int)Math.Ceiling(chromeH * scale);
            var minW = (int)Math.Ceiling((Math.Max(0, dialog.MinWidth) + chromeW) * scale);
            var minH = (int)Math.Ceiling((Math.Max(0, dialog.MinHeight) + chromeH) * scale);
            var (x, y, w, h) = WindowPlacementResolver.FitDialog(owner.Position.X, owner.Position.Y, ownerPx.Width, ownerPx.Height, dialogPx.Width, dialogPx.Height, minW, minH, topInset, screen);
            if (dialog.CanResize && (w < dialogPx.Width || h < dialogPx.Height))
            {
                if (w < dialogPx.Width) dialog.Width = Math.Max(1, w / scale - chromeW);
                if (h < dialogPx.Height) dialog.Height = Math.Max(1, h / scale - chromeH);
            }
            var target = new PixelPoint(x, y);
            if (dialog.Position != target) dialog.Position = target;
        }
        catch
        {
            // Screen metrics can be unavailable during display changes; Avalonia's own CenterOwner placement stands.
        }
    }

    private static double Scale(double value) => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 1;

    /// <summary>The working area of the monitor holding the owner's centre (else the one it overlaps most, else the primary).</summary>
    private static ScreenWorkArea? ScreenOf(Window owner, PixelSize ownerPx)
    {
        var (screens, primary) = WindowPlacementService.ScreensOf(owner);
        var cx = owner.Position.X + ownerPx.Width / 2;
        var cy = owner.Position.Y + ownerPx.Height / 2;
        ScreenWorkArea? best = null;
        long bestArea = 0;
        foreach (var screen in screens)
        {
            if (screen.IsEmpty) continue;
            if (cx >= screen.X && cx < screen.Right && cy >= screen.Y && cy < screen.Bottom) return screen;
            var w = Math.Min((long)owner.Position.X + ownerPx.Width, screen.Right) - Math.Max(owner.Position.X, screen.X);
            var h = Math.Min((long)owner.Position.Y + ownerPx.Height, screen.Bottom) - Math.Max(owner.Position.Y, screen.Y);
            if (w > 0 && h > 0 && w * h > bestArea) { bestArea = w * h; best = screen; }
        }
        return best ?? (primary.IsEmpty ? null : primary);
    }
}
