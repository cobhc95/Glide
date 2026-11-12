using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Diagnostics;
using Glide.App.Platform;
using Glide.App.Printing;
using Glide.App.Settings;
using Glide.App.Services;
using Glide.Core;
using Glide.Core.Commands;
using Glide.Core.Workspace;
using Glide.Imaging;
using Glide.Input;
using Glide.Diagnostics.Runtime;
using Microsoft.VisualBasic.FileIO;

namespace Glide.App;

/// <summary>Slideshow sessions and tab close/restore/clone helpers. Split from MainWindow (4.5.2).</summary>
public partial class MainWindow
{
    private Task ToggleSlideshowAsync() => EnsureSlideshow().ToggleAsync();

    private async Task<bool> ConfirmSlideshowSessionOptionsAsync()
    {
        var interval = new NumericUpDown { Minimum = 250, Maximum = 600000, Increment = 250, Value = _settings.SlideshowIntervalMs, MinWidth = 150 };
        var loop = new CheckBox { Content = "Loop slideshow", IsChecked = _settings.SlideshowLoop };
        var cross = new CheckBox { Content = "Continue across sibling folders", IsChecked = _settings.SlideshowCrossFolders };
        var shuffle = new CheckBox { Content = "Shuffle images", IsChecked = _settings.SlideshowShuffle };
        var direction = new ComboBox
        {
            ItemsSource = new[] { "Forward", "Backward" },
            SelectedItem = string.Equals(_settings.SlideshowDirection, "Backward", StringComparison.OrdinalIgnoreCase) ? "Backward" : "Forward",
            MinWidth = 150
        };
        var startFullscreen = new CheckBox { Content = "Start slideshow in fullscreen", IsChecked = _settings.SlideshowStartFullscreen };
        var pauseInactive = new CheckBox { Content = "Pause while Glide is not the active window", IsChecked = _settings.SlideshowPauseWhenInactive };
        var rightClickStops = new CheckBox { Content = "Right-click stops an active slideshow", IsChecked = _settings.SlideshowRightClickStops };
        var leftClick = new ComboBox
        {
            ItemsSource = Glide.Core.Settings.SlideshowLeftClickActions.All,
            SelectedItem = Glide.Core.Settings.SlideshowLeftClickActions.Normalize(_settings.SlideshowLeftClickAction),
            MinWidth = 190
        };
        var qualityIndicator = new CheckBox { Content = "Show slideshow playing indicator", IsChecked = _settings.SlideshowShowQualityIndicator };
        var start = new Button { Content = "Start slideshow", MinWidth = 130 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(start);
        var root = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        root.Children.Add(new TextBlock { Text = "Slideshow options", FontSize = 21, FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "Choose this slideshow session before it starts.",
            Foreground = new SolidColorBrush(Color.Parse(IsDarkTheme() ? "#AAB5C2" : "#56616B"))
        });
        var intervalRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
        intervalRow.Children.Add(new TextBlock { Text = "Interval (ms)", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, MinWidth = 170 });
        intervalRow.Children.Add(interval);
        root.Children.Add(intervalRow);
        var directionRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
        directionRow.Children.Add(new TextBlock { Text = "Direction", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, MinWidth = 170 });
        directionRow.Children.Add(direction);
        root.Children.Add(directionRow);
        root.Children.Add(loop);
        root.Children.Add(cross);
        root.Children.Add(shuffle);
        root.Children.Add(startFullscreen);
        root.Children.Add(pauseInactive);
        root.Children.Add(rightClickStops);
        var leftClickRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
        leftClickRow.Children.Add(new TextBlock { Text = "Left-click does", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, MinWidth = 170 });
        leftClickRow.Children.Add(leftClick);
        root.Children.Add(leftClickRow);
        root.Children.Add(qualityIndicator);
        root.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Start slideshow", Icon = Icon, Width = 470, SizeToContent = SizeToContent.Height, MinWidth = 420,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root,
            Topmost = Topmost
        };
        PopupPlacementStore.Track(dialog, "slideshow-start");
        start.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            dialog.Close(true);
            e.Handled = true;
        };
        _overlays?.SetModalSuppressed(true);
        bool accepted;
        try
        {
            accepted = await ShowOwnedDialogAsync<bool>(dialog);
        }
        finally
        {
            _overlays?.SetModalSuppressed(false);
        }
        if (!accepted) return false;
        _settings.SlideshowIntervalMs = (int)(interval.Value ?? 3000);
        _settings.SlideshowLoop = loop.IsChecked == true;
        _settings.SlideshowCrossFolders = cross.IsChecked == true;
        _settings.SlideshowShuffle = shuffle.IsChecked == true;
        _settings.SlideshowDirection = direction.SelectedItem?.ToString() == "Backward" ? "Backward" : "Forward";
        _settings.SlideshowStartFullscreen = startFullscreen.IsChecked == true;
        _settings.SlideshowPauseWhenInactive = pauseInactive.IsChecked == true;
        _settings.SlideshowRightClickStops = rightClickStops.IsChecked == true;
        _settings.SlideshowLeftClickAction = Glide.Core.Settings.SlideshowLeftClickActions.Normalize(leftClick.SelectedItem?.ToString());
        _settings.SlideshowShowQualityIndicator = qualityIndicator.IsChecked == true;
        SaveSettingsAndPublish();
        return true;
    }
    private void StopSlideshow(bool restoreStartingMode) => _slideshow?.Stop(restoreStartingMode);

    /// <summary>Runs the user-chosen action for a left-click on the picture during a slideshow.</summary>
    private async Task HandleSlideshowLeftClickAsync()
    {
        if (_slideshow?.IsActive != true) return;
        switch (Glide.Core.Settings.SlideshowLeftClickActions.Normalize(_settings.SlideshowLeftClickAction))
        {
            case Glide.Core.Settings.SlideshowLeftClickActions.NextImage: await ExecuteCommandAsync(GlideCommand.NextImage); break;
            case Glide.Core.Settings.SlideshowLeftClickActions.PreviousImage: await ExecuteCommandAsync(GlideCommand.PreviousImage); break;
            case Glide.Core.Settings.SlideshowLeftClickActions.StopSlideshow: StopSlideshow(restoreStartingMode: true); break;
            case Glide.Core.Settings.SlideshowLeftClickActions.DoNothing: break;
            default: await ToggleSlideshowAsync(); break;
        }
    }

    private void UpdateSlideshowUi()
    {
        var slideshow = _slideshow;
        var active = slideshow?.IsActive == true;
        var running = slideshow?.IsRunning == true;
        if (Viewport is not null)
            Viewport.SlideshowRightClickStopEnabled = running && _settings.SlideshowRightClickStops;
        if (Viewport is not null)
            Viewport.SlideshowLeftClickEnabled = active &&
                !string.Equals(Glide.Core.Settings.SlideshowLeftClickActions.Normalize(_settings.SlideshowLeftClickAction), Glide.Core.Settings.SlideshowLeftClickActions.DoNothing, StringComparison.Ordinal);
        if (SlideshowQualityIndicator is not null)
            SlideshowQualityIndicator.IsVisible = running && _settings.SlideshowShowQualityIndicator;
        if (SlideshowIcon is not null) SlideshowIcon.Kind = running ? "Pause" : "Play";
        if (SlideshowStopButton is not null) SlideshowStopButton.IsVisible = active;
        if (SlideshowButton is not null)
        {
            // The status-bar slideshow control itself is the authoritative playback indicator.
            // A separate badge can be clipped/disabled by layout or persisted settings, so never
            // rely on it as the only indication that playback is actually running.
            SlideshowButton.Classes.Remove("slideshowPlaying");
            SlideshowButton.Classes.Remove("slideshowPaused");
            if (running) SlideshowButton.Classes.Add("slideshowPlaying");
            else if (active) SlideshowButton.Classes.Add("slideshowPaused");

            var verb = !active ? "Start slideshow" : running ? "Pause slideshow" : "Resume slideshow";
            ToolTip.SetTip(SlideshowButton, $"[{verb} ({ShortcutFor(GlideCommand.StartPauseSlideshow)})]");
            ToolTip.SetShowDelay(SlideshowButton, 650);
        }
        if (SlideshowStopButton is not null)
        {
            ToolTip.SetTip(SlideshowStopButton, $"[Stop slideshow session ({ShortcutFor(GlideCommand.StopSlideshow)})]");
            ToolTip.SetShowDelay(SlideshowStopButton, 650);
        }
    }

    private async void SlideshowStopClicked(object? sender, RoutedEventArgs e)
    {
        StopSlideshow(restoreStartingMode: true);
        await Task.CompletedTask;
    }

    private void ToggleFullscreen()
    {
        if (_wholeAppOverlayMode)
        {
            // Fullscreen and whole-app Overlay are mutually exclusive presentation shells. F11 from
            // Overlay mode returns to the preserved normal shell and then enters fullscreen.
            ExitWholeAppOverlayMode();
            if (WindowState != WindowState.FullScreen) EnterFullscreen();
            return;
        }
        if (WindowState == WindowState.FullScreen) ExitFullscreen(forceMaximized: false);
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (WindowState == WindowState.FullScreen) return;
        CancelSelectionForFullscreenTransition();
        var priorState = WindowState;
        var priorPosition = Position;
        var priorWidth = Width;
        var priorHeight = Height;
        // Remember the true normal bounds. From Normal the live bounds are the normal bounds; from
        // Maximized the live Width/Height/Position are the maximized geometry, so use the last tracked
        // normal placement instead.
        if (priorState == WindowState.Normal) TrackNormalWindowPlacement();
        _fullscreenNormalKnown = _normalPlacementKnown && _lastNormalWindowSize.Width >= 100 && _lastNormalWindowSize.Height >= 100;
        _fullscreenNormalPosition = _lastNormalWindowPosition;
        _fullscreenNormalSize = _lastNormalWindowSize;
        _fullscreen.Capture(priorState, priorPosition, priorWidth, priorHeight);
        WindowState = WindowState.FullScreen;
        ApplyChromeLayoutForWindowState();
        _diagnostics.Write("fullscreen", "entered", new { previousState = priorState, previousPosition = priorPosition, previousWidth = priorWidth, previousHeight = priorHeight });
        ResetFullscreenCursorTimer();
        FinishFullscreenTransition();
    }

    private void ExitFullscreen(bool forceMaximized)
    {
        if (WindowState != WindowState.FullScreen) return;
        CancelSelectionForFullscreenTransition();
        StopFullscreenCursorAutoHide();
        WindowState = WindowState.Normal;
        var snapshot = _fullscreen.Restore();
        var exitBehavior = forceMaximized ? "Maximized" : (_settings.FullscreenExitBehavior ?? "Restore size and location");
        // Whatever the exit behaviour is, put the ordinary window back to its real normal bounds first.
        // Going straight to Maximized from here used to leave the fullscreen/maximized geometry as the
        // "restored" rectangle, so un-maximizing later produced a screen-sized window.
        ReapplyNormalBoundsAfterFullscreen();
        if (string.Equals(exitBehavior, "Restore size and location", StringComparison.OrdinalIgnoreCase))
        {
            if (!_fullscreenNormalKnown && snapshot.State == WindowState.Normal && snapshot.Width > 0 && snapshot.Height > 0)
            {
                Width = snapshot.Width;
                Height = snapshot.Height;
                Position = snapshot.Position;
            }
            if (snapshot.State == WindowState.Maximized) WindowState = WindowState.Maximized;
            else if (snapshot.State == WindowState.Minimized) WindowState = WindowState.Minimized;
        }
        else if (string.Equals(exitBehavior, "Minimized", StringComparison.OrdinalIgnoreCase))
        {
            WindowState = WindowState.Minimized;
        }
        else
        {
            WindowState = WindowState.Maximized;
        }
        ApplyChromeLayoutForWindowState();
        _diagnostics.Write("fullscreen", "exited", new
        {
            restoredState = WindowState.ToString(),
            forcedMaximized = forceMaximized, exitBehavior,
            position = Position.ToString(), Width, Height
        });
        FinishFullscreenTransition();
    }

    private void ReapplyNormalBoundsAfterFullscreen()
    {
        if (!_fullscreenNormalKnown) return;
        var position = _fullscreenNormalPosition;
        var size = _fullscreenNormalSize;
        void Assert()
        {
            _lastNormalWindowPosition = position;
            _lastNormalWindowSize = size;
            _normalPlacementKnown = true;
            _settings.WindowX = position.X;
            _settings.WindowY = position.Y;
            _settings.WindowWidth = size.Width;
            _settings.WindowHeight = size.Height;
        }
        Width = Math.Max(MinWidth, size.Width);
        Height = Math.Max(MinHeight, size.Height);
        Position = position;
        // Resizing while Normal fires the normal-placement tracker with transient geometry; restate the
        // remembered values immediately and once more after the transition settles.
        Assert();
        Dispatcher.UIThread.Post(() => { if (WindowState != WindowState.FullScreen) Assert(); }, DispatcherPriority.Loaded);
    }

    private void CancelSelectionForFullscreenTransition()
    {
        // Double-click enters fullscreen during the same pointer sequence that can begin a legacy
        // selection. Cancel capture/interaction first (not merely the visible rectangle), then clear
        // both semantic and fast-overlay state so the second click cannot leave a tiny selection box.
        Viewport.CancelInteraction();
        Viewport.ClearSelection();
        SelectionOverlay.SetSelection(null, default);
    }

    private void FinishFullscreenTransition()
    {
        Viewport.IsFullscreen = WindowState == WindowState.FullScreen;
        ApplyStatusVisibility();
        UpdateCaptionButtonState();
        // A final render-queue clear protects against a queued pointer/selection invalidation from the
        // double-click event that initiated the transition without changing legacy selection logic.
        Dispatcher.UIThread.Post(() => SelectionOverlay.SetSelection(null, default), DispatcherPriority.Render);
    }

    /// <summary>Entering fullscreen / regaining focus: cursor visible, hide countdown starts.</summary>
    private void ResetFullscreenCursorTimer()
    {
        if (WindowState != WindowState.FullScreen || !_settings.HideCursorFullscreen)
        {
            StopFullscreenCursorAutoHide();
            return;
        }
        _cursorAutoHide.Activate(DateTime.UtcNow);
        ApplyFullscreenCursorVisibility();
        _fullscreenCursorTimer.Start();
    }

    private void StopFullscreenCursorAutoHide()
    {
        _fullscreenCursorTimer.Stop();
        var wasHidden = _cursorAutoHide.IsHidden;
        _cursorAutoHide.Deactivate();
        if (wasHidden || ReferenceEquals(Cursor, HiddenCursor)) Cursor = null;
    }

    private void ApplyFullscreenCursorVisibility()
    {
        if (_cursorAutoHide.IsHidden) Cursor = HiddenCursor;
        else if (ReferenceEquals(Cursor, HiddenCursor)) Cursor = null;
    }

    /// <summary>Only pointer motion feeds the policy; clicks and keys never reveal the cursor.</summary>
    private void HandleFullscreenCursorMove(PointerEventArgs e, Point windowPointer)
    {
        if (!_cursorAutoHide.IsActive) return;
        var now = DateTime.UtcNow;
        var changed = _cursorAutoHide.SetSuppressed(IsPointerOverInteractiveChrome(e, windowPointer), now);
        changed |= _cursorAutoHide.OnPointerMoved(now, windowPointer.X, windowPointer.Y);
        if (changed) ApplyFullscreenCursorVisibility();
    }

    /// <summary>True when the pointer is over a visible control/overlay rather than the image surface.</summary>
    private bool IsPointerOverInteractiveChrome(PointerEventArgs e, Point windowPointer)
    {
        if (_wholeAppOverlayMode && IsPointerOverWholeAppOverlayPixels(windowPointer)) return true;
        for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, this); v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, Viewport)) return false;
            if (v is Avalonia.Controls.Primitives.TemplatedControl or Avalonia.Controls.ContextMenu or Avalonia.Controls.Primitives.Popup) return true;
        }
        return false;
    }
}
