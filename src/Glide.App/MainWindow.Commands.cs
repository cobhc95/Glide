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

/// <summary>Pointer/keyboard shortcuts, command dispatch, clipboard, save and print workflows. Split from MainWindow (4.5.2).</summary>
public partial class MainWindow
{
    private async void MainPointerShortcutPressed(object? sender, PointerPressedEventArgs e)
    {
        // Plain pointer presses over the viewer are owned by the viewport/overlay gesture
        // routes.  Letting the global mouse-hotkey route consume them first is especially
        // dangerous in fullscreen: a rapid slideshow click can be interpreted twice (once by
        // navigation and once by a configured mouse command such as CloseWindow).  Modified
        // pointer shortcuts and X buttons remain available, while ordinary left/right/middle
        // clicks are arbitrated by the visual that owns the gesture.
        var point = e.GetCurrentPoint(this);
        var physical = PhysicalMouseButtons(point);
        // PointerUpdateKind is not stable across repeated presses (some backends report a generic
        // move/update kind while the button is physically down).  Classify the physical buttons
        // first so an unmodified ordinary click can never fall through to a mapped command such as
        // CloseWindow, regardless of the update kind reported by the platform.
        if (e.KeyModifiers == KeyModifiers.None && (physical.Left || physical.Right || physical.Middle))
            return;
        var shortcut = CanonicalMouseShortcut(e, this);
        if (shortcut is null) return;
        var command = _inputRouter.ResolveShortcut(shortcut, _settings.Hotkeys);
        if (command == GlideCommand.None) return;
        _diagnostics.Write("input", "mouse_hotkey_command", new { command = command.ToString(), shortcut });
        e.Handled = true;
        if (command == GlideCommand.PreviousImage || command == GlideCommand.NextImage)
        {
            await NavigateMouseAsync(command == GlideCommand.NextImage ? 1 : -1);
            return;
        }
        await ExecuteCommandAsync(command);
    }

    private static string? CanonicalMouseShortcut(PointerPressedEventArgs e, Control relativeTo)
    {
        var point = e.GetCurrentPoint(relativeTo);
        string? button = null;
        var update = point.Properties.PointerUpdateKind.ToString();
        if (update.Contains("XButton1", StringComparison.OrdinalIgnoreCase)) button = "MouseX1";
        else if (update.Contains("XButton2", StringComparison.OrdinalIgnoreCase)) button = "MouseX2";
        else
        {
            var physical = PhysicalMouseButtons(point);
            if (physical.Middle) button = "MouseMiddle";
            else if (physical.Right && !physical.Left) button = "MouseRight";
            else if (physical.Left) button = "MouseLeft";
        }
        if (button is null) return null;
        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(button);
        return string.Join("+", parts);
    }

