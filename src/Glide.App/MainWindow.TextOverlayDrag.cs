using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Glide.Core.Settings;

namespace Glide.App;

/// <summary>
/// Alt+drag the text overlay to move it (5.1.2). Since 5.1.4 a drop always switches the card in use to "Custom" over the
/// picture (also when dragged out of the title bar or the vertical tab bar): the nearest picture anchor plus an offset
/// (<see cref="TextOverlayLayout.CustomFromDrop"/>), the same offset as the "Text overlay horizontal / vertical offset" settings.
/// Alt+drag only starts on the overlay's own box, so every other Alt+drag keeps its meaning.
/// </summary>
public partial class MainWindow
{
    private IPointer? _textOverlayDragPointer;
    private Point _textOverlayDragStart;
    private (int X, int Y) _textOverlayDragStartOffset;
    private (int X, int Y) _textOverlayDragOffset;
    private Point? _textOverlayDragOrigin;   // overlay top-left in picture-area coordinates at press

    /// <summary>Called once from the constructor.</summary>
    private void AttachTextOverlayDrag()
    {
        AddHandler(InputElement.PointerPressedEvent, TextOverlayDragPressed, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerMovedEvent, TextOverlayDragMoved, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerReleasedEvent, TextOverlayDragReleased, RoutingStrategies.Tunnel);
        // 5.1.3: right-click on the overlay opens its settings instead of the picture / title-bar context menu.
        AddHandler(InputElement.PointerReleasedEvent, TextOverlayRightReleased, RoutingStrategies.Tunnel);
        AddHandler(Control.ContextRequestedEvent, (_, e) => { if (_textOverlayRightPressed) { _textOverlayRightPressed = false; e.Handled = true; } }, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => EndTextOverlayDrag(save: true), RoutingStrategies.Bubble);
    }

    private bool IsPointerOnTextOverlay(PointerEventArgs e)
    {
        if (CornerCounterHost is not { IsVisible: true } host || host.Opacity <= 0 || !ImageView.IsVisible || _wholeAppOverlayMode) return false;
        var p = e.GetPosition(host);
        return p.X >= 0 && p.Y >= 0 && p.X <= host.Bounds.Width && p.Y <= host.Bounds.Height;
    }

    private bool _textOverlayRightPressed;

    /// <summary>Setting id of the "Text overlay placement" card for the current tab layout (the "In use now" one).</summary>
    internal static string TextOverlayPlacementSettingId(bool sideTabs) =>
        sideTabs ? "overlay.picturePositionSideTabs" : "overlay.picturePositionTopTabs";

    /// <summary>Opens Settings at the text overlay placement card for the current tab layout.</summary>
    internal void OpenTextOverlaySettings() => OpenSettingsAtSetting(TextOverlayPlacementSettingId(SideTabsActive));

    private void TextOverlayRightReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_textOverlayRightPressed || e.InitialPressMouseButton != MouseButton.Right) return;
        e.Handled = true;
        // ContextRequested may follow this release; the flag swallows it and is cleared shortly after.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _textOverlayRightPressed = false, Avalonia.Threading.DispatcherPriority.Background);
        if (IsPointerOnTextOverlay(e)) OpenTextOverlaySettings();
    }

    private void TextOverlayDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && IsPointerOnTextOverlay(e))
        {
            _textOverlayRightPressed = true;
            e.Handled = true;
            return;
        }
        if ((e.KeyModifiers & KeyModifiers.Alt) == 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsPointerOnTextOverlay(e)) return;
        _textOverlayDragPointer = e.Pointer;
        _textOverlayDragStart = e.GetPosition(this);
        _textOverlayDragStartOffset = _textOverlayDragOffset = ReferenceEquals(CornerCounterHost.Parent, ImageView)
            ? OverlayOptionsView.PictureCounterOffset(SideTabsActive) : (0, 0);
        _textOverlayDragOrigin = CornerCounterHost.TranslatePoint(new Point(0, 0), ImageView);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void TextOverlayDragMoved(object? sender, PointerEventArgs e)
    {
        if (_textOverlayDragPointer is null || !ReferenceEquals(e.Pointer, _textOverlayDragPointer)) return;
        var delta = e.GetPosition(this) - _textOverlayDragStart;
        _textOverlayDragOffset = (
            Math.Clamp(_textOverlayDragStartOffset.X + (int)Math.Round(delta.X), -TextOverlayOffsets.Limit, TextOverlayOffsets.Limit),
            Math.Clamp(_textOverlayDragStartOffset.Y + (int)Math.Round(delta.Y), -TextOverlayOffsets.Limit, TextOverlayOffsets.Limit));
        CornerCounterHost.RenderTransform = new TranslateTransform(_textOverlayDragOffset.X, _textOverlayDragOffset.Y);
        e.Handled = true;
    }

    private void TextOverlayDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_textOverlayDragPointer is null || !ReferenceEquals(e.Pointer, _textOverlayDragPointer)) return;
        EndTextOverlayDrag(save: true);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void EndTextOverlayDrag(bool save)
    {
        if (_textOverlayDragPointer is null) return;
        _textOverlayDragPointer = null;
        if (!save || _textOverlayDragOffset == _textOverlayDragStartOffset || _textOverlayDragOrigin is not { } origin)
            return;
        var dropX = origin.X + _textOverlayDragOffset.X - _textOverlayDragStartOffset.X;
        var dropY = origin.Y + _textOverlayDragOffset.Y - _textOverlayDragStartOffset.Y;
        var (anchor, x, y) = TextOverlayLayout.CustomFromDrop(ImageView.Bounds.Width, ImageView.Bounds.Height,
            CornerCounterHost.Bounds.Width, CornerCounterHost.Bounds.Height, dropX, dropY,
            TextOverlayPictureSideGap, TextOverlayPictureTopGap, TextOverlayBottomInset());
        TextOverlayPlacements.SetCustom(_settings, SideTabsActive, anchor, x, y);
        SaveSettingsAndPublish();
    }
}
