using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Transport;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Platform;
using Glide.App.Services;
using Glide.Core.Commands;

namespace Glide.App;

/// <summary>Slideshow sessions and tab close/restore/clone helpers. Split from MainWindow (4.5.2).</summary>
public partial class MainWindow
{
    private Task ToggleSlideshowAsync() => EnsureSlideshow().ToggleAsync();

    private async Task<bool> ConfirmSlideshowSessionOptionsAsync()
    {
        var interval = new NumericUpDown { Minimum = 250, Maximum = 600000, Increment = 250, Value = SlideshowOptionsView.IntervalMs, MinWidth = 150 };
        var loop = new CheckBox { Content = "Loop slideshow", IsChecked = SlideshowOptionsView.Loop };
        var cross = new CheckBox { Content = "Continue across sibling folders", IsChecked = SlideshowOptionsView.CrossFolders };
        var shuffle = new CheckBox { Content = "Shuffle images", IsChecked = SlideshowOptionsView.Shuffle };
        var direction = new ComboBox
        {
            ItemsSource = new[] { "Forward", "Backward" },
            SelectedItem = string.Equals(SlideshowOptionsView.Direction, "Backward", StringComparison.OrdinalIgnoreCase) ? "Backward" : "Forward",
            MinWidth = 150
        };
        var startFullscreen = new CheckBox { Content = "Start slideshow in fullscreen", IsChecked = SlideshowOptionsView.StartFullscreen };
        var pauseInactive = new CheckBox { Content = "Pause while Glide is not the active window", IsChecked = SlideshowOptionsView.PauseWhenInactive };
        var rightAction = _inputRouter.ResolveGesture("slideshow.rightClick", _settings.Gestures);
        var rightClickStops = new CheckBox { Content = "Right-click stops an active slideshow", IsChecked = rightAction == GestureCatalog.Default };
        var leftAction = _inputRouter.ResolveGesture("slideshow.leftClick", _settings.Gestures);
        var leftLabels = SlideshowLeftClickChoices.Select(c => c.Label).ToList();
        var currentLeft = SlideshowLeftClickChoices.FirstOrDefault(c => c.Action == leftAction).Label;
        if (currentLeft is null)
        {
            // A Settings-only action: show it so the dialog does not silently change it.
            currentLeft = GestureCatalog.FindAction(leftAction)?.Label ?? leftAction;
            leftLabels.Add(currentLeft);
        }
        var leftClick = new ComboBox
        {
            ItemsSource = leftLabels,
            SelectedItem = currentLeft,
            MinWidth = 190
        };
        var qualityIndicator = new CheckBox { Content = "Show slideshow playing indicator", IsChecked = SlideshowOptionsView.ShowQualityIndicator };
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
        // Write a row only when its control changed, so a Settings-only binding is preserved.
        var stopsNow = rightClickStops.IsChecked == true;
        if (stopsNow != (rightAction == GestureCatalog.Default))
            _settings.Gestures["slideshow.rightClick"] = stopsNow ? GestureCatalog.Default : GestureCatalog.PassThrough;
        var pickedLeft = leftClick.SelectedItem?.ToString();
        if (!string.Equals(pickedLeft, currentLeft, StringComparison.Ordinal) &&
            SlideshowLeftClickChoices.FirstOrDefault(c => c.Label == pickedLeft) is { Label: not null } choice)
            _settings.Gestures["slideshow.leftClick"] = choice.Action;
        _settings.SlideshowShowQualityIndicator = qualityIndicator.IsChecked == true;
        SaveSettingsAndPublish();
        return true;
    }
    private void StopSlideshow(bool restoreStartingMode) => _slideshow?.Stop(restoreStartingMode);

    /// <summary>Runs the user-chosen action for a left-click on the picture during a slideshow.</summary>
    private async Task HandleSlideshowLeftClickAsync()
    {
        // Default of the "Slideshow: Left click" row; other actions arrive as gesture actions.
        if (_slideshow?.IsActive != true) return;
        await ToggleSlideshowAsync();
    }