    private async void GlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? GlideCommand.PreviousTab : GlideCommand.NextTab;
            e.Handled = true;
            _diagnostics.Write("input", "global_tab_cycle", new { command = command.ToString() });
            await ExecuteCommandAsync(command);
            return;
        }

        if (!_settings.EnableTabFocusNavigation && e.Key == Key.Tab && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // When Tab focus navigation is disabled, suppress Tab and Shift+Tab from navigating focus through UI buttons
            // unless active input is inside a text box.
            if (e.Source is not TextBox)
            {
                var shortcut = CanonicalShortcut(e);
                var mapped = _inputRouter.ResolveShortcut(shortcut, _settings.Hotkeys);
                if (mapped == GlideCommand.None)
                {
                    e.Handled = true;
                }
            }
        }
    }

    private bool _slideshowSpaceDown;

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Home)
            {
                if (_homeKeyDown)
                {
                    e.Handled = true;
                    return;
                }
                _homeKeyDown = true;
            }

            // Space always pauses / resumes an active slideshow, whatever Space is bound to otherwise
            // (by default it is "next image"). Key auto-repeat must not flip it back and forth.
            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && _slideshow?.IsActive == true &&
                e.Source is not TextBox)
            {
                e.Handled = true;
                if (_slideshowSpaceDown) return;
                _slideshowSpaceDown = true;
                await ToggleSlideshowAsync();
                return;
            }

            var shortcut = CanonicalShortcut(e);
            var command = _inputRouter.ResolveShortcut(shortcut, _settings.Hotkeys);
            if (command == GlideCommand.None) return;

        // Arrow navigation has two distinct intents:
        //   1. a tap is exactly one ordinary navigation command (same semantics as clicking the UI),
        //   2. a genuine hold becomes frame-paced rapid browsing only after a short hold threshold.
        // Never treat the first KeyDown as permission to start a 120 Hz producer: a perfectly normal
        // human key press lasts many render frames and previously caused one tap to skip several images.
        // Windows auto-repeat KeyDown events are ignored while the physical key remains down; Glide owns
        // the held playback loop after the threshold, so there is still only one navigation producer.
        if ((e.Key == Key.Right && command == GlideCommand.NextImage) ||
            (e.Key == Key.Left && command == GlideCommand.PreviousImage))
        {
            var direction = command == GlideCommand.NextImage ? 1 : -1;
            if (_navigationBoundaryPromptActive)
            {
                e.Handled = true;
                return;
            }
            if (_suppressedHeldNavigationDirection == direction)
            {
                // Windows can resume auto-repeat as soon as a modal dialog closes even though the
                // user never released the key. That repeat is stale input: require KeyUp first.
                e.Handled = true;
                return;
            }
            if (_suppressedHeldNavigationDirection != 0 && _suppressedHeldNavigationDirection != direction)
                _suppressedHeldNavigationDirection = 0;
            if (!_heldNavigationKeyIsDown || _heldNavigationDirection != direction)
                StartHeldNavigation(direction);
            e.Handled = true;
            return;
        }

        if (command == GlideCommand.PreviousImage || command == GlideCommand.NextImage)
        {
            await NavigateKeyboardAsync(command == GlideCommand.NextImage ? 1 : -1);
            e.Handled = true;
            return;
        }

        _diagnostics.Write("input", "command", new { command = command.ToString(), shortcut });
        await ExecuteCommandAsync(command);
            e.Handled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _diagnostics.Write("input", "key_command_failed", new { key = e.Key.ToString(), error = ex.GetType().Name, ex.Message });
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Home) _homeKeyDown = false;
        if (e.Key == Key.Space) _slideshowSpaceDown = false;
        if ((e.Key == Key.Right && _suppressedHeldNavigationDirection > 0) ||
            (e.Key == Key.Left && _suppressedHeldNavigationDirection < 0))
        {
            _suppressedHeldNavigationDirection = 0;
            e.Handled = true;
        }
        if ((e.Key == Key.Right && _heldNavigationDirection > 0) ||
            (e.Key == Key.Left && _heldNavigationDirection < 0))
        {
            StopHeldNavigation(settle: true);
            e.Handled = true;
        }
    }

    private void QueueWheelNavigation(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0 || _navigationBoundaryPromptActive) return;

        var now = Stopwatch.GetTimestamp();
        // One isolated notch is intentionally identical to clicking Previous/Next. Only a sustained
        // burst switches subsequent notches to the rapid-preview path. A 220 ms window tolerates
        // ordinary wheel hardware while still requiring clear repeated intent.
        var rapid = _lastWheelNavigationTimestamp != 0 &&
                    Stopwatch.GetElapsedTime(_lastWheelNavigationTimestamp, now) <= TimeSpan.FromMilliseconds(220);
        _lastWheelNavigationTimestamp = now;

        lock (_wheelNavigationSync)
        {
            // A bounded queue keeps input responsive without turning old wheel ticks into seconds of
            // post-input movement. Five frames is enough to absorb a short decode/render hiccup.
            while (_wheelNavigationQueue.Count >= MaxBufferedNavigationCommands)
                _wheelNavigationQueue.Dequeue();
            _wheelNavigationQueue.Enqueue((direction, rapid));
            if (_wheelNavigationProcessorRunning) return;
            _wheelNavigationProcessorRunning = true;
        }
        _ = ProcessWheelNavigationQueueAsync();
    }

    private async Task ProcessWheelNavigationQueueAsync()
    {
        var usedRapidPreview = false;
        try
        {
            while (true)
            {
                (int Direction, bool Rapid) request;
                lock (_wheelNavigationSync)
                {
                    if (_wheelNavigationQueue.Count == 0)
                    {
                        _wheelNavigationProcessorRunning = false;
                        break;
                    }
                    request = _wheelNavigationQueue.Dequeue();
                }

                if (_navigationBoundaryPromptActive)
                    break;

                if (request.Rapid)
                {
                    // Serialize every wheel notch and wait for a render turn after each reduced frame.
                    // This preserves the same no-teleport invariant as held keyboard navigation: fast
                    // input may lower preview fidelity, but it does not deliberately skip images.
                    usedRapidPreview |= await NavigateRapidStepAsync(request.Direction, CancellationToken.None);
                }
                else
                {
                    await NavigateAsync(request.Direction);
                }
            }
        }
        finally
        {
            var restart = false;
            lock (_wheelNavigationSync)
            {
                if (_wheelNavigationQueue.Count == 0)
                    _wheelNavigationProcessorRunning = false;
                else
                {
                    _wheelNavigationProcessorRunning = true;
                    restart = true;
                }
            }

            // Do not arm full-resolution refinement between individual queued wheel frames. That would
            // compete with the next reduced decode on slower hardware. Refine once the serialized burst
            // has actually drained, matching the keyboard hold/release contract.
            if (!restart && usedRapidPreview) ScheduleRapidNavigationSettle();
            if (restart) _ = ProcessWheelNavigationQueueAsync();
        }
    }

    private void StartHeldNavigation(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0 || _navigationBoundaryPromptActive) return;

        if (_heldNavigationCts is { IsCancellationRequested: false } &&
            _heldNavigationDirection == direction && _heldNavigationKeyIsDown)
            return;

        StopHeldNavigation(settle: false);
        _heldNavigationDirection = direction;
        _heldNavigationKeyIsDown = true;
        _heldNavigationCts = new CancellationTokenSource();
        var token = _heldNavigationCts.Token;
        _ = RunHeldNavigationAsync(direction, token);
    }

    private void StopHeldNavigation(bool settle, string? reason = null)
    {
        var wasActive = _heldNavigationCts is not null || _heldNavigationKeyIsDown;
        _heldNavigationCts?.Cancel();
        _heldNavigationCts?.Dispose();
        _heldNavigationCts = null;
        _heldNavigationDirection = 0;
        _heldNavigationKeyIsDown = false;
        if (wasActive && reason is not null)
            _diagnostics.Write("navigation", "held_playback_cancelled", new { reason });
        if (settle) ScheduleRapidNavigationSettle();
    }

    private async Task RunHeldNavigationAsync(int direction, CancellationToken token)
    {
        // A fresh press is one normal navigation action, including the exact same sibling / hierarchical
        // folder-boundary policy used by status-bar/title-bar mouse buttons. Only if the key is STILL down
        // after the hold threshold do we enter the reduced-resolution playback path. This makes a tap
        // deterministic regardless of whether it lasts 30 ms or a few render frames.
        const int holdActivationMs = 180;
        var minimumFrameTime = TimeSpan.FromSeconds(1.0 / 120.0);
        try
        {
            await NavigateKeyboardAsync(direction);
            if (token.IsCancellationRequested) return;

            await Task.Delay(holdActivationMs, token);
            if (token.IsCancellationRequested) return;

            _diagnostics.Write("navigation", "held_playback_started", new { direction, holdActivationMs });
            while (!token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                if (TryAcceptNavigationRateLimit(NavigationRateInput.Keyboard, showIndicator: false))
                {
                    if (!await NavigateRapidStepAsync(direction, token)) break;
                }

                var elapsed = Stopwatch.GetElapsedTime(started);
                var remaining = minimumFrameTime - elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, token);
                else
                    await Task.Yield();
            }
        }
        catch (OperationCanceledException) { }
    }

    private Task<bool> NavigateRapidStepAsync(int delta, CancellationToken token) =>
        RunSerializedNavigationResultAsync(() => NavigateRapidStepCoreAsync(delta, token));

    private async Task<bool> RunSerializedNavigationResultAsync(Func<Task<bool>> action)
    {
        await _navigationSerializationGate.WaitAsync().ConfigureAwait(true);
        try { return await action().ConfigureAwait(true); }
        finally { _navigationSerializationGate.Release(); }
    }

    private async Task<bool> NavigateRapidStepCoreAsync(int delta, CancellationToken token)
    {
        if (_navigationBoundaryPromptActive || _navigator.Count == 0 || delta == 0 || token.IsCancellationRequested) return false;
        var navigationEpoch = _navigationCommandEpoch;
        _selectionZoomDemandPath = null;
        _lastNavigationDirection = Math.Sign(delta);
        _loader.ReportNavigationActivity(ImageNavigationActivity.RapidBrowse);

        if (!_folderIndexReady) return false;
        var atBoundary = delta > 0 ? _navigator.Index >= _navigator.Count - 1 : _navigator.Index <= 0;
        if (atBoundary && !_folderIndexCanonicalReady) return false;
        if (atBoundary)
        {
            // Held keyboard navigation must obey the SAME global cross-folder policy as mouse/icon
            // navigation. The lookup happens only at a real boundary, never on every frame. If automatic
            // continuation is disabled, stop here; otherwise enter the next sibling / hierarchical branch
            // using the same settings and confirmation rules as NavigateAsync.
            if (!_settings.ContinueSiblingFolders) return false;
            if (!await TryNavigateSiblingFolderAsync(Math.Sign(delta), rapidPreviewOnly: true)) return false;
            if (navigationEpoch != _navigationCommandEpoch && _navigationBoundaryPromptActive) return false;

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (token.IsCancellationRequested) return false;
            if (_navigator.Count > 1)
                _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);
            return true;
        }

        _navigator.Move(delta);
        _diagnostics.Write("navigation", "held_frame_requested", new
        {
            delta, index = _navigator.Index, count = _navigator.Count
        });

        // Schedule prefetching of the next frames immediately so background decoding
        // runs concurrently while presenting and rendering the current frame.
        if (_navigator.Count > 1)
            _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);

        await PresentCurrentAsync(rapidPreviewOnly: true);
        if (token.IsCancellationRequested) return false;

        await Task.Yield();
        return true;
    }

    private async Task ExecuteGestureActionAsync(string actionId)
    {
        actionId = GestureCatalog.NormalizeAction(actionId);
        if (actionId is GestureCatalog.Default or GestureCatalog.Nothing) return;
        if (actionId == GestureCatalog.ContextMenu)
        {
            var menu = BuildViewerContextMenu();
            menu.Open(Viewport);
            return;
        }
        if (actionId == GestureCatalog.ClearSelection)
        {
            Viewport.ClearSelection();
            return;
        }
        var command = GestureCatalog.CommandForAction(actionId);
        if (command == GlideCommand.PreviousImage || command == GlideCommand.NextImage)
            await NavigateMouseAsync(command == GlideCommand.NextImage ? 1 : -1);
        else if (command != GlideCommand.None)
            await ExecuteCommandAsync(command);
    }

    private Task NavigateHomeAsync() => RunSerializedNavigationAsync(NavigateHomeCoreAsync);

    private async Task NavigateHomeCoreAsync()
    {
        _diagnostics.Write("workspace", "home_begin", new
        {
            active = _workspace.Active?.Kind.ToString(), currentPath = _currentPath,
            slideshowActive = _slideshow?.IsActive == true, request = Volatile.Read(ref _imageRequestId),
            workspaceEpoch = Volatile.Read(ref _workspaceEpoch)
        });
        // Home mutates the same workspace/navigator/presentation state as image navigation. It must
        // therefore own the navigation gate for the entire transition. Previously Home could race an
        // async slideshow tick or rapid click continuation, which could then present into a workspace
        // that had already switched to Home.
        StopHeldNavigation(settle: false, reason: "home");
        StopSlideshow(false);
        Interlocked.Increment(ref _navigationCommandEpoch);
        AdvanceWorkspaceEpoch();
        Interlocked.Increment(ref _imageRequestId);
        _loader.InvalidatePending();
        _diagnostics.Write("workspace", "home_invalidated", new
        { request = Volatile.Read(ref _imageRequestId), workspaceEpoch = Volatile.Read(ref _workspaceEpoch) });

        if (string.Equals(_settings.HomePageMode, "Browser page", StringComparison.OrdinalIgnoreCase))
        {
            var homeFolder = ResolveHomeBrowserFolder();
            var browser = _workspace.Tabs.OfType<BrowserTabState>()
                .FirstOrDefault(t => string.Equals(t.Folder, homeFolder, StringComparison.OrdinalIgnoreCase));
            browser ??= (BrowserTabState)CreateConfiguredHomeDestination();
            _workspace.Select(browser.Id);
        }
        else
        {
            var home = _workspace.Tabs.FirstOrDefault(t => t.Kind == TabKind.Home);
            home ??= CreateConfiguredHomeDestination();
            _workspace.Select(home.Id);
        }

        _diagnostics.Write("workspace", "home_navigate", new
        {
            active = _workspace.Active?.Kind.ToString(),
            tabs = _workspace.Tabs.Count,
            visible = IsVisible
        });
        await ActivateWorkspaceAsync().ConfigureAwait(true);
        _diagnostics.Write("workspace", "home_complete", new
        { active = _workspace.Active?.Kind.ToString(), imageVisible = ImageView.IsVisible, homeVisible = HomeHost.IsVisible });
    }

    private async Task ExecuteCommandAsync(GlideCommand command)
    {
        // Command entry points include async-void Avalonia event handlers and native key/pointer
        // callbacks.  Never allow a transient decode/navigation failure to escape those callbacks
        // and terminate the process (Home and rapid slideshow input are especially exposed).
        try { await _commands.ExecuteAsync(command).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _diagnostics.Write("input", "command_failed", new { command = command.ToString(), error = ex.GetType().Name, ex.Message });
        }
    }

    internal IReadOnlyCollection<GlideCommand> ProductionMappedCommandsForTests() => _commands.MappedCommands;

    private MainWindowCommandDispatcher CreateCommandDispatcher()
    {
        Task Event(Action<object?, RoutedEventArgs> action) { action(this, new RoutedEventArgs()); return Task.CompletedTask; }
        var actions = new Dictionary<GlideCommand, Func<Task>>
        {
            [GlideCommand.OpenFile] = () => Event(OpenClicked), [GlideCommand.OpenFolder] = () => Event(OpenFolderClicked),
            [GlideCommand.RefreshFolder] = async () => { if (_workspace.Active is BrowserTabState b) NavigateBrowserTo(b.Folder, false); else if (_currentPath is not null) await LoadPathAsync(_currentPath, true); },
            [GlideCommand.Home] = NavigateHomeAsync,
            [GlideCommand.PreviousImage] = () => NavigateAsync(-1), [GlideCommand.NextImage] = () => NavigateAsync(1),
            [GlideCommand.FirstImage] = () => NavigateEdgeAsync(-1), [GlideCommand.LastImage] = () => NavigateEdgeAsync(1),
            [GlideCommand.PreviousFolder] = () => RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(-1)), [GlideCommand.NextFolder] = () => RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(1)),
            [GlideCommand.ExploreParentFolder] = () => Task.CompletedTask,
            [GlideCommand.FitImage] = () => { Viewport.Fit(); return Task.CompletedTask; }, [GlideCommand.FitWidth] = () => { Viewport.FitWidth(); return Task.CompletedTask; }, [GlideCommand.FitHeight] = () => { Viewport.FitHeight(); return Task.CompletedTask; }, [GlideCommand.FitWindow] = () => { Viewport.FitWindow(); return Task.CompletedTask; }, [GlideCommand.ActualSize] = () => { Viewport.ActualSize(); return Task.CompletedTask; },
            [GlideCommand.ToggleFitActual] = () => { if (Viewport.ZoomPercent == 100) Viewport.Fit(); else Viewport.ActualSize(); return Task.CompletedTask; }, [GlideCommand.ZoomIn] = () => { Viewport.ZoomBy(1.15); return Task.CompletedTask; }, [GlideCommand.ZoomOut] = () => { Viewport.ZoomBy(1 / 1.15); return Task.CompletedTask; },
            [GlideCommand.RotateLeft] = () => { Viewport.RotateLeft(); return Task.CompletedTask; }, [GlideCommand.RotateRight] = () => { Viewport.RotateRight(); return Task.CompletedTask; }, [GlideCommand.FlipHorizontal] = () => { Viewport.FlipHorizontal(); return Task.CompletedTask; }, [GlideCommand.FlipVertical] = () => { Viewport.FlipVertical(); return Task.CompletedTask; },
            [GlideCommand.NewTab] = async () => { CreateConfiguredNewTabDestination(); await ActivateWorkspaceAsync(); }, [GlideCommand.NewWindowSameImage] = OpenCurrentInNewWindowAsync, [GlideCommand.DuplicateSessionWindow] = DuplicateSessionWindowAsync, [GlideCommand.CloseTab] = CloseActiveTabAsync, [GlideCommand.RestoreClosedTab] = RestoreClosedTabAsync, [GlideCommand.DuplicateTab] = DuplicateActiveTabAsync,
            [GlideCommand.NextTab] = () => CycleTabAsync(1), [GlideCommand.PreviousTab] = () => CycleTabAsync(-1),
            [GlideCommand.SelectTab1] = () => SelectTabIndexAsync(0), [GlideCommand.SelectTab2] = () => SelectTabIndexAsync(1), [GlideCommand.SelectTab3] = () => SelectTabIndexAsync(2), [GlideCommand.SelectTab4] = () => SelectTabIndexAsync(3), [GlideCommand.SelectTab5] = () => SelectTabIndexAsync(4), [GlideCommand.SelectTab6] = () => SelectTabIndexAsync(5), [GlideCommand.SelectTab7] = () => SelectTabIndexAsync(6), [GlideCommand.SelectTab8] = () => SelectTabIndexAsync(7), [GlideCommand.SelectLastTab] = () => SelectTabIndexAsync(Math.Max(0, _workspace.Tabs.Count - 1)),
            [GlideCommand.DetachTab] = async () => { if (_workspace.Active is { } a) await DetachTabAsync(a.Id, this.PointToScreen(new Point(Bounds.Width / 2, 48))); }, [GlideCommand.CloseWindow] = () => { Close(); return Task.CompletedTask; }, [GlideCommand.ToggleFullscreen] = () => { ToggleFullscreen(); return Task.CompletedTask; }, [GlideCommand.StartPauseSlideshow] = ToggleSlideshowAsync, [GlideCommand.StopSlideshow] = () => { StopSlideshow(true); return Task.CompletedTask; },
            [GlideCommand.ToggleImageInfo] = () => Event(InfoClicked), [GlideCommand.ClearSelection] = HandleEscapeAsync, [GlideCommand.Settings] = () => Event(SettingsClicked), [GlideCommand.AddOverlay] = () => Event(OverlayAddClicked), [GlideCommand.SaveOverlayLayout] = () => Event(OverlaySaveClicked), [GlideCommand.LoadOverlayLayout] = () => Event(OverlayLoadClicked), [GlideCommand.ResetSelectedOverlayZoom] = () => { EnsureOverlays().ResetSelectedZoom(); return Task.CompletedTask; }, [GlideCommand.BringSelectedOverlayFront] = () => { EnsureOverlays().BringSelectedToFront(); return Task.CompletedTask; },
            [GlideCommand.ToggleAlwaysOnTop] = () => Event(AlwaysOnTopClicked), [GlideCommand.ToggleTransparency] = () => Event(TransparencyClicked), [GlideCommand.ShowContextMenu] = () => { ShowViewerContextMenu(); return Task.CompletedTask; }, [GlideCommand.BrowserBack] = () => Event(BrowserBackClicked), [GlideCommand.BrowserForward] = () => Event(BrowserForwardClicked), [GlideCommand.BrowserUp] = () => Event(BrowserUpClicked),
            [GlideCommand.CopyImage] = () => { CopyImagePixels(); return Task.CompletedTask; }, [GlideCommand.ExportSelection] = ExportSelectionAsync, [GlideCommand.CopyFile] = () => { CopyImageFile(); return Task.CompletedTask; }, [GlideCommand.CopyFileName] = () => CopyTextAsync(_currentPath is null ? null : Path.GetFileName(_currentPath)), [GlideCommand.CopyFolderPath] = () => CopyTextAsync(_currentPath is null ? null : Path.GetDirectoryName(_currentPath)), [GlideCommand.CopyFullPath] = () => CopyTextAsync(_currentPath), [GlideCommand.RenameFile] = RenameCurrentFileAsync, [GlideCommand.DeleteFile] = DeleteCurrentFileAsync, [GlideCommand.OpenContainingFolder] = () => { OpenContainingFolder(); return Task.CompletedTask; }, [GlideCommand.Print] = PrintCurrentImageAsync, [GlideCommand.SaveImage] = SaveCurrentImageAsync, [GlideCommand.SaveImageAs] = SaveCurrentImageAsAsync, [GlideCommand.ExternalProgram1] = () => { LaunchExternal(1); return Task.CompletedTask; }, [GlideCommand.ExternalProgram2] = () => { LaunchExternal(2); return Task.CompletedTask; }, [GlideCommand.ExternalProgram3] = () => { LaunchExternal(3); return Task.CompletedTask; }
        };
        actions[GlideCommand.ToggleStatusSurface] = () => { if (!_settings.ShowStatusSurface) { _settings.ShowStatusSurface = true; _statusCollapsed = false; SaveSettingsAndPublish(); } else _statusCollapsed = !_statusCollapsed; ApplyStatusVisibility(); return Task.CompletedTask; };
        MainWindowCommandDispatcher.AssertComplete(actions.Keys);
        return new MainWindowCommandDispatcher(actions);
    }

    private static string CanonicalShortcut(KeyEventArgs e)
    {
        // OemPlus is the physical =/+ key on common layouts.  Treat Shift+OemPlus as the semantic
        // "+" shortcut (without serializing a redundant "Shift++"), while an unshifted press remains
        // "=". Numpad Add is always "+". This keeps capture and runtime lookup byte-for-byte aligned.
        var shiftedOemPlus = e.Key == Key.OemPlus && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var key = e.Key switch
        {
            Key.Escape => "Esc", Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab", Key.Back => "Backspace",
            Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Home => "Home", Key.End => "End", Key.Delete => "Delete",
            Key.Add => "+", Key.OemPlus when shiftedOemPlus => "+", Key.OemPlus => "=", Key.OemMinus or Key.Subtract => "-",
            Key.OemComma => ",", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4", Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
            _ => e.Key.ToString()
        };
        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !shiftedOemPlus) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(key);
        return string.Join("+", parts);
    }

    private string ShortcutFor(GlideCommand command)
    {
        var shortcuts = HotkeyCatalog.ShortcutsForCommand(command, _settings.Hotkeys);
        return shortcuts.Count == 0 ? "Unassigned" : string.Join(" / ", shortcuts);
    }

    private void RefreshDynamicTooltips()
    {
        void Tip(Control c, string text, GlideCommand? command = null)
        {
            ToolTip.SetTip(c, command is null ? text : $"[{text} ({ShortcutFor(command.Value)})]");
            ToolTip.SetShowDelay(c, 650);
        }
        Tip(NewTabButton, "New tab", GlideCommand.NewTab);
        foreach (var (id, button) in _titleBarButtons)
        {
            if (TitleBarButtonCatalog.Find(id) is not { } definition) continue;
            var label = id == "window.transparency"
                ? $"Window transparency: {(int)Math.Round(_sessionOpacity * 100)}%"
                : definition.Label;
            Tip(button, label, definition.Command);
        }
        if (_homeSurface is not null) Tip(_homeSurface.OptionsButton, "Settings", GlideCommand.Settings);
        Tip(StatusZoomOutButton, "Zoom out", GlideCommand.ZoomOut);
        Tip(StatusZoomInButton, "Zoom in", GlideCommand.ZoomIn);
        Tip(StatusFitImageButton, $"Fit image: shrink to fit, never enlarge past 100% (applies to: {_settings.StatusFitScope})", GlideCommand.FitImage);
        Tip(StatusFitWindowButton, $"Fit to window: enlarge or shrink to fill the window (applies to: {_settings.StatusFitScope})", GlideCommand.FitWindow);
        Tip(StatusFitWidthButton, $"Fit width (applies to: {_settings.StatusFitScope})", GlideCommand.FitWidth);
        Tip(StatusFitHeightButton, $"Fit height (applies to: {_settings.StatusFitScope})", GlideCommand.FitHeight);
        Tip(SlideshowButton, _slideshow?.IsActive != true ? "Start slideshow" : _slideshow.IsRunning ? "Pause slideshow" : "Resume slideshow", GlideCommand.StartPauseSlideshow);
        Tip(SlideshowStopButton, "Stop slideshow session", GlideCommand.StopSlideshow);
        Tip(StatusInfoButton, "Image information", GlideCommand.ToggleImageInfo);
        Tip(StatusOptionsButton, "Settings", GlideCommand.Settings);
        Tip(StatusFirstButton, "First image", GlideCommand.FirstImage);
        Tip(StatusPreviousButton, "Previous image", GlideCommand.PreviousImage);
        Tip(StatusNextButton, "Next image", GlideCommand.NextImage);
        Tip(StatusLastButton, "Last image", GlideCommand.LastImage);
        Tip(StatusActualButton, "100% / actual size", GlideCommand.ActualSize);
        UpdateCollapseButtonGlyphs();
        Tip(StatusRestoreButton, "Expand status surface", GlideCommand.ToggleStatusSurface);
        Tip(StatusCloseButton, "Hide status surface");
        if (_browserSurface is not null)
        {
            Tip(_browserSurface.BackButton, "Back", GlideCommand.BrowserBack);
            Tip(_browserSurface.ForwardButton, "Forward", GlideCommand.BrowserForward);
            Tip(_browserSurface.UpButton, "Up one folder", GlideCommand.BrowserUp);
            Tip(_browserSurface.RefreshButton, "Refresh", GlideCommand.RefreshFolder);
        }
    }

    private void CopyImagePixels()
    {
        if (Viewport.Bitmap is not { } bitmap) return;
        Avalonia.PixelRect? selection = Viewport.TryGetSelectionPixelRect(out var selected) ? selected : null;
        var ok = WindowsClipboard.TrySetBitmap(bitmap, selection);
        _diagnostics.Write("clipboard", ok ? "image_pixels_copied" : "image_pixels_copy_failed", new
        {
            selected = selection is not null,
            width = selection?.Width ?? bitmap.PixelSize.Width,
            height = selection?.Height ?? bitmap.PixelSize.Height
        });
    }

    private async Task ExportSelectionAsync()
    {
        if (Viewport.Bitmap is not { } bitmap || !Viewport.TryGetSelectionPixelRect(out var selection) || selection.Width <= 0 || selection.Height <= 0)
        {
            _diagnostics.Write("selection", "export_rejected_no_selection", null);
            return;
        }
        var stem = string.IsNullOrWhiteSpace(_currentPath) ? "glide-selection" : Path.GetFileNameWithoutExtension(_currentPath) + "-selection";
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export selected region",
                SuggestedFileName = stem + ".png",
                DefaultExtension = "png",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("JPEG image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                    new FilePickerFileType("Bitmap image") { Patterns = new[] { "*.bmp" } }
                }
            });
            if (file is null) return;
            var destination = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(destination)) return;
            await ImageSelectionExportService.ExportAsync(bitmap, selection, destination);
            _diagnostics.Write("selection", "exported", new { path = destination, selection.Width, selection.Height });
        }
        catch (Exception ex)
        {
            _diagnostics.Write("selection", "export_failed", new { error = ex.Message });
        }
    }

    private const float SaveJpegQuality = 0.92f;

    private void SaveImageClicked(object? sender, RoutedEventArgs e) => _ = SaveCurrentImageAsync();

    private void SaveImageAsClicked(object? sender, RoutedEventArgs e) => _ = SaveCurrentImageAsAsync();

    /// <summary>Save is only meaningful once the picture has been rotated or flipped.</summary>
    private bool CanSaveCurrentImage()
        => Viewport.Bitmap is not null && !string.IsNullOrWhiteSpace(_currentPath) && !Viewport.EditTransform.IsIdentity;

    private void UpdateSaveButtons()
    {
        if (StatusSaveButton is null || StatusSaveAsButton is null) return;
        var hasImage = Viewport.Bitmap is not null && !string.IsNullOrWhiteSpace(_currentPath);
        StatusSaveButton.IsEnabled = CanSaveCurrentImage();
        StatusSaveAsButton.IsEnabled = hasImage;
    }

    private Task SaveCurrentImageAsync()
    {
        var path = _currentPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Task.CompletedTask;
        // Nothing edited: Save does nothing (unlike Save As, which always writes a copy).
        if (!CanSaveCurrentImage()) return Task.CompletedTask;
        // Formats the WIC bridge cannot write (WebP, GIF, HEIC, ...) fall through to Save As.
        if (!ImageSavePolicy.CanSaveInPlace(path, NativeImageEncoder.CanEncode)) return SaveCurrentImageAsAsync();
        return SaveCurrentImageToAsync(path, confirmOverwrite: true);
    }

    private async Task SaveCurrentImageAsAsync()
    {
        var path = _currentPath;
        if (string.IsNullOrWhiteSpace(path) || Viewport.Bitmap is null) return;
        try
        {
            var srcExt = Path.GetExtension(path).ToLowerInvariant();
            var defaultExt = NativeImageEncoder.CanEncode(srcExt) ? srcExt.TrimStart('.') : "png";
            var folder = Path.GetDirectoryName(path);
            var start = folder is null ? null : await StorageProvider.TryGetFolderFromPathAsync(folder);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save image as",
                SuggestedFileName = Path.GetFileNameWithoutExtension(path) + "." + defaultExt,
                SuggestedStartLocation = start,
                DefaultExtension = defaultExt,
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("JPEG image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                    new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("Bitmap image") { Patterns = new[] { "*.bmp" } },
                    new FilePickerFileType("TIFF image") { Patterns = new[] { "*.tif", "*.tiff" } }
                }
            });
            var destination = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(destination)) return;
            destination = ImageSavePolicy.EnsureExtension(destination, "." + defaultExt);
            // The system picker already confirmed overwrite (ShowOverwritePrompt).
            await SaveCurrentImageToAsync(destination, confirmOverwrite: false);
        }
        catch (Exception ex)
        {
            _diagnostics.Write("file", "save_as_failed", new { error = ex.Message });
        }
    }

    private async Task SaveCurrentImageToAsync(string destination, bool confirmOverwrite)
    {
        var source = _currentPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;
        var transform = Viewport.EditTransform;
        if (confirmOverwrite && File.Exists(destination) &&
            !await ConfirmAsync("Save image", $"Overwrite \"{Path.GetFileName(destination)}\"?" +
                (transform.IsIdentity ? "" : " The current rotation/flip will be applied to the file.") +
                " Embedded metadata (EXIF) is not preserved.", "Overwrite"))
            return;
        Bitmap? full = null;
        try
        {
            // Always encode from a fresh full-resolution decode: the viewport may show a reduced preview.
            full = await Task.Run(async () =>
            {
                await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                return await _decoderBackend.DecodeFullAsync(stream, source);
            });
            await ImageSaveService.SaveAsync(full, transform, destination, SaveJpegQuality);
            _diagnostics.Write("file", "saved", new { source, destination, transform.QuarterTurns, transform.FlipHorizontal, transform.FlipVertical });
            if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            {
                // Edits are now baked in; reload so the view no longer re-applies them.
                await LoadPathAsync(source, updateActiveTab: true);
            }
        }
        catch (Exception ex)
        {
            _diagnostics.Write("file", "save_failed", new { destination, error = ex.Message });
            await ConfirmAsync("Save failed", ex.Message, "OK", showCancel: false);
        }
        finally { full?.Dispose(); }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string acceptText, bool showCancel = true)
    {
        var accept = new Button { Content = acceptText, MinWidth = 100 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsVisible = showCancel };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        var root = new StackPanel { Margin = new Thickness(22), Spacing = 14 };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        root.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Icon = Icon, Width = 470, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root, Topmost = Topmost
        };
        accept.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        _overlays?.SetModalSuppressed(true);
        try { return await ShowOwnedDialogAsync<bool>(dialog); }
        finally { _overlays?.SetModalSuppressed(false); }
    }

    private void CopyImageFile()
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        var ok = WindowsClipboard.TrySetFileDrop(_currentPath);
        _diagnostics.Write("clipboard", ok ? "image_file_copied" : "image_file_copy_failed", new { path = _currentPath });
    }

    /// <summary>
    /// File → Print… / Ctrl+P / viewer context menu. Prints the currently displayed image
    /// from its original decoded pixels — never a screenshot of the viewport.
    /// </summary>
    private Task PrintCurrentImageAsync()
    {
        if (!PrintService.IsSupported)
        {
            _diagnostics.Write("print", "unsupported_platform", null);
            return Task.CompletedTask;
        }
        var path = _currentPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || Viewport.Bitmap is null)
        {
            _diagnostics.Write("print", "rejected_no_image", new { path });
            return Task.CompletedTask;
        }
        var frame = Viewport.PresentedFrame;
        var fullW = frame.SourceSize.Width > 0 ? (int)frame.SourceSize.Width : Viewport.Bitmap.PixelSize.Width;
        var fullH = frame.SourceSize.Height > 0 ? (int)frame.SourceSize.Height : Viewport.Bitmap.PixelSize.Height;
        return ShowPrintDialogAsync(path, Viewport.Bitmap, disposePreviewAfter: false, fullW, fullH);
    }

    /// <summary>Browser item Print…: prints that file without disturbing the current view.</summary>
    private async Task PrintFileAsync(string path)
    {
        if (!PrintService.IsSupported || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        Bitmap? preview = null;
        var disposePreview = false;
        try
        {
            if (string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase) && Viewport.Bitmap is not null)
            {
                var frame = Viewport.PresentedFrame;
                var w = frame.SourceSize.Width > 0 ? (int)frame.SourceSize.Width : Viewport.Bitmap.PixelSize.Width;
                var h = frame.SourceSize.Height > 0 ? (int)frame.SourceSize.Height : Viewport.Bitmap.PixelSize.Height;
                await ShowPrintDialogAsync(path, Viewport.Bitmap, disposePreviewAfter: false, w, h);
                return;
            }
            // Decode a small bounded preview for the dialog; the spool path loads full resolution.
            preview = await Task.Run(() =>
            {
                try
                {
                    if (!ImageHeaderProbe.TryProbe(path, out var dims) || !dims.IsValid)
                        return null;
                    var longest = Math.Max(1, Math.Min(900, dims.LongestSide));
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                    var backend = new AvaloniaImageDecoderBackend();
                    return backend.DecodePreview(stream, dims, longest, BitmapInterpolationMode.MediumQuality, path);
                }
                catch { return null; }
            });
            if (preview is null) return;
            disposePreview = true;
            if (!ImageHeaderProbe.TryProbe(path, out var full) || !full.IsValid)
                full = new ImageDimensions(preview.PixelSize.Width, preview.PixelSize.Height);
            await ShowPrintDialogAsync(path, preview, disposePreviewAfter: true, full.Width, full.Height);
            disposePreview = false; // dialog took effect; ownership transferred below
        }
        finally
        {
            if (disposePreview) preview?.Dispose();
        }
    }

    private async Task ShowPrintDialogAsync(string path, Bitmap previewBitmap, bool disposePreviewAfter, int fullWidthPx, int fullHeightPx)
    {
        Bitmap? ownedPreview = disposePreviewAfter ? previewBitmap : null;
        try
        {
            var (dpiX, dpiY, _) = await Task.Run(() => PrintDpiReader.GetDpi(path));
            var args = new PrintWindowArgs(
                path, previewBitmap,
                Math.Max(1, fullWidthPx), Math.Max(1, fullHeightPx),
                dpiX, dpiY,
                ct => ResolveFullPrintBitmapAsync(path, ct));
            var window = new PrintWindow(args) { Topmost = Topmost };
            _overlays?.SetModalSuppressed(true);
            try
            {
                await ShowOwnedDialogAsync<bool>(window);
            }
            finally
            {
                _overlays?.SetModalSuppressed(false);
            }
            _diagnostics.Write("print", "dialog_closed", new { path });
        }
        catch (Exception ex)
        {
            _diagnostics.Write("print", "dialog_failed", new { path, error = ex.GetType().Name, ex.Message });
        }
        finally
        {
            ownedPreview?.Dispose();
        }
    }

    /// <summary>
    /// Loads the authoritative full-resolution frame for spooling. Never the viewport-sized
    /// preview: the dialog preview bitmap may be a downscaled first frame.
    /// </summary>
    private async Task<(Bitmap Bitmap, int WidthPx, int HeightPx)> ResolveFullPrintBitmapAsync(string path, CancellationToken ct)
    {
        var result = await _loader.LoadFullForegroundAsync(path, ct, cacheResult: false);
        if (result?.Bitmap is null)
            throw new InvalidOperationException("Could not decode the image at full resolution.");
        var w = result.SourceWidth > 0 ? result.SourceWidth : result.Bitmap.PixelSize.Width;
        var h = result.SourceHeight > 0 ? result.SourceHeight : result.Bitmap.PixelSize.Height;
        return (result.Bitmap, w, h);
    }

    /// <summary>
    /// Diagnostic-only capture of the Print window (used by --capture-print-dialog). Opens the real
    /// dialog so its printer query, live preview and chrome are exercised, captures the pixels, exits.
    /// </summary>
    private async Task CapturePrintDialogForDiagnosticsAsync(string image, string output)
    {
        try
        {
            if (!string.Equals(_currentPath, image, StringComparison.OrdinalIgnoreCase))
            {
                try { await OpenAsImageTabAsync(image); await Task.Delay(700); } catch { }
            }
            if (Viewport.Bitmap is not { } bitmap)
            {
                Console.Error.WriteLine("capture_print_dialog: no bitmap presented");
                return;
            }
            var frame = Viewport.PresentedFrame;
            var w = frame.SourceSize.Width > 0 ? (int)frame.SourceSize.Width : bitmap.PixelSize.Width;
            var h = frame.SourceSize.Height > 0 ? (int)frame.SourceSize.Height : bitmap.PixelSize.Height;
            var (dpiX, dpiY, _) = await Task.Run(() => PrintDpiReader.GetDpi(image));
            var args = new PrintWindowArgs(image, bitmap, w, h, dpiX, dpiY,
                ct => ResolveFullPrintBitmapAsync(image, ct));
            var window = new PrintWindow(args) { Topmost = false };
            window.Show(this);
            await Task.Delay(3500); // printer enumeration + preview settle
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            var ok = UiDiagnosticCapture.TryCapture(window, output, out var error);
            _diagnostics.Write("print", "capture_dialog", new { ok, output, error });
            window.Close();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("capture_print_dialog failed: " + ex);
        }
        finally
        {
            // Diagnostic capture must terminate even with Speed Boost standby enabled.
            ForceExit();
        }
    }

    private async Task CopyTextAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    internal void RestoreFromMinimizedIfNeeded()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _stateBeforeMinimize == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        EnsureNativeMaximizedState();
    }

    private bool TryGetNativeZoomed(out bool zoomed)
    {
        zoomed = false;
        if (!OperatingSystem.IsWindows()) return false;
        var handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return false;
        zoomed = IsZoomed(handle.Handle);
        return true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    private void EnsureNativeMaximizedState()
    {
        if (!OperatingSystem.IsWindows() || !IsVisible || WindowState != WindowState.Maximized) return;
        _windowsBrowserFrame?.RefreshForWarmRestore();
        // WindowState can already be Maximized here even when the native HWND was restored by a
        // Hide/Show or pre-show state assignment. SC_MAXIMIZE is idempotent for a truly maximized
        // HWND and repairs that split-brain state for the warm-start path.
        TrySendNativeSystemCommand(ScMaximize);
    }
}
