using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Glide.App.Services;
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
        // handledEventsToo: the hover tip must not depend on whether an earlier window-level handler (pan, tab drag, hover reveal) marked the move handled.
        AddHandler(InputElement.PointerMovedEvent, TextOverlayDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, TextOverlayDragReleased, RoutingStrategies.Tunnel);
        // 5.1.3: right-click on the overlay opens its settings instead of the picture / title-bar context menu.
        AddHandler(InputElement.PointerReleasedEvent, TextOverlayRightReleased, RoutingStrategies.Tunnel);
        AddHandler(Control.ContextRequestedEvent, (_, e) => { if (_textOverlayRightPressed) { _textOverlayRightPressed = false; e.Handled = true; } }, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => EndTextOverlayDrag(save: true), RoutingStrategies.Bubble);
        // Round 4 B2: releasing Alt ends the "about to drag" hover that freezes the status bar auto-hide.
        AddHandler(InputElement.KeyUpEvent, (_, e) => { if ((e.KeyModifiers & KeyModifiers.Alt) == 0) _textOverlayAltHover = false; }, RoutingStrategies.Tunnel);
        // Round 4 B1: say how to move it. The hint is our own in-window label (ShowTextOverlayHint), not an Avalonia ToolTip.
        AddHandler(InputElement.PointerExitedEvent, (_, _) => UpdateTextOverlayHoverTip(false), RoutingStrategies.Direct | RoutingStrategies.Bubble);
        AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => DismissTextOverlayHint(untilPointerLeaves: false), RoutingStrategies.Tunnel, handledEventsToo: true);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsActiveProperty && e.NewValue is false) UpdateTextOverlayHoverTip(false);
        };
        CornerCounterHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.NewValue is false) HideTextOverlayHint();
        };
    }

    internal const string TextOverlayToolTip = "Alt+drag to move. Right-click for its settings.";

    // The overlay is not hit-testable (IsHitTestVisible=False keeps panning, wheel zoom and double-click working under it), so
    // Avalonia's tooltip service never sees the pointer enter it, and a ToolTip opened by hand was closed again by the service on
    // the real desktop. A label in the window's OverlayLayer (5.2.0 first attempt) passed every headless test and still never
    // showed on the real desktop (4th retest). So the hint is now the plain Border named TextOverlayHint at the end of the root grid
    // (MainWindow.axaml): topmost, never hit-testable, explicit opaque colours (no theme resource that could resolve to nothing),
    // placed by its own Margin next to CornerCounterHost. The window-level pointer handler below owns it: shown after the Main
    // interface tooltip delay, hidden when the pointer leaves the box, on a click, on a wheel tick, when the window is deactivated
    // or the box is hidden. GLIDE_TRACE_HINT=1 logs every step to startup-trace.log (UiTrace).
    private Avalonia.Threading.DispatcherTimer? _textOverlayTipTimer;

    private bool _textOverlayPointerOn;
    private bool _textOverlayHintDismissed;
    private long _textOverlayHoverSinceTick;
    private long _textOverlayHoverTraceTick;
    private bool _textOverlayHoverTraceState;

    /// <summary>True while the "Alt+drag to move" hint is on screen.</summary>
    internal bool IsTextOverlayHintShown => TextOverlayHint.IsVisible;

    private void UpdateTextOverlayHoverTip(bool onOverlay)
    {
        if (UiTrace.Hint)
        {
            // Only changes, and a heartbeat twice a second while resting, so a resting pointer does not flood the log.
            var now = Environment.TickCount64;
            if (onOverlay != _textOverlayHoverTraceState || (onOverlay && now - _textOverlayHoverTraceTick >= 500))
            {
                _textOverlayHoverTraceState = onOverlay;
                _textOverlayHoverTraceTick = now;
                UiTrace.WriteHint("hover", $"onOverlay={onOverlay} hintShown={IsTextOverlayHintShown} dismissed={_textOverlayHintDismissed} timerOn={_textOverlayTipTimer is { IsEnabled: true }} active={IsActive} host={DescribeBox(CornerCounterHost)}");
            }
        }
        _textOverlayPointerOn = onOverlay;
        if (!onOverlay)
        {
            _textOverlayHintDismissed = false;
            _textOverlayHoverSinceTick = 0;
            _textOverlayTipTimer?.Stop();
            HideTextOverlayHint();
            return;
        }
        var delay = TextOverlayTipDelay();
        if (_textOverlayHoverSinceTick == 0) _textOverlayHoverSinceTick = Environment.TickCount64;
        if (_textOverlayHintDismissed || IsTextOverlayHintShown) return;
        // A pointer that keeps moving a little over the box (a real hand) also shows it once the delay has passed, without waiting
        // for the timer: the timer is the fallback for a pointer that rests perfectly still.
        if (Environment.TickCount64 - _textOverlayHoverSinceTick >= delay.TotalMilliseconds && TextOverlayHintMayShowNow() && TryShowTextOverlayHint("move")) return;
        if (_textOverlayTipTimer is { IsEnabled: true }) return;
        StartTextOverlayTipTimer(delay, "hover");
    }

    private TimeSpan TextOverlayTipDelay() =>
        TimeSpan.FromMilliseconds(Math.Max(1, Glide.Core.Settings.TooltipAreas.DelayMs(_settings, Glide.Core.Settings.TooltipAreas.Main)));

    /// <summary>Not while dragging the box, with a modal dialog open, or in the quiet period after a wheel tick.</summary>
    private bool TextOverlayHintMayShowNow() =>
        _textOverlayDragPointer is null && !Glide.App.Services.ToolTipGuard.IsBlockedByModal(this) && !Glide.App.Services.ToolTipGuard.IsInWheelQuietPeriod(Environment.TickCount64);

    private void StartTextOverlayTipTimer(TimeSpan interval, string why)
    {
        // Normal priority: a DispatcherTimer() defaults to Background priority, which runs only when nothing else is queued, so a
        // window that keeps rendering (or any steady stream of input and layout work on the real desktop) could postpone a
        // Background tick for as long as the pointer rests.
        if (_textOverlayTipTimer is null)
        {
            _textOverlayTipTimer = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Normal);
            _textOverlayTipTimer.Tick += TextOverlayTipTimerTick;
        }
        _textOverlayTipTimer.Stop();
        _textOverlayTipTimer.Interval = interval;
        _textOverlayTipTimer.Start();
        if (UiTrace.Hint) UiTrace.WriteHint("timer_start", $"why={why} intervalMs={interval.TotalMilliseconds:0} enabled={_textOverlayTipTimer.IsEnabled} uiThread={Avalonia.Threading.Dispatcher.UIThread.CheckAccess()}");
    }

    private void TextOverlayTipTimerTick(object? sender, EventArgs e)
    {
        _textOverlayTipTimer?.Stop();
        if (UiTrace.Hint)
            UiTrace.WriteHint("tick", $"pointerOn={_textOverlayPointerOn} hostVisible={CornerCounterHost.IsVisible} dismissed={_textOverlayHintDismissed} dragging={_textOverlayDragPointer is not null} " +
                $"modal={Glide.App.Services.ToolTipGuard.IsBlockedByModal(this)} wheelQuiet={Glide.App.Services.ToolTipGuard.IsInWheelQuietPeriod(Environment.TickCount64)} active={IsActive}");
        if (!_textOverlayPointerOn || !CornerCounterHost.IsVisible || _textOverlayHintDismissed) return;
        // Still over the box, and not dragging or in the middle of something else (a menu, a dialog, the wheel): try again shortly
        // instead of giving up until the pointer leaves and comes back (the hint would otherwise never show for a pointer that rests).
        if (!TextOverlayHintMayShowNow() || !TryShowTextOverlayHint("tick")) RearmTextOverlayTip();
    }

    private void RearmTextOverlayTip()
    {
        if (_textOverlayTipTimer is null || !_textOverlayPointerOn) return;
        StartTextOverlayTipTimer(TimeSpan.FromMilliseconds(250), "rearm");
    }

    /// <summary>
    /// A click or a wheel tick over the box: the hint goes away. After a click it stays away until the pointer has left and come
    /// back (as Windows tooltips do); after a wheel tick it may return once the wheel has been quiet.
    /// </summary>
    private void DismissTextOverlayHint(bool untilPointerLeaves)
    {
        if (untilPointerLeaves) _textOverlayHintDismissed = true;
        _textOverlayTipTimer?.Stop();
        HideTextOverlayHint();
        if (!untilPointerLeaves) RearmTextOverlayTip();
    }

    private void HideTextOverlayHint()
    {
        if (!TextOverlayHint.IsVisible) return;
        TextOverlayHint.IsVisible = false;
        UiTrace.WriteHint("hidden");
    }

    private bool TryShowTextOverlayHint(string trigger)
    {
        var shown = ShowTextOverlayHint(out var reason);
        if (UiTrace.Hint)
        {
            UiTrace.WriteHint("show", $"trigger={trigger} result={shown} reason={reason}");
            if (shown) TraceTextOverlayHintGeometry("placed");
        }
        return shown;
    }

    /// <summary>Puts the hint next to the overlay box. False with a reason when it cannot (the reason is traced).</summary>
    private bool ShowTextOverlayHint(out string reason)
    {
        var hint = TextOverlayHint;
        if (!CornerCounterHost.IsVisible) { reason = "overlay_box_not_visible"; return false; }
        var root = MainRoot;
        var window = root.Bounds.Size;
        if (window.Width < 1 || window.Height < 1) { reason = "root_bounds_empty"; return false; }
        if (TextOverlayHintText.Text != TextOverlayToolTip) TextOverlayHintText.Text = TextOverlayToolTip;
        hint.IsVisible = true;
        hint.Margin = default;
        hint.Measure(window);
        var desired = hint.DesiredSize;
        if (desired.Width < 1 || desired.Height < 1) { hint.IsVisible = false; reason = "desired_size_zero"; return false; }
        if (CornerCounterHost.TranslatePoint(new Point(0, 0), root) is not { } topLeft) { hint.IsVisible = false; reason = "overlay_box_has_no_position"; return false; }
        var at = TextOverlayHintPlacement.Place(new Rect(topLeft, CornerCounterHost.Bounds.Size), desired, window);
        hint.Margin = new Thickness(at.X, at.Y, 0, 0);
        hint.InvalidateArrange();
        reason = "shown";
        return true;
    }

    /// <summary>Everything that decides whether the hint is seen: layer, size, position, visibility, opacity, brush, parents.</summary>
    internal string DescribeTextOverlayHint()
    {
        var hint = TextOverlayHint;
        var opacity = 1.0;
        for (Visual? v = hint; v is not null; v = v.GetVisualParent()) opacity *= v.Opacity;
        var chain = new List<string>();
        for (Visual? v = hint.GetVisualParent(); v is not null && chain.Count < 6; v = v.GetVisualParent())
            chain.Add(v.GetType().Name + (v is StyledElement { Name: { Length: > 0 } n } ? "#" + n : "") + (v.IsVisible ? "" : "(hidden)"));
        static string BrushText(IBrush? brush) => brush switch { ISolidColorBrush solid => solid.Color + "@" + solid.Opacity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), null => "null", _ => brush.GetType().Name };
        var inRoot = hint.TranslatePoint(new Point(0, 0), MainRoot) is { } p ? new Rect(p, hint.Bounds.Size).ToString() : "none";
        return $"layer=MainRoot{MainRoot.Bounds.Size} desired={hint.DesiredSize} margin={hint.Margin} bounds={hint.Bounds} inRoot={inRoot} " +
               $"isVisible={hint.IsVisible} effectivelyVisible={hint.IsEffectivelyVisible} effectiveOpacity={opacity:0.##} zIndex={hint.ZIndex} " +
               $"background={BrushText(hint.Background)} foreground={BrushText(TextOverlayHintText.Foreground)} parents={string.Join(" < ", chain)}";
    }

    private static string DescribeBox(Control control) =>
        $"{control.Bounds.Size} visible={control.IsVisible} opacity={control.Opacity:0.##} parent={control.Parent?.GetType().Name}";

    private void TraceTextOverlayHintGeometry(string stage)
    {
        UiTrace.WriteHint(stage, DescribeTextOverlayHint());
        // Once layout has run: did it get a size and a place on screen?
        Avalonia.Threading.Dispatcher.UIThread.Post(() => UiTrace.WriteHint(stage + "_after_layout", DescribeTextOverlayHint()), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private bool _textOverlayAltHover;

    /// <summary>
    /// Round 4 B2: while Alt is held over the text overlay or it is being dragged, the auto-hidden status bar neither shows nor
    /// hides (a bottom overlay sits above the bar and would jump away from the pointer as the bar toggles).
    /// </summary>
    internal bool StatusAutoHideFrozen => _textOverlayDragPointer is not null || _textOverlayAltHover;

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
        if (_textOverlayPointerOn || IsTextOverlayHintShown) DismissTextOverlayHint(untilPointerLeaves: true);
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
        if (_textOverlayDragPointer is null)
        {
            var onOverlay = IsPointerOnTextOverlay(e);
            _textOverlayAltHover = (e.KeyModifiers & KeyModifiers.Alt) != 0 && onOverlay;
            UpdateTextOverlayHoverTip(onOverlay);
            return;
        }
        if (!ReferenceEquals(e.Pointer, _textOverlayDragPointer)) return;
        var delta = e.GetPosition(this) - _textOverlayDragStart;
        // Round 4 B3: the overlay never leaves the picture area while dragged (the translation is limited so its box stays
        // inside; over a bar it starts at offset 0 from wherever it sits, so the limit is taken from its picture-area origin).
        var dx = delta.X; var dy = delta.Y;
        if (_textOverlayDragOrigin is { } o)
        {
            var maxX = Math.Max(0, ImageView.Bounds.Width - CornerCounterHost.Bounds.Width - TextOverlayClampInset);
            var maxY = Math.Max(0, ImageView.Bounds.Height - CornerCounterHost.Bounds.Height - TextOverlayClampInset);
            dx = Math.Clamp(dx, Math.Min(0, TextOverlayClampInset - o.X), Math.Max(0, maxX - o.X));
            dy = Math.Clamp(dy, Math.Min(0, TextOverlayClampInset - o.Y), Math.Max(0, maxY - o.Y));
        }
        _textOverlayDragOffset = (_textOverlayDragStartOffset.X + (int)Math.Round(dx), _textOverlayDragStartOffset.Y + (int)Math.Round(dy));
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
        _textOverlayAltHover = false;
        if (!save || _textOverlayDragOffset == _textOverlayDragStartOffset || _textOverlayDragOrigin is not { } origin)
            return;
        var dropX = origin.X + _textOverlayDragOffset.X - _textOverlayDragStartOffset.X;
        var dropY = origin.Y + _textOverlayDragOffset.Y - _textOverlayDragStartOffset.Y;
        var (anchor, x, y) = TextOverlayLayout.CustomFromDrop(ImageView.Bounds.Width, ImageView.Bounds.Height,
            CornerCounterHost.Bounds.Width, CornerCounterHost.Bounds.Height, dropX, dropY,
            TextOverlayPictureSideGap, TextOverlayPictureTopGap, TextOverlayBottomInset(), TextOverlayClampInset);
        TextOverlayPlacements.SetCustom(_settings, SideTabsActive, anchor, x, y);
        SaveSettingsAndPublish();
    }
}