    // The session dialog keeps its two familiar mouse controls; they now read and write the
    // Slideshow gesture rows. Labels map 1:1 onto the folded 4.7 choices.
    private static readonly (string Label, string Action)[] SlideshowLeftClickChoices =
    [
        (Glide.Core.Settings.SlideshowLeftClickActions.PauseResume, GestureCatalog.Default),
        (Glide.Core.Settings.SlideshowLeftClickActions.NextImage, GestureCatalog.Next),
        (Glide.Core.Settings.SlideshowLeftClickActions.PreviousImage, GestureCatalog.Previous),
        (Glide.Core.Settings.SlideshowLeftClickActions.StopSlideshow, GestureCatalog.StopSlideshow),
        (Glide.Core.Settings.SlideshowLeftClickActions.DoNothing, GestureCatalog.PassThrough)
    ];

    private void UpdateSlideshowUi()
    {
        var slideshow = _slideshow;
        var active = slideshow?.IsActive == true;
        var running = slideshow?.IsRunning == true;
        // Slideshow-area gesture rows (4.8.0) replace "Right-click stops slideshow" and
        // "Left-click during slideshow"; the viewport resolves them while a session is active.
        if (Viewport is not null)
        {
            Viewport.SlideshowActive = active;
            Viewport.SlideshowRunning = running;
        }
        if (SlideshowQualityIndicator is not null)
            SlideshowQualityIndicator.IsVisible = running && SlideshowOptionsView.ShowQualityIndicator;
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
            ToolTip.SetTip(SlideshowButton, $"{verb} ({ShortcutFor(GlideCommand.StartPauseSlideshow)})");
        }
        if (SlideshowStopButton is not null)
        {
            ToolTip.SetTip(SlideshowStopButton, $"Stop slideshow session ({ShortcutFor(GlideCommand.StopSlideshow)})");
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

    // ---- Fullscreen enter / exit (reworked in 5.0.2) ------------------------------------------------------------
    // Leaving fullscreen used to feel like the app reloading: the whole top level went to Opacity 0 (Glide's HWND
    // is transparency-capable, so the desktop showed through) for at least three render passes, the window walked
    // through an intermediate Normal state (Maximized origin: fullscreen -> maximized rect -> normal rect ->
    // maximized rect, each a real WM_SIZE), and every one of those resizes re-ran the compact chrome, the status
    // bar sizing and a full tab-strip rebuild. Now: one native state change where possible, the final chrome is
    // applied before the native change so the synchronous WM_SIZE layout pass is the only one, per-resize chrome
    // work is done once at the end, and only a Normal<->fullscreen change (a large geometry change that would show
    // the previous frame clipped or stretched) is hidden, with a DWM cloak that is lifted on the first composed
    // frame at the final geometry. Maximized<->fullscreen is not hidden at all.

    /// <summary>True while EnterFullscreen/ExitFullscreen change the native state: SizeChanged skips per-resize chrome work.</summary>
    private bool _fullscreenTransitionActive;
    private int _fullscreenTransitionResizeEvents;
    private int _fullscreenFenceGeneration;
    private bool _fullscreenFenceCloaked;
    private IntPtr _fullscreenFenceHwnd;
    private long _fullscreenFenceStartedTimestamp;

    private void EnterFullscreen()
    {
        if (WindowState == WindowState.FullScreen) return;
        var started = Stopwatch.GetTimestamp();
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
        var fenced = FullscreenTransitionPlanner.PlanEnterFence(priorState) == FullscreenTransitionFence.Cloak && BeginFullscreenTransitionFence();
        _fullscreenTransitionResizeEvents = 0;
        _fullscreenTransitionActive = true;
        try
        {
            // Final chrome first, so the layout pass Windows runs synchronously inside the state change already
            // lays out the fullscreen chrome (instead of the windowed chrome at fullscreen size, then again).
            ApplyChromeLayoutForWindowState(WindowState.FullScreen);
            WindowState = WindowState.FullScreen;
        }
        finally
        {
            _fullscreenTransitionActive = false;
            if (fenced) ScheduleFullscreenTransitionReveal("enter");
        }
        ApplyChromeLayoutForWindowState();
        ApplyChromeSizingAfterFullscreenTransition();
        ResetFullscreenCursorTimer();
        FinishFullscreenTransition();
        RefreshTabStripAfterFullscreenTransition();
        _diagnostics.Write("fullscreen", "entered", new
        {
            previousState = priorState, previousPosition = priorPosition, previousWidth = priorWidth, previousHeight = priorHeight,
            fence = fenced ? "cloak" : "none",
            resizeEvents = _fullscreenTransitionResizeEvents,
            transitionMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2)
        });
    }

