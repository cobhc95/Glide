using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Glide.Diagnostics.Runtime;

namespace Glide.App;

/// <summary>
/// Opt-in input trace for the title/tab bar (top-edge click reports). Enabled only when the live diagnostic trace is
/// on (Glide.exe --diagnostic-trace, or GLIDE_DIAGNOSTIC_TRACE=1); otherwise no handler is even attached, so normal
/// runs pay nothing. Together with win32.nchittest_top (WindowsBrowserFrameController) one trace shows, for a press on
/// the top pixels: what Win32 answered for the row, which visual Avalonia routed the press to, whether a title-bar
/// drag claimed it, where the release landed relative to the "+" and whether Click fired.
/// Output: %USERPROFILE%\Downloads\Glide-Diagnostic-Latest.txt (or GLIDE_DIAGNOSTIC_TRACE_DIR).
/// </summary>
public partial class MainWindow
{
    private void WireTitleBarInputTrace()
    {
        if (!LiveDiagnosticTrace.IsEnabled) return;
        const RoutingStrategies Both = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;

        ChromeBorder.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Route == RoutingStrategies.Bubble) TraceTitleBarPointer("chrome_pressed_bubble", e, e.Handled);
            else TraceTitleBarPointer("chrome_pressed_tunnel", e, e.Handled);
        }, Both, handledEventsToo: true);
        ChromeBorder.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.Route == RoutingStrategies.Tunnel) TraceTitleBarPointer("chrome_released", e, e.Handled);
        }, Both, handledEventsToo: true);
        ChromeBorder.AddHandler(InputElement.PointerCaptureLostEvent, (_, e) =>
            LiveDiagnosticTrace.Write("titlebar", "chrome_capture_lost", new { source = DescribeVisual(e.Source as Visual) }),
            RoutingStrategies.Bubble, handledEventsToo: true);

        NewTabButton.PointerEntered += (_, e) => TraceTitleBarPointer("plus_entered", e, e.Handled);
        NewTabButton.PointerExited += (_, e) => TraceTitleBarPointer("plus_exited", e, e.Handled);
        NewTabButton.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) =>
            LiveDiagnosticTrace.Write("titlebar", "plus_capture_lost", new { pressed = NewTabButton.IsPressed }),
            RoutingStrategies.Bubble, handledEventsToo: true);
        NewTabButton.Click += (_, _) => LiveDiagnosticTrace.Write("titlebar", "plus_click", new { state = WindowState.ToString() });
    }

    private void TraceTitleBarPointer(string name, PointerEventArgs e, bool handled)
    {
        try
        {
            var window = e.GetPosition(this);
            var plus = e.GetPosition(NewTabButton);
            var plusSize = NewTabButton.Bounds.Size;
            LiveDiagnosticTrace.Write("titlebar", name, new
            {
                x = Math.Round(window.X, 2),
                y = Math.Round(window.Y, 2),
                physicalY = Math.Round(window.Y * RenderScaling, 2),
                plusX = Math.Round(plus.X, 2),
                plusY = Math.Round(plus.Y, 2),
                overPlus = plus.X >= 0 && plus.Y >= 0 && plus.X <= plusSize.Width && plus.Y <= plusSize.Height,
                plusPressed = NewTabButton.IsPressed,
                plusPointerOver = NewTabButton.IsPointerOver,
                plusTransform = NewTabButton.RenderTransform?.Value.ToString(),
                handled,
                source = DescribeVisual(e.Source as Visual),
                captured = DescribeVisual(e.Pointer.Captured as Visual),
                interactive = IsInteractiveSource(e.Source),
                state = WindowState.ToString(),
                scaling = RenderScaling
            });
        }
        catch { /* tracing must never disturb input */ }
    }

    private static string? DescribeVisual(Visual? visual)
    {
        if (visual is null) return null;
        var parts = new List<string>(3);
        for (var current = visual; current is not null && parts.Count < 3; current = current.GetVisualParent())
        {
            var name = current is StyledElement { Name: { Length: > 0 } n } ? "#" + n : "";
            var classes = current is StyledElement s && s.Classes.Count > 0 ? "." + string.Join(".", s.Classes) : "";
            parts.Add(current.GetType().Name + name + classes);
        }
        return string.Join(" < ", parts);
    }
}
