using Avalonia;
using Avalonia.Controls;

namespace Glide.App.Services;

/// <summary>Framework-light fullscreen snapshot/restore state; window chrome remains a UI concern.</summary>
public sealed class FullscreenSessionController
{
    private WindowState _previousState = WindowState.Normal;
    private PixelPoint _previousPosition;
    private double _previousWidth;
    private double _previousHeight;

    public void Capture(WindowState state, PixelPoint position, double width, double height)
    {
        _previousState = state; _previousPosition = position; _previousWidth = width; _previousHeight = height;
    }

    public (WindowState State, PixelPoint Position, double Width, double Height) Restore() =>
        (_previousState, _previousPosition, _previousWidth, _previousHeight);
}