    private void ExitFullscreen(bool forceMaximized)
    {
        if (WindowState != WindowState.FullScreen) return;
        var started = Stopwatch.GetTimestamp();
        CancelSelectionForFullscreenTransition();
        StopFullscreenCursorAutoHide();
        var snapshot = _fullscreen.Restore();
        var exitBehavior = forceMaximized ? FullscreenTransitionPlanner.MaximizedBehavior : (_settings.FullscreenExitBehavior ?? FullscreenTransitionPlanner.RestoreBehavior);
        var plan = FullscreenTransitionPlanner.PlanExit(snapshot.State, exitBehavior, forceMaximized, NativeFullscreenRestoreRectIsRememberedNormal(snapshot));
        var fenced = plan.Fence == FullscreenTransitionFence.Cloak && BeginFullscreenTransitionFence();
        var boundsReapplied = false;
        _fullscreenTransitionResizeEvents = 0;
        _fullscreenTransitionActive = true;
        try
        {
            ApplyChromeLayoutForWindowState(plan.TargetState);
            if (plan.StepThroughNormal)
            {
                // Legacy path (Minimized target, or remembered bounds that differ from the rectangle Avalonia saved
                // on entry): put the ordinary window on its real normal bounds first so the Win32 restore rectangle
                // and the remembered placement are the normal bounds, never fullscreen/maximized geometry.
                WindowState = WindowState.Normal;
                boundsReapplied = ReapplyNormalBoundsAfterFullscreen(snapshot, allowSnapshotFallback: false);
            }
            // Maximized origin: Avalonia's Win32 fullscreen kept WS_MAXIMIZE and the native restore rectangle, so a
            // direct Maximized needs no intermediate Normal geometry and un-maximizing later still returns to the
            // real normal bounds. Normal origin: leaving fullscreen restores the rectangle saved on entry.
            if (WindowState != plan.TargetState) WindowState = plan.TargetState;
            if (plan.TargetState == WindowState.Normal)
                boundsReapplied |= ReapplyNormalBoundsAfterFullscreen(snapshot, allowSnapshotFallback: true);
            else if (!plan.StepThroughNormal)
                RestateRememberedNormalPlacement();
        }
        finally
        {
            _fullscreenTransitionActive = false;
            if (fenced) ScheduleFullscreenTransitionReveal("exit");
        }
        ApplyChromeLayoutForWindowState();
        ApplyChromeSizingAfterFullscreenTransition();
        FinishFullscreenTransition();
        RefreshTabStripAfterFullscreenTransition();
        _diagnostics.Write("fullscreen", "exited", new
        {
            restoredState = WindowState.ToString(),
            forcedMaximized = forceMaximized, exitBehavior,
            path = plan.Path, fence = fenced ? "cloak" : "none", boundsReapplied,
            resizeEvents = _fullscreenTransitionResizeEvents,
            transitionMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2),
            position = Position.ToString(), Width, Height
        });
    }

    /// <summary>
    /// Whether the rectangle Avalonia restores when fullscreen ends (the live geometry captured on entry) is already
    /// the remembered normal placement. Only meaningful for a window that entered fullscreen from Normal.
    /// </summary>
    private bool NativeFullscreenRestoreRectIsRememberedNormal((WindowState State, PixelPoint Position, double Width, double Height) snapshot)
    {
        if (snapshot.State != WindowState.Normal || !_fullscreenNormalKnown) return true;
        return FullscreenTransitionPlanner.SameGeometry(snapshot.Position, new Size(snapshot.Width, snapshot.Height),
            _fullscreenNormalPosition, _fullscreenNormalSize, RenderScaling);
    }

    /// <summary>
    /// Re-applies the remembered normal bounds after leaving fullscreen, but only when the live window is not
    /// already there (a no-op Width/Height/Position re-assignment still costs native round trips and, with
    /// fractional DPI rounding, could nudge the window by a pixel). Returns true when the geometry was changed.
    /// </summary>
    private bool ReapplyNormalBoundsAfterFullscreen((WindowState State, PixelPoint Position, double Width, double Height) snapshot, bool allowSnapshotFallback)
    {
        if (!_fullscreenNormalKnown)
        {
            if (!allowSnapshotFallback || snapshot.State != WindowState.Normal || snapshot.Width <= 0 || snapshot.Height <= 0) return false;
            if (FullscreenTransitionPlanner.SameGeometry(Position, ClientSize, snapshot.Position, new Size(snapshot.Width, snapshot.Height), RenderScaling)) return false;
            Width = snapshot.Width;
            Height = snapshot.Height;
            Position = snapshot.Position;
            return true;
        }
        var position = _fullscreenNormalPosition;
        var size = _fullscreenNormalSize;
        var changed = false;
        if (!FullscreenTransitionPlanner.SameGeometry(Position, ClientSize, position, size, RenderScaling))
        {
            Width = Math.Max(MinWidth, size.Width);
            Height = Math.Max(MinHeight, size.Height);
            Position = position;
            changed = true;
        }
        RestateRememberedNormalPlacement();
        return changed;
    }

    /// <summary>
    /// Resizing while Normal fires the normal-placement tracker with transient geometry; restate the remembered
    /// normal bounds immediately and once more after the transition settles (bookkeeping only, no native calls).
    /// </summary>
    private void RestateRememberedNormalPlacement()
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
        Assert();
        Dispatcher.UIThread.Post(() => { if (WindowState != WindowState.FullScreen) Assert(); }, DispatcherPriority.Loaded);
    }

    /// <summary>The compact title-bar and status-bar sizing that SizeChanged skipped during the transition, run once.</summary>
    private void ApplyChromeSizingAfterFullscreenTransition()
    {
        ApplyCompactChromeLayout();
        ApplyStatusBarSize();
        if (_wholeAppOverlayMode) Dispatcher.UIThread.Post(AlignWholeAppOverlayChromeToPixels, DispatcherPriority.Render);
    }

    /// <summary>
    /// One tab-strip rebuild for the final width (it used to run on every intermediate WM_SIZE). With the title bar
    /// visible it is rebuilt before the first frame at the new size; with hidden fullscreen chrome it is not on
    /// screen, so the rebuild is kept off the transition frame.
    /// </summary>
    private void RefreshTabStripAfterFullscreenTransition()
    {
        bool Needed() => !_tabDrag.IsActive && _physicalDraggedTabId is null && Math.Abs(Bounds.Width - _lastTabLayoutWidth) >= 24;
        if (!Needed()) return;
        if (ChromeBorder.IsVisible)
        {
            // Lay out the final chrome first so the strip width the rebuild reads is the new one.
            UpdateLayout();
            _lastTabLayoutWidth = Bounds.Width;
            RebuildTabStrip();
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (!Needed()) return;
            _lastTabLayoutWidth = Bounds.Width;
            RebuildTabStrip();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Cloaks the HWND (DWM) for a Normal&lt;-&gt;fullscreen change. Returns false when nothing was hidden (not on
    /// Windows, no HWND, window not shown, or the warm-presentation gate already suppresses it).
    /// </summary>
    private bool BeginFullscreenTransitionFence()
    {
        if (_warmPresentationGateActive || !IsVisible) return false;
        if (_fullscreenFenceCloaked)
        {
            _fullscreenFenceGeneration++; // a newer transition takes over the pending reveal
            return true;
        }
        var platform = TryGetPlatformHandle();
        if (platform is null || platform.Handle == IntPtr.Zero ||
            !string.Equals(platform.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!WindowCloak.TrySet(platform.Handle, cloaked: true)) return false;
        _fullscreenFenceGeneration++;
        _fullscreenFenceCloaked = true;
        _fullscreenFenceHwnd = platform.Handle;
        _fullscreenFenceStartedTimestamp = Stopwatch.GetTimestamp();
        return true;
    }

    /// <summary>
    /// Reveals the cloaked window on the first frame composed at the final geometry: the composition batch is
    /// requested from inside the next render pass (after this transition's layout is queued, before it is
    /// committed), so its Rendered task completes only once that frame has been presented; DwmFlush then waits for
    /// DWM to compose it. A timer is the safety net so the window can never stay hidden.
    /// </summary>
    private void ScheduleFullscreenTransitionReveal(string transition)
    {
        if (!_fullscreenFenceCloaked) return;
        var generation = _fullscreenFenceGeneration;
        DispatcherTimer.RunOnce(() => RevealFullscreenTransition(generation, transition, "timeout"),
            TimeSpan.FromMilliseconds(FullscreenTransitionPlanner.RevealTimeoutMs));
        try
        {
            RequestAnimationFrame(frameTime =>
            {
                if (!_fullscreenFenceCloaked || generation != _fullscreenFenceGeneration) return;
                CompositionBatch? batch = null;
                try { batch = ElementComposition.GetElementVisual(this)?.Compositor.RequestCompositionBatchCommitAsync(); }
                catch (Exception ex) { _diagnostics.Write("fullscreen", "reveal_batch_failed", new { ex.Message }); }
                _ = RevealWhenComposedAsync(batch, generation, transition);
            });
        }
        catch (Exception ex)
        {
            _diagnostics.Write("fullscreen", "reveal_schedule_failed", new { ex.Message });
            RevealFullscreenTransition(generation, transition, "schedule_failed");
        }
    }

    private async Task RevealWhenComposedAsync(CompositionBatch? batch, int generation, string transition)
    {
        var reason = batch is null ? "no_compositor" : "rendered";
        try
        {
            if (batch is not null)
                await batch.Rendered.WaitAsync(TimeSpan.FromMilliseconds(FullscreenTransitionPlanner.RevealTimeoutMs)).ConfigureAwait(false);
            await Task.Run(WindowCloak.Flush).WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            reason = "frame_wait_timeout"; // reveal anyway; the RunOnce timer would otherwise do it a little later
        }
        catch (Exception ex)
        {
            reason = "frame_wait_failed:" + ex.GetType().Name;
        }
        Dispatcher.UIThread.Post(() => RevealFullscreenTransition(generation, transition, reason), DispatcherPriority.Send);
    }

    private void RevealFullscreenTransition(int generation, string transition, string reason)
    {
        if (!_fullscreenFenceCloaked || generation != _fullscreenFenceGeneration) return;
        _fullscreenFenceCloaked = false;
        var hwnd = _fullscreenFenceHwnd;
        _fullscreenFenceHwnd = IntPtr.Zero;
        var uncloaked = WindowCloak.TrySet(hwnd, cloaked: false);
        _diagnostics.Write("fullscreen", "revealed", new
        {
            transition, reason, uncloaked,
            hiddenMs = Math.Round(Stopwatch.GetElapsedTime(_fullscreenFenceStartedTimestamp).TotalMilliseconds, 2)
        });
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
        NotifyStatusChromeChanged();
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