/// <summary>Where the "Alt+drag to move" hint sits next to the text overlay (pure, so it is testable without a window).</summary>
internal static class TextOverlayHintPlacement
{
    /// <summary>Gap between the overlay box and the hint, and the minimum distance from the window edge (DIPs).</summary>
    internal const double Gap = 6;

    /// <summary>
    /// Below the box (above it when there is no room underneath), aligned with the box's edge that is nearer the window edge, so
    /// a box in the top-right corner gets a hint that ends at its right edge. Always fully inside <paramref name="bounds"/>.
    /// </summary>
    internal static Point Place(Rect host, Size tip, Size bounds)
    {
        var y = host.Bottom + Gap;
        if (y + tip.Height > bounds.Height - Gap) y = host.Top - Gap - tip.Height;
        y = Math.Clamp(y, Math.Min(Gap, Math.Max(0, bounds.Height - tip.Height)), Math.Max(Math.Min(Gap, Math.Max(0, bounds.Height - tip.Height)), bounds.Height - tip.Height - Gap));
        var x = host.Center.X > bounds.Width / 2 ? host.Right - tip.Width : host.Left;
        x = Math.Clamp(x, Math.Min(Gap, Math.Max(0, bounds.Width - tip.Width)), Math.Max(Math.Min(Gap, Math.Max(0, bounds.Width - tip.Width)), bounds.Width - tip.Width - Gap));
        return new Point(x, y);
    }
}
