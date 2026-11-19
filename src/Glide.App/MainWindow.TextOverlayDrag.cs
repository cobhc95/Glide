using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Glide.Core.Settings;

namespace Glide.App;

/// <summary>
/// Alt+drag the text overlay to move it (5.1.2). The offset is remembered per position anchor
/// (<see cref="TextOverlayOffsets"/>) and is the same value as the "Text overlay horizontal / vertical offset" settings.
/// Alt+drag only starts on the overlay's own box, so every other Alt+drag keeps its meaning.
/// </summary>
public partial class MainWindow
{
    private IPointer? _textOverlayDragPointer;
    private Point _textOverlayDragStart;
    private (int X, int Y) _textOverlayDragStartOffset;
    private (int X, int Y) _textOverlayDragOffset;

    /// <summary>Called once from the constructor.</summary>
    private void AttachTextOverlayDrag()
    {
        AddHandler(InputElement.PointerPressedEvent, TextOverlayDragPressed, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerMovedEvent, TextOverlayDragMoved, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerReleasedEvent, TextOverlayDragReleased, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => EndTextOverlayDrag(save: true), RoutingStrategies.Bubble);
    }

    private bool IsPointerOnTextOverlay(PointerEventArgs e)
    {
        if (CornerCounterHost is not { IsVisible: true } host || !ImageView.IsVisible || _wholeAppOverlayMode) return false;
        var p = e.GetPosition(host);
        return p.X >= 0 && p.Y >= 0 && p.X <= host.Bounds.Width && p.Y <= host.Bounds.Height;
    }

    private void TextOverlayDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Alt) == 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsPointerOnTextOverlay(e)) return;
        _textOverlayDragPointer = e.Pointer;
        _textOverlayDragStart = e.GetPosition(this);
        _textOverlayDragStartOffset = _textOverlayDragOffset = OverlayOptionsView.PictureCounterOffset;
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
        if (!save || _textOverlayDragOffset == _textOverlayDragStartOffset) return;
        _settings.PictureCounterOffsets = TextOverlayOffsets.Set(_settings.PictureCounterOffsets, _settings.PictureCounterPosition,
            _textOverlayDragOffset.X, _textOverlayDragOffset.Y);
        SaveSettingsAndPublish();
    }
}
