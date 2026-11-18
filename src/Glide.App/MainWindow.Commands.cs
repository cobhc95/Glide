using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Diagnostics;
using Glide.App.Explorer;
using Glide.App.Platform;
using Glide.App.Printing;
using Glide.App.Settings;
using Glide.App.Services;
using Glide.Core;
using Glide.Core.Commands;
using Glide.Core.Navigation;
using Glide.Core.Settings;
using Glide.Core.Workspace;
using Glide.Imaging;
using Glide.Imaging.Encoders;

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
        // Mouse side buttons (XButton1 back / XButton2 forward) are gesture inputs of the viewer, Tabs and Explorer areas.
        // Default leaves the press untouched (it falls through exactly as before); a binding handles it here.
        if (TryRouteSideButtonGesture(e)) return;
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
        var action = _inputRouter.ResolveShortcutAction(shortcut, _settings.Hotkeys);
        if (action is null) return;
        var command = action.Command;
        _diagnostics.Write("input", "mouse_hotkey_command", new { action = action.Id, command = command.ToString(), shortcut });
        e.Handled = true;
        if (action.Kind == AppActionKind.Command && (command == GlideCommand.PreviousImage || command == GlideCommand.NextImage))
        {
            await NavigateMouseAsync(command == GlideCommand.NextImage ? 1 : -1);
            return;
        }
        await ExecuteActionAsync(action, "mouse_hotkey");
    }

    /// <summary>
    /// Maps a mouse side-button press to its gesture slot by what is under the pointer: a tab (Tabs), the Explorer tab's
    /// page (Explorer), or the image viewer (Windowed / Fullscreen image). Null where the buttons have no slot.
    /// <paramref name="tabId"/> is set when the press is on a tab.
    /// </summary>
    internal GestureSlotDefinition? SideButtonSlotAt(Visual? source, bool back, out Guid? tabId)
    {
        tabId = null;
        var input = back ? GestureInputs.BackButton : GestureInputs.ForwardButton;
        if (source is null) return null;
        bool Within(Visual host) => ReferenceEquals(source, host) || source.GetVisualAncestors().Any(a => ReferenceEquals(a, host));
        var onTabStrip = Within(TabStripScroll);
        if (onTabStrip)
        {
            for (Visual? v = source; v is not null && !ReferenceEquals(v, TabStripScroll); v = v.GetVisualParent())
                if (v is Border { DataContext: Guid id }) { tabId = id; break; }
        }
        // The Welcome / Home page has no gesture rows of its own: it follows the Windowed image rows, so the side buttons
        // go Back / Forward there too (after Back to Home, the forward button returns to the image).
        var area = GestureResolver.SideButtonArea(
            onTabStrip,
            onVisibleExplorer: _workspace.Active is BrowserTabState && BrowserView.IsVisible && Within(BrowserView),
            onVisibleImage: ImageView.IsVisible && Within(Viewport),
            onVisibleHome: _workspace.Active is HomeTabState && HomeHost.IsVisible && Within(HomeHost),
            fullscreen: WindowState == WindowState.FullScreen);
        return area is null ? null : GestureCatalog.SlotFor(area, input);
    }

    private bool TryRouteSideButtonGesture(PointerPressedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return false;
        var slot = SideButtonSlotAt(e.Source as Visual, kind == PointerUpdateKind.XButton1Pressed, out var tabId);
        if (slot is null) return false;
        // 5.0.8: Default on a side-button row is pass-through ("not used by Glide"): the press is NOT marked handled, so
        // Windows / the mouse software keep it. Only an explicit choice ("Do nothing" blocks; any action runs) claims it.
        var decision = GestureResolver.ForSideButton(slot.Id, ResolveGestureSlot(slot.Id, e.KeyModifiers));
        if (!decision.Runs) return false;
        var action = decision.Action;
        e.Handled = true;
        _diagnostics.Write("input", "side_button_gesture", new { slot = slot.Id, action });
        _ = RunSideButtonGestureAsync(slot.Id, action, tabId);
        return true;
    }

    private async Task RunSideButtonGestureAsync(string slotId, string action, Guid? tabId)
    {
        try
        {
            if (tabId is { } id) await ExecuteTabGestureAsync(id, action, "side_button");
            else await ExecuteGestureActionAsync(action, 0, slotId);
        }
        catch (OperationCanceledException) { /* cancellation is the expected way to stop this work */ }
        catch (Exception ex)
        {
            _diagnostics.Write("input", "side_button_gesture_failed", new { slot = slotId, error = ex.GetType().Name, ex.Message });
        }
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
        if (e.Handled) return;
        // The "Ask me each time" end-of-folder picker owns the keyboard while it is open (before any hotkey routing).
        if (_endOfFolderPicker is not null)
        {
            EndOfFolderPickerKeyDown(sender, e);
            return;
        }
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl+Tab must be claimed on the tunnel route (Avalonia's focus navigation consumes Tab before the
            // bubble route), but it is resolved through the hotkey map like every other key: it used to be
            // hardwired to Next/Previous tab, ignoring a rebinding in Settings.
            var tabAction = _inputRouter.ResolveShortcutAction(KeyboardShortcutText.From(e), _settings.Hotkeys);
            if (tabAction is null) return;
            e.Handled = true;
            _diagnostics.Write("input", "global_tab_cycle", new { action = tabAction.Id });
            await ExecuteActionAsync(tabAction, "hotkey");
            return;
        }

        // A toolbar / status-bar button keeps keyboard focus after it is clicked, and a focused Button consumes
        // Enter and Space before the window's hotkey handler (bubble route) sees them: after clicking e.g. Fit,
        // Space re-clicked Fit instead of going to the next image, and Enter did not toggle fullscreen. Unless the
        // user enabled Tab focus navigation (then Enter/Space on a focused button is the expected keyboard click),
        // route these keys through the hotkey map first.
        if (!_settings.EnableTabFocusNavigation && e.Key is Key.Enter or Key.Space &&
            e.Source is Button && e.Source is not TextBox && !IsInsideOpenMenu(e.Source))
        {
            await RouteHotkeyAsync(e);
            return;
        }

        if (!_settings.EnableTabFocusNavigation && e.Key == Key.Tab && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // When Tab focus navigation is disabled, suppress Tab and Shift+Tab from navigating focus through UI buttons
            // unless active input is inside a text box.
            if (e.Source is not TextBox)
            {
                var shortcut = KeyboardShortcutText.From(e);
                var mapped = _inputRouter.ResolveShortcut(shortcut, _settings.Hotkeys);
                if (mapped == GlideCommand.None)
                {
                    e.Handled = true;
                }
            }
        }
    }

    private bool _slideshowSpaceDown;

    private static bool IsInsideOpenMenu(object? source)
    {
        for (var visual = source as Avalonia.Visual; visual is not null; visual = Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual))
            if (visual is MenuItem or Avalonia.Controls.ContextMenu) return true;
        return false;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e) => await RouteHotkeyAsync(e);

    /// <summary>
    /// The one keyboard entry point: key press -> canonical shortcut text -> hotkey map -> registry action ->
    /// <see cref="ExecuteActionAsync"/>. Called from the window's bubble KeyDown and, for keys a focused button
    /// would otherwise swallow, from the tunnel handler.
    /// </summary>
    private async Task RouteHotkeyAsync(KeyEventArgs e)
    {
        try
        {
            // Typing into a text box of the window (Explorer address bar / filter / inline rename) must never fire
            // single-key viewer hotkeys such as F (fullscreen) or R (rotate). Modified shortcuts and F-keys still work.
            if (e.Source is TextBox && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0 &&
                !(e.Key >= Key.F1 && e.Key <= Key.F24) && e.Key is not (Key.Escape or Key.Tab))
                return;

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

            var shortcut = KeyboardShortcutText.From(e);
            var action = _inputRouter.ResolveShortcutAction(shortcut, _settings.Hotkeys);
            if (action is null) return;
            // Back / Forward never fire from a text box (Explorer address bar, filter, inline rename), whatever key they
            // are bound to: Backspace / Shift+Backspace edit text, and Alt+Left / Alt+Right must not leave the folder
            // while typing. (Unmodified keys are already stopped above; this also covers the Alt and Shift chords.)
            if (e.Source is TextBox && action.Command is GlideCommand.NavigateBack or GlideCommand.NavigateForward or GlideCommand.BackOrPrevious or GlideCommand.BrowserBack or GlideCommand.BrowserForward)
                return;
            if (action.Kind != AppActionKind.Command)
            {
                _diagnostics.Write("input", "command", new { action = action.Id, shortcut });
                e.Handled = true;
                await ExecuteActionAsync(action, "hotkey");
                return;
            }
            var command = action.Command;
            if (command is GlideCommand.NextImage or GlideCommand.PreviousImage && !IsActionAvailableHere(action, "hotkey"))
            {
                // Next / previous belong to the image viewer: never browse a hidden tab's folder from Explorer or Home.
                NotifyActionRouted(action, "hotkey");
                return;
            }

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
            NotifyActionRouted(action, "hotkey");
            var acceptedBefore = _acceptedKeyboardNavigations;
            if (!_heldNavigationKeyIsDown || _heldNavigationDirection != direction)
                StartHeldNavigation(direction);
            // The first step of a fresh press runs synchronously inside StartHeldNavigation; auto-repeat key-downs while
            // the key is held start nothing. Either way the sticky-key detector hears about this key-down.
            NoteNavigationKeyDown(e.Key, acceptedBefore);
            e.Handled = true;
            return;
        }

        if (command == GlideCommand.PreviousImage || command == GlideCommand.NextImage)
        {
            e.Handled = true;
            NotifyActionRouted(action, "hotkey");
            var acceptedBefore = _acceptedKeyboardNavigations;
            var navigation = NavigateKeyboardAsync(command == GlideCommand.NextImage ? 1 : -1);
            NoteNavigationKeyDown(e.Key, acceptedBefore);
            await navigation;
            return;
        }

        _diagnostics.Write("input", "command", new { command = command.ToString(), shortcut });
        // Mark the key handled before awaiting: an async command must not let the same key bubble on.
        e.Handled = true;
        await ExecuteActionAsync(action, "hotkey");
        }
        catch (OperationCanceledException) { /* cancellation is the expected way to stop this work */ }
        catch (Exception ex)
        {
            _diagnostics.Write("input", "key_command_failed", new { key = e.Key.ToString(), error = ex.GetType().Name, ex.Message });
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        NoteNavigationKeyUp(e.Key);
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
        NoteWheelNavigationForSkipDetector();

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
        catch (OperationCanceledException) { /* cancellation is the expected way to stop this work */ }
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
            // Only the steps that keep the user browsing (a folder in this tab, a loop) carry held playback on; a new tab,
            // Home or the "Ask me each time" picker stops it and runs once as a fresh navigation.
            var endStep = EndOfFolderStepFor(delta);
            if (endStep.Kind == EndOfFolderStepKind.Stay) return false;
            if (!EndOfFolderPolicy.ContinuesHeldNavigation(endStep))
            {
                Dispatcher.UIThread.Post(async () => await ExecuteEndOfFolderStepSerializedAsync(endStep, Math.Sign(delta)));
                return false;
            }
            if (!await ExecuteEndOfFolderStepAsync(endStep, Math.Sign(delta), rapidPreviewOnly: true)) return false;
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

    /// <summary>Resolves a gesture slot for an input made while <paramref name="modifiers"/> were held
    /// (custom gestures first, then the slot binding).</summary>
    private string ResolveGestureSlot(string slotId, KeyModifiers modifiers) =>
        GestureCatalog.ResolveAction(slotId, _settings.Gestures, _settings.CustomGestures,
            GestureModifiers.From(modifiers.HasFlag(KeyModifiers.Control), modifiers.HasFlag(KeyModifiers.Shift), modifiers.HasFlag(KeyModifiers.Alt)));

    private async Task ExecuteGestureActionAsync(string actionId, int wheelStep = 0, string? slotId = null)
    {
        actionId = GestureCatalog.NormalizeAction(actionId);
        if (actionId is GestureCatalog.Default or GestureCatalog.Nothing or GestureCatalog.PassThrough) return;
        // Generated setting toggles (and any other registry action that is not a plain command) run through the
        // same registry entry point as their hotkey.
        if (AppActionRegistry.FindByGestureId(actionId) is { IsSettingAction: true } settingAction)
        {
            await ExecuteActionAsync(settingAction, "gesture");
            return;
        }
        // Slideshow rows keep the exact 4.7 "Left-click during slideshow" semantics for next /
        // previous (dispatched as commands, not as rate-limited mouse navigation).
        if (slotId is not null && slotId.StartsWith("slideshow.", StringComparison.Ordinal) &&
            actionId is GestureCatalog.Next or GestureCatalog.Previous)
        {
            await ExecuteCommandAsync(actionId == GestureCatalog.Next ? GlideCommand.NextImage : GlideCommand.PreviousImage);
            return;
        }
        switch (actionId)
        {
            case GestureCatalog.ContextMenu:
                BuildViewerContextMenu().Open(Viewport);
                return;
            case GestureCatalog.ClearSelection:
                Viewport.ClearSelection();
                return;
            case GestureCatalog.MaximizeRestore:
                ToggleMaximizeRestoreFromGesture();
                return;
            case GestureCatalog.MinimizeWindow:
                WindowState = WindowState.Minimized;
                return;
            case GestureCatalog.OpenInCurrentTab or GestureCatalog.OpenInNewTab:
                return; // Explorer item actions are handled by the Explorer tab itself.
        }
        if (await ExecuteWheelGestureAsync(actionId, wheelStep)) return;
        var command = GestureCatalog.CommandForAction(actionId);
        if (command == GlideCommand.PreviousImage || command == GlideCommand.NextImage)
            await NavigateMouseAsync(command == GlideCommand.NextImage ? 1 : -1);
        else if (command != GlideCommand.None)
            await ExecuteCommandAsync(command);
    }

    /// <summary>Direction-aware wheel actions raised outside the viewport's own wheel handling
    /// (tab strip, title bar, Explorer) or the pairs the viewport hands up. Step: -1 up, +1 down.</summary>
    private async Task<bool> ExecuteWheelGestureAsync(string actionId, int step)
    {
        if (step == 0) return false;
        switch (actionId)
        {
            case GestureCatalog.ZoomWheel: Viewport.ZoomBy(step < 0 ? Viewport.WheelZoomFactor : 1 / Viewport.WheelZoomFactor); return true;
            case GestureCatalog.ZoomWheelReversed: Viewport.ZoomBy(step < 0 ? 1 / Viewport.WheelZoomFactor : Viewport.WheelZoomFactor); return true;
            case GestureCatalog.BrowseWheel: QueueWheelNavigation(step); return true;
            case GestureCatalog.BrowseWheelReversed: QueueWheelNavigation(-step); return true;
            case GestureCatalog.PanWheel: Viewport.PanByWheel(-step, horizontal: false); return true;
            case GestureCatalog.PanWheelHorizontal: Viewport.PanByWheel(-step, horizontal: true); return true;
            case GestureCatalog.JumpWheel: await ExecuteCommandAsync(step > 0 ? GlideCommand.JumpForward10 : GlideCommand.JumpBack10); return true;
            case GestureCatalog.FolderWheel: await ExecuteCommandAsync(step > 0 ? GlideCommand.NextFolder : GlideCommand.PreviousFolder); return true;
            case GestureCatalog.RotateWheel: await ExecuteCommandAsync(step > 0 ? GlideCommand.RotateRight : GlideCommand.RotateLeft); return true;
            case GestureCatalog.TabWheel: await CycleTabAsync(step > 0 ? 1 : -1); return true;
        }
        return false;
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

        if (HomePageIsExplorer)
        {
            var homeFolder = ResolveHomeBrowserFolder();
            var browser = _workspace.Tabs.OfType<BrowserTabState>()
                .FirstOrDefault(t => string.Equals(t.Folder, homeFolder, StringComparison.OrdinalIgnoreCase));
            var target = browser ?? CreateConfiguredHomeDestination();
            _workspace.Select(target.Id);
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
        try
        {
            if (!_commands.CanExecute(command))
                _diagnostics.Write("input", "command_unmapped", new { command = command.ToString() });
            await _commands.ExecuteAsync(command).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* cancellation is the expected way to stop this work */ }
        catch (Exception ex)
        {
            _diagnostics.Write("input", "command_failed", new { command = command.ToString(), error = ex.GetType().Name, ex.Message });
        }
    }

    /// <summary>Raised for every registry action a hotkey, gesture, button or menu item routes to (tests and diagnostics).</summary>
    internal event Action<AppAction, string>? ActionRouted;

    private void NotifyActionRouted(AppAction action, string source) => ActionRouted?.Invoke(action, source);

    /// <summary>
    /// The single execution entry point for master-registry actions: commands go to the command dispatcher,
    /// generated setting actions flip / cycle their setting through the normal settings pipeline.
    /// </summary>
    /// <summary>Test seam: when it returns true the routed action is recorded but not executed (lets the hotkey sweep
    /// press every default key without opening pickers, printing, deleting files or closing the window).</summary>
    internal Func<AppAction, string, bool>? ActionInterceptorForTests { get; set; }

    internal async Task ExecuteActionAsync(AppAction action, string source)
    {
        NotifyActionRouted(action, source);
        if (!IsActionAvailableHere(action, source)) return;
        if (ActionInterceptorForTests?.Invoke(action, source) == true) return;
        switch (action.Kind)
        {
            case AppActionKind.SettingToggle:
            case AppActionKind.SettingCycle:
                ChangeSetting(action);
                break;
            case AppActionKind.Command:
                await ExecuteCommandAsync(action.Command);
                break;
            default:
                // Gesture-only and fixed-shortcut entries are owned by their surface; nothing to run here.
                _diagnostics.Write("input", "action_not_invokable", new { action = action.Id, source });
                break;
        }
    }

    /// <summary>
    /// Viewer-only actions (registry context Viewer: the current image's file, pixels, view, folder, overlays) run only
    /// while the image viewer is showing. In an Explorer or Home tab the viewer still holds the image of another tab;
    /// a key pressed there (focus outside the Explorer list) used to delete, rename, rotate or copy that hidden image.
    /// Menus are built for their own surface, so menu clicks are not gated.
    /// </summary>
    private bool IsActionAvailableHere(AppAction action, string source)
    {
        if (action.Contexts != AppActionContexts.Viewer || source == "menu" || ImageView.IsVisible) return true;
        _diagnostics.Write("input", "action_out_of_context", new { action = action.Id, source, active = _workspace.Active?.Kind.ToString() });
        return false;
    }

    /// <summary>
    /// Flips (Toggle) or advances (Choice) a catalogued setting exactly like editing it in Settings and pressing
    /// Apply: the new state goes through ApplySettingsFromDialog(commit: true), which applies it live, persists it
    /// and publishes it to the other windows.
    /// </summary>
    private void ChangeSetting(AppAction action)
    {
        if (action.SettingId is not { } settingId || SettingAccessors.Find(settingId) is not { } accessor)
        {
            _diagnostics.Write("settings", "setting_action_unmapped", new { action = action.Id });
            return;
        }
        var state = _settings.CloneState();
        object? value;
        if (action.Kind == AppActionKind.SettingToggle && accessor.GetBool is { } getBool && accessor.SetBool is { } setBool)
        {
            var next = !getBool(state);
            setBool(state, next);
            value = next;
        }
        else if (action.Kind == AppActionKind.SettingCycle && accessor.GetText is { } getText && accessor.SetText is { } setText)
        {
            var next = SettingActionCatalog.NextChoice(settingId, getText(state));
            setText(state, next);
            value = next;
        }
        else
        {
            _diagnostics.Write("settings", "setting_action_kind_mismatch", new { action = action.Id });
            return;
        }
        SettingAccessors.NormalizeDerived(state, settingId);
        ApplySettingsFromDialog(state, commit: true);
        if (string.Equals(settingId, "status.visible", StringComparison.OrdinalIgnoreCase))
        {
            // Same as the viewer menu's "Show status bar": showing it again also clears a collapsed / session-closed bar.
            _statusCollapsed = false;
            _statusSessionClosed = false;
            NotifyStatusChromeChanged();
        }
        _diagnostics.Write("settings", "setting_action_applied", new { action = action.Id, setting = settingId, value });
    }

    internal IReadOnlyCollection<GlideCommand> ProductionMappedCommandsForTests() => _commands.MappedCommands;

    private MainWindowCommandDispatcher CreateCommandDispatcher()
    {
        // Commands call the same methods the buttons do (no fake click events). Fire keeps the old fire-and-forget timing of the
        // async click handlers: the command completes at once and an exception surfaces like an async void handler's.
        static Task Run(Action action) { action(); return Task.CompletedTask; }
        static Task Fire(Func<Task> action) { FireAndForget(action); return Task.CompletedTask; }
        static async void FireAndForget(Func<Task> action) => await action();
        var actions = new Dictionary<GlideCommand, Func<Task>>
        {
            [GlideCommand.OpenFile] = () => Fire(OpenImageFromPickerAsync), [GlideCommand.OpenFolder] = () => Fire(OpenFolderFromPickerAsync),
            [GlideCommand.RefreshFolder] = async () => { if (_workspace.Active is BrowserTabState b) NavigateBrowserTo(b.Folder, false); else if (_currentPath is not null) await LoadPathAsync(_currentPath, true); },
            [GlideCommand.Home] = NavigateHomeAsync,
            [GlideCommand.ToggleLoopFolder] = ToggleLoopFolderAsync,
            [GlideCommand.PreviousImage] = () => NavigateAsync(-1), [GlideCommand.NextImage] = () => NavigateAsync(1),
            [GlideCommand.FirstImage] = () => NavigateEdgeAsync(-1), [GlideCommand.LastImage] = () => NavigateEdgeAsync(1),
            [GlideCommand.PreviousFolder] = () => RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(-1)), [GlideCommand.NextFolder] = () => RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(1)),
            // "Explore/Browse containing folder" (hotkey folder.exploreParent). It used to be an empty stub, which is why the
            // action did nothing. It is the same operation as the title-bar Show in Explorer button and follows its target setting.
            [GlideCommand.ExploreParentFolder] = OpenContainingFolderOrAlternativeAsync,
            // The five fit / size commands follow "Fit control scope" exactly like the status bar buttons: same code path (ApplyStatusBarFit).
            [GlideCommand.FitImage] = () => { ApplyStatusBarFit("Fit image", Viewport.Fit); return Task.CompletedTask; }, [GlideCommand.FitWidth] = () => { ApplyStatusBarFit("Fit width", Viewport.FitWidth); return Task.CompletedTask; }, [GlideCommand.FitHeight] = () => { ApplyStatusBarFit("Fit height", Viewport.FitHeight); return Task.CompletedTask; }, [GlideCommand.FitWindow] = () => { ApplyStatusBarFit("Fit to window", Viewport.FitWindow); return Task.CompletedTask; }, [GlideCommand.ActualSize] = () => { ApplyStatusBarFit("100%", Viewport.ActualSize); return Task.CompletedTask; },
            [GlideCommand.ToggleFitActual] = () => { if (Viewport.ZoomPercent == 100) Viewport.Fit(); else Viewport.ActualSize(); return Task.CompletedTask; }, [GlideCommand.ZoomIn] = () => { Viewport.ZoomStep(true); return Task.CompletedTask; }, [GlideCommand.ZoomOut] = () => { Viewport.ZoomStep(false); return Task.CompletedTask; },
            [GlideCommand.RotateLeft] = () => { Viewport.RotateLeft(); return Task.CompletedTask; }, [GlideCommand.RotateRight] = () => { Viewport.RotateRight(); return Task.CompletedTask; }, [GlideCommand.FlipHorizontal] = () => { Viewport.FlipHorizontal(); return Task.CompletedTask; }, [GlideCommand.FlipVertical] = () => { Viewport.FlipVertical(); return Task.CompletedTask; },
            [GlideCommand.NewTab] = async () => { CreateConfiguredNewTabDestination(); await ActivateWorkspaceAsync(); }, [GlideCommand.NewWindowSameImage] = OpenCurrentInNewWindowAsync, [GlideCommand.DuplicateSessionWindow] = DuplicateSessionWindowAsync, [GlideCommand.CloseTab] = CloseActiveTabAsync, [GlideCommand.RestoreClosedTab] = RestoreClosedTabAsync, [GlideCommand.DuplicateTab] = DuplicateActiveTabAsync,
            [GlideCommand.NextTab] = () => CycleTabAsync(1), [GlideCommand.PreviousTab] = () => CycleTabAsync(-1),
            [GlideCommand.SelectTab1] = () => SelectTabIndexAsync(0), [GlideCommand.SelectTab2] = () => SelectTabIndexAsync(1), [GlideCommand.SelectTab3] = () => SelectTabIndexAsync(2), [GlideCommand.SelectTab4] = () => SelectTabIndexAsync(3), [GlideCommand.SelectTab5] = () => SelectTabIndexAsync(4), [GlideCommand.SelectTab6] = () => SelectTabIndexAsync(5), [GlideCommand.SelectTab7] = () => SelectTabIndexAsync(6), [GlideCommand.SelectTab8] = () => SelectTabIndexAsync(7), [GlideCommand.SelectLastTab] = () => SelectTabIndexAsync(Math.Max(0, _workspace.Tabs.Count - 1)),
            [GlideCommand.DetachTab] = async () => { if (_workspace.Active is { } a) await DetachTabAsync(a.Id, this.PointToScreen(new Point(Bounds.Width / 2, 48)), fromMenu: true); }, [GlideCommand.CloseWindow] = () => { Close(); return Task.CompletedTask; }, [GlideCommand.ExitApplication] = ExitGlideAsync, [GlideCommand.ToggleFullscreen] = () => { ToggleFullscreen(); return Task.CompletedTask; }, [GlideCommand.StartPauseSlideshow] = ToggleSlideshowAsync, [GlideCommand.StopSlideshow] = () => { StopSlideshow(true); return Task.CompletedTask; },
            [GlideCommand.ToggleImageInfo] = () => Run(ToggleImageInfoPanel), [GlideCommand.ClearSelection] = HandleEscapeAsync, [GlideCommand.Settings] = () => Fire(OpenSettingsAsync), [GlideCommand.AddOverlay] = () => Fire(AddOverlayFromPickerAsync), [GlideCommand.SaveOverlayLayout] = () => Fire(SaveOverlayLayoutAsync), [GlideCommand.LoadOverlayLayout] = () => Fire(LoadOverlayLayoutAsync), [GlideCommand.ResetSelectedOverlayZoom] = () => { EnsureOverlays().ResetSelectedZoom(); return Task.CompletedTask; }, [GlideCommand.BringSelectedOverlayFront] = () => { EnsureOverlays().BringSelectedToFront(); return Task.CompletedTask; },
            [GlideCommand.ToggleAlwaysOnTop] = () => Run(ToggleAlwaysOnTopSetting), [GlideCommand.ToggleTransparency] = () => Run(ToggleTransparencyPanel), [GlideCommand.ShowContextMenu] = () => { ShowViewerContextMenu(); return Task.CompletedTask; }, [GlideCommand.BrowserBack] = () => Fire(BrowserBackAsync), [GlideCommand.BrowserForward] = () => Fire(BrowserForwardAsync), [GlideCommand.BrowserUp] = () => Run(NavigateBrowserUp),
            [GlideCommand.CopyImage] = () => { CopyImagePixels(); return Task.CompletedTask; }, [GlideCommand.ExportSelection] = ExportSelectionAsync, [GlideCommand.CopyFile] = () => { CopyImageFile(); return Task.CompletedTask; }, [GlideCommand.CopyFileName] = () => CopyTextAsync(_currentPath is null ? null : Path.GetFileName(_currentPath)), [GlideCommand.CopyFolderPath] = () => CopyTextAsync(_currentPath is null ? null : Path.GetDirectoryName(_currentPath)), [GlideCommand.CopyFullPath] = () => CopyTextAsync(_currentPath), [GlideCommand.RenameFile] = RenameCurrentFileAsync, [GlideCommand.DeleteFile] = DeleteCurrentFileAsync, [GlideCommand.OpenContainingFolder] = OpenContainingFolderOrAlternativeAsync, [GlideCommand.Print] = PrintCurrentImageAsync, [GlideCommand.SaveImage] = SaveCurrentImageAsync, [GlideCommand.SaveImageAs] = SaveCurrentImageAsAsync, [GlideCommand.ExternalProgram1] = () => { LaunchExternal(1); return Task.CompletedTask; }, [GlideCommand.ExternalProgram2] = () => { LaunchExternal(2); return Task.CompletedTask; }, [GlideCommand.ExternalProgram3] = () => { LaunchExternal(3); return Task.CompletedTask; }
        };
        actions[GlideCommand.JumpForward10] = () => NavigateMouseAsync(10);
        actions[GlideCommand.JumpBack10] = () => NavigateMouseAsync(-10);
        actions[GlideCommand.ZoomTo200] = () => { Viewport.ZoomTo(2); return Task.CompletedTask; };
        actions[GlideCommand.GifPlayPause] =() => { _gifPlayer?.TogglePause(); return Task.CompletedTask; };
        actions[GlideCommand.GifPreviousFrame] = () => { _gifPlayer?.Step(-1); return Task.CompletedTask; };
        actions[GlideCommand.GifNextFrame] = () => { _gifPlayer?.Step(1); return Task.CompletedTask; };
        actions[GlideCommand.ToggleStatusSurface] = () => { ToggleStatusSurfaceCommand(); return Task.CompletedTask; };
        // 5.0.x registry: features that were reachable only from a button, menu item or gesture now have a
        // command (and therefore a hotkey row, a gesture entry and Settings search) of their own.
        actions[GlideCommand.MaximizeRestoreWindow] = () => { ToggleMaximizeRestoreFromGesture(); return Task.CompletedTask; };
        actions[GlideCommand.MinimizeWindow] = () => { WindowState = WindowState.Minimized; return Task.CompletedTask; };
        actions[GlideCommand.ClearOverlays] = () => Run(ClearAllOverlays);
        actions[GlideCommand.CloseStatusBar] = () => Run(CloseStatusSurface);
        actions[GlideCommand.NewExplorerTab] = () => OpenExplorerTabAsync(_currentPath is { } current && File.Exists(current) ? Path.GetDirectoryName(current) : null);
        actions[GlideCommand.NewHomeTab] = async () => { AddNewHomeTab(); RebuildTabStrip(); await ActivateWorkspaceAsync(); };
        actions[GlideCommand.OpenFirstImageInFolder] = () => _workspace.Active is BrowserTabState browserTab ? OpenFirstImageInFolderAsync(browserTab.Folder) : Task.CompletedTask;
        actions[GlideCommand.ToggleOverlayMode] = () => { if (_wholeAppOverlayMode) ExitWholeAppOverlayMode(); else EnterWholeAppOverlayMode(); return Task.CompletedTask; };
        actions[GlideCommand.CustomizeTitleBar] = ShowTitleBarCustomizerAsync;
        actions[GlideCommand.CustomizeStatusBar] = ShowStatusBarCustomizerAsync;
        actions[GlideCommand.TabToolTipLarger] = () => { StepTabToolTipTextSize(1); return Task.CompletedTask; };
        actions[GlideCommand.TabToolTipSmaller] = () => { StepTabToolTipTextSize(-1); return Task.CompletedTask; };
        actions[GlideCommand.ShowFolderInWindowsExplorer] = () => { ShowFolderInWindowsExplorer(); return Task.CompletedTask; };
        actions[GlideCommand.ShowInAlternativeFileManager] = () => { ShowInAlternativeFileManager(); return Task.CompletedTask; };
        actions[GlideCommand.ResetTransparency] = () => Run(ResetWindowTransparency);
        actions[GlideCommand.ClearRecentHistory] = () => Run(ClearRecentHistoryList);
        actions[GlideCommand.CycleRecentHistorySize] = () => Run(CycleRecentHistoryTileSize);
        // 5.0.0 browser-style tab history (MainWindow.History.cs). explorer.back / explorer.forward (BrowserBack /
        // BrowserForward) run the same steps from an Explorer tab.
        actions[GlideCommand.NavigateBack] = NavigateBackAsync;
        actions[GlideCommand.NavigateForward] = NavigateForwardAsync;
        actions[GlideCommand.BackOrPrevious] = BackOrPreviousAsync;
        // 5.0.1 update checker: "Check for updates now" (MainWindow.Updates.cs).
        actions[GlideCommand.CheckForUpdates] = CheckForUpdatesNowAsync;
        // 5.0.4 Back up and restore (MainWindow.Backup.cs): Settings at the card, then its Export / Import flow.
        actions[GlideCommand.ExportGlideBackup] = () => OpenBackupFromCommandAsync(import: false);
        actions[GlideCommand.ImportGlideBackup] = () => OpenBackupFromCommandAsync(import: true);
        MainWindowCommandDispatcher.AssertComplete(actions.Keys);
        return new MainWindowCommandDispatcher(actions);
    }

    /// <summary>
    /// "Show / hide status surface" (status.toggle): the keyboard twin of the status bar's collapse / expand
    /// button. A closed bar (X / Settings) is shown again; otherwise the bar collapses or expands. With
    /// Auto-hide on the collapse flag is ignored by design (see StatusCollapsedNow), so the command only flips
    /// the remembered flag there, as before; what it should do in that mode is an open owner decision.
    /// </summary>
    private void ToggleStatusSurfaceCommand()
    {
        if (!StatusBarOptionsView.ShowStatusSurface || _statusSessionClosed)
        {
            _settings.ShowStatusSurface = true;
            _statusCollapsed = false;
            _statusSessionClosed = false;
            SaveSettingsAndPublish();
        }
        else _statusCollapsed = !_statusCollapsed;
        NotifyStatusChromeChanged();
        _diagnostics.Write("status", "toggle_command", new { collapsed = _statusCollapsed, autoHide = StatusBarOptionsView.AutoHide });
    }

    /// <summary>Explorer tab: the folder being browsed; elsewhere: the current image's folder, with the image selected.</summary>
    private void ShowFolderInWindowsExplorer()
    {
        if (_workspace.Active is BrowserTabState browser) { ExplorerShell.RevealSelected(browser.Folder, ExplorerRevealSelection(browser)); return; }
        if (!string.IsNullOrWhiteSpace(_currentPath) && File.Exists(_currentPath)) WindowsExplorerLocateLauncher(_currentPath);
    }

    /// <summary>Shortcut text of a registry action for menus and tooltips ("Unassigned" when it has none).</summary>
    private string ShortcutForAction(string actionId) => ShortcutText(HotkeyCatalog.ShortcutsForAction(actionId, _settings.Hotkeys));

    /// <summary>"<paramref name="text"/> (O / Ctrl+O)" with a registry action's live keys, or just the text when it has none.</summary>
    private string WithActionShortcuts(string text, string actionId) =>
        BindingHints.WithShortcuts(text, HotkeyCatalog.ShortcutsForAction(actionId, _settings.Hotkeys));

    private static string ShortcutText(IReadOnlyList<string> shortcuts) =>
        shortcuts.Count == 0 ? "Unassigned" : string.Join(" / ", shortcuts);

    /// <summary>Title-bar button tooltip shortcut: an alias button (Show in Explorer) reads its target action's live keys.</summary>
    private string ShortcutForTitleButton(TitleBarButtonDefinition definition) =>
        AppActionRegistry.Find(definition.ActionId)?.AliasOf is not null ? ShortcutForAction(definition.ActionId) : ShortcutFor(definition.Command);

    private string ShortcutFor(GlideCommand command) => ShortcutText(HotkeyCatalog.ShortcutsForCommand(command, _settings.Hotkeys));

    private void RefreshDynamicTooltips()
    {
        void Tip(Control c, string text, GlideCommand? command = null)
        {
            ToolTip.SetTip(c, command is null ? text : $"{text} ({ShortcutFor(command.Value)})");
        }
        Tip(NewTabButton, "New tab", GlideCommand.NewTab);
        foreach (var (id, button) in _titleBarButtons)
        {
            if (TitleBarButtonCatalog.Find(id) is not { } definition) continue;
            ToolTip.SetTip(button, TitleActionTip(id, definition));
        }
        if (_homeSurface is not null) UpdateHomeHintTexts(_homeSurface);
        if (_homeSurface is not null) Tip(_homeSurface.OptionsButton, "Settings", GlideCommand.Settings);
        Tip(StatusZoomOutButton, "Zoom out", GlideCommand.ZoomOut);
        Tip(StatusZoomInButton, "Zoom in", GlideCommand.ZoomIn);
        Tip(StatusFitImageButton, $"Fit image: shrink to fit, never enlarge past 100% (applies to: {StatusBarOptionsView.FitScope})", GlideCommand.FitImage);
        Tip(StatusFitWindowButton, $"Fit to window: enlarge or shrink to fill the window (applies to: {StatusBarOptionsView.FitScope})", GlideCommand.FitWindow);
        Tip(StatusFitWidthButton, $"Fit width (applies to: {StatusBarOptionsView.FitScope})", GlideCommand.FitWidth);
        Tip(StatusFitHeightButton, $"Fit height (applies to: {StatusBarOptionsView.FitScope})", GlideCommand.FitHeight);
        Tip(SlideshowButton, _slideshow?.IsActive != true ? "Start slideshow" : _slideshow.IsRunning ? "Pause slideshow" : "Resume slideshow", GlideCommand.StartPauseSlideshow);
        Tip(SlideshowStopButton, "Stop slideshow session", GlideCommand.StopSlideshow);
        Tip(StatusInfoButton, "Image information", GlideCommand.ToggleImageInfo);
        Tip(StatusSaveButton, "Save: write the current rotation/flip into the file. Does nothing until the picture has been edited", GlideCommand.SaveImage);
        Tip(StatusSaveAsButton, "Save as...: save a copy under a new name or format", GlideCommand.SaveImageAs);
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
            // The Explorer toolbar buttons run the tab history Back / Forward: show that action's live keys.
            Tip(_browserSurface.BackButton, "Back", GlideCommand.NavigateBack);
            Tip(_browserSurface.ForwardButton, "Forward", GlideCommand.NavigateForward);
            ToolTip.SetShowOnDisabled(_browserSurface.BackButton, true);
            ToolTip.SetShowOnDisabled(_browserSurface.ForwardButton, true);
            // Up one folder is bindable, and its fixed registry key (Alt+Up) always works: show whichever applies.
            var upKeys = HotkeyCatalog.ShortcutsForCommand(GlideCommand.BrowserUp, _settings.Hotkeys);
            if (upKeys.Count == 0 && BindingHints.FixedShortcut("fixed.explorer.up") is { } fixedUp) upKeys = [fixedUp];
            ToolTip.SetTip(_browserSurface.UpButton, BindingHints.WithShortcuts("Up one folder", upKeys));
            Tip(_browserSurface.RefreshButton, "Refresh", GlideCommand.RefreshFolder);
            ToolTip.SetTip(_browserSurface.WindowsExplorerButton, ExplorerRevealButtonTip());
            // Ctrl+F is a fixed registry shortcut: read it from there instead of typing it a second time.
            _browserSurface.FilterBox.Watermark = BindingHints.FixedShortcut("fixed.explorer.filter") is { } filterKeys
                ? $"Filter this folder ({filterKeys})" : "Filter this folder";
        }
        UpdateHistoryNavigationState(); // Back / Forward tips may name a cross-tab target ("Back to x.jpg")
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
            var choices = await WritableSaveFormatsAsync();
            // A crop defaults to PNG (lossless), as before; every writable type is offered.
            var defaultFormat = choices.Contains(SaveFormatCatalog.Png) ? SaveFormatCatalog.Png : choices[0];
            var picked = await PickSaveDestinationAsync("Export selected region", null, defaultFormat, stem, null, choices);
            if (picked is null) return;
            var (destination, format, _) = picked.Value;
            // Exporting over the multi-frame original itself would keep only one cropped frame of it.
            if (_currentPath is { } original && ImageSavePolicy.IsSameFile(original, destination))
            {
                var frames = await Task.Run(() => File.Exists(original) ? ImageFrameProbe.CountFrames(original) : 0);
                if (ImageSavePolicy.BlocksInPlaceSave(frames))
                {
                    _diagnostics.Write("selection", "export_blocked_multiframe_original", new { path = original, frames });
                    await ConfirmAsync("Export selected region", ImageSavePolicy.MultiFrameOverwriteMessage(original, frames), "OK", showCancel: false);
                    return;
                }
            }
            await ImageSelectionExportService.ExportAsync(bitmap, selection, destination, format: format, sourcePath: _currentPath);
            _diagnostics.Write("selection", "exported", new { path = destination, format = format.Id, selection.Width, selection.Height });
        }
        catch (Exception ex)
        {
            _diagnostics.Write("selection", "export_failed", new { error = ex.Message });
            if (ex is ImageSaveRefusedException or NotSupportedException)
                await ConfirmAsync("Export selected region", ex.Message, "OK", showCancel: false);
        }
    }

    /// <summary>Lossy formats (JPEG, WebP, HEIC, AVIF, JPEG XL, JPEG XR) all use Glide's JPEG save quality.</summary>
    private const float SaveJpegQuality = ImageEncodeOptions.DefaultQuality;

    private static IReadOnlyList<SaveFormat>? _writableSaveFormats;

    /// <summary>
    /// The formats this PC can write, probed once per process on first use (off the UI thread: the probe asks WIC
    /// for HEIC/AVIF/JPEG XL encoders and may load the bundled codec provider for JPEG 2000/WebP).
    /// </summary>
    private static async Task<IReadOnlyList<SaveFormat>> WritableSaveFormatsAsync()
        => _writableSaveFormats ??= await Task.Run(SaveFormatAvailability.Writable);

    /// <summary>
    /// Shows the system Save dialog with every writable type, the default type preselected and a suggested name
    /// whose extension matches it. The returned path's extension always matches the returned format: a typed
    /// extension of a writable type selects that type, otherwise the selected type's extension is appended.
    /// </summary>
    private async Task<(string Path, SaveFormat Format, bool NameCompleted)?> PickSaveDestinationAsync(string title, string? sourcePath, SaveFormat defaultFormat,
        string? stem, IStorageFolder? start, IReadOnlyList<SaveFormat> choices)
    {
        var types = choices.Select(f => new FilePickerFileType(f.DialogLabel) { Patterns = f.Patterns }).ToArray();
        var defaultIndex = Math.Max(0, IndexOf(choices, defaultFormat));
        var result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = SaveAsDialogPolicy.SuggestedFileName(sourcePath, defaultFormat, stem),
            SuggestedStartLocation = start,
            DefaultExtension = SaveAsDialogPolicy.ExtensionFor(defaultFormat, sourcePath).TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = types,
            // The type list and the suggested name must agree (5.0.4 fix: the name said .png while the type said JPEG).
            SuggestedFileType = types[defaultIndex]
        });
        var picked = result.File?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(picked)) return null;
        var selected = result.SelectedFileType is { } type
            ? SaveAsDialogPolicy.FromDialogLabel(type.Name, choices) ?? (Array.IndexOf(types, type) is var i and >= 0 ? choices[i] : null)
            : null;
        var resolved = SaveAsDialogPolicy.ResolveDestination(picked, selected, choices, sourcePath);
        _diagnostics.Write("file", "save_destination_resolved", new { picked, selected = selected?.Id, resolved = resolved.Path, format = resolved.Format.Id });
        return (resolved.Path, resolved.Format, !string.Equals(resolved.Path, picked, StringComparison.OrdinalIgnoreCase));
    }

    private static int IndexOf(IReadOnlyList<SaveFormat> formats, SaveFormat format)
    {
        for (var i = 0; i < formats.Count; i++) if (formats[i] == format) return i;
        return -1;
    }

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

    private async Task SaveCurrentImageAsync()
    {
        var path = _currentPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        // Nothing edited: Save does nothing (unlike Save As, which always writes a copy).
        if (!CanSaveCurrentImage()) return;
        // Formats Glide cannot write on this PC, or must not rewrite in place (layered PSD/ORA, HDR/EXR/PFM precision,
        // multi-size icons, mipmapped textures), fall through to Save As.
        var inPlace = await Task.Run(() => SaveFormatAvailability.InPlaceFormat(path));
        if (inPlace is null) { await SaveCurrentImageAsAsync(); return; }
        // Glide writes one frame: a multipage TIFF (or any multi-frame file) must never be overwritten in place.
        var frames = await Task.Run(() => File.Exists(path) ? ImageFrameProbe.CountFrames(path) : 0);
        if (frames == 0) return;
        if (ImageSavePolicy.BlocksInPlaceSave(frames))
        {
            _diagnostics.Write("file", "save_in_place_blocked_multiframe", new { path, frames });
            if (await ConfirmAsync("Save image", ImageSavePolicy.MultiFrameInPlaceMessage(path, frames), "Save as…"))
                await SaveCurrentImageAsAsync();
            return;
        }
        await SaveCurrentImageToAsync(path, confirmOverwrite: true, knownSourceFrames: frames, format: inPlace);
    }

    private async Task SaveCurrentImageAsAsync()
    {
        var path = _currentPath;
        if (string.IsNullOrWhiteSpace(path) || Viewport.Bitmap is null) return;
        try
        {
            var choices = await WritableSaveFormatsAsync();
            // Default type: the source's own format when this PC can write it, otherwise PNG (lossless).
            var defaultFormat = SaveAsDialogPolicy.DefaultFormat(path, choices);
            var folder = Path.GetDirectoryName(path);
            var start = folder is null ? null : await StorageProvider.TryGetFolderFromPathAsync(folder);
            var picked = await PickSaveDestinationAsync("Save image as", path, defaultFormat, null, start, choices);
            if (picked is null) return;
            var (destination, format, completed) = picked.Value;
            // The system picker already confirmed overwrite (ShowOverwritePrompt) for the name it returned; a name
            // Glide completed with the type's extension has not been confirmed yet.
            await SaveCurrentImageToAsync(destination, confirmOverwrite: completed, format: format);
        }
        catch (Exception ex)
        {
            _diagnostics.Write("file", "save_as_failed", new { error = ex.Message });
        }
    }

    private async Task SaveCurrentImageToAsync(string destination, bool confirmOverwrite, int? knownSourceFrames = null, SaveFormat? format = null)
    {
        var source = _currentPath;
        if (string.IsNullOrWhiteSpace(source)) return;
        var transform = Viewport.EditTransform;
        // File checks and the page/frame probe run off the UI thread (slow or network locations).
        var (sourceExists, destinationExists, frames) = await Task.Run(() =>
        {
            var exists = File.Exists(source);
            return (exists, File.Exists(destination), exists ? knownSourceFrames ?? ImageFrameProbe.CountFrames(source) : 0);
        });
        if (!sourceExists) return;
        if (ImageSavePolicy.BlocksInPlaceSave(frames))
        {
            if (ImageSavePolicy.IsSameFile(source, destination))
            {
                // Save as picked the original itself: writing one frame over it would lose the others.
                _diagnostics.Write("file", "save_in_place_blocked_multiframe", new { path = source, frames });
                await ConfirmAsync("Save image", ImageSavePolicy.MultiFrameOverwriteMessage(source, frames), "OK", showCancel: false);
                return;
            }
            if (!await ConfirmAsync("Save image as", ImageSavePolicy.MultiFrameCopyWarning(source, frames),
                    "Save first " + ImageSavePolicy.FrameNoun(source, 1)))
                return;
        }
        if (confirmOverwrite && destinationExists &&
            !await ConfirmAsync("Save image", $"Overwrite \"{Path.GetFileName(destination)}\"?" +
                (transform.IsIdentity ? "" : " The current rotation/flip will be applied to the file.") +
                ((format ?? SaveFormatCatalog.FromPath(destination))?.PreservesMetadata == true
                    ? " Embedded metadata (EXIF, XMP, colour profile) is kept."
                    : " Embedded metadata (EXIF) is not preserved."), "Overwrite"))
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
            await ImageSaveService.SaveAsync(full, transform, destination, SaveJpegQuality, sourcePath: source, format: format);
            _diagnostics.Write("file", "saved", new { source, destination, format = format?.Id, transform.QuarterTurns, transform.FlipHorizontal, transform.FlipVertical });
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
        var dialog = CreateConfirmDialog(title, message, acceptText, showCancel);
        dialog.Icon = Icon;
        dialog.Topmost = Topmost;
        PopupPlacementStore.Track(dialog, "confirm");
        _overlays?.SetModalSuppressed(true);
        try { return await ShowOwnedDialogAsync<bool>(dialog); }
        finally { _overlays?.SetModalSuppressed(false); }
    }

    /// <summary>
    /// The confirmation dialog (Explorer Delete, Save overwrite ...): message, Cancel and the accept button. Closes with
    /// true (accept) or false (Cancel). The accept button is the default and has the keyboard focus when the dialog opens,
    /// so Enter confirms (round 3 #14, like Windows' own Delete prompt); Escape is Cancel.
    /// </summary>
    internal static Window CreateConfirmDialog(string title, string message, string acceptText, bool showCancel = true)
    {
        var accept = new Button { Content = acceptText, MinWidth = 100, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsVisible = showCancel, IsCancel = true };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        var root = new StackPanel { Margin = new Thickness(22), Spacing = 14 };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        root.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Width = 470, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root
        };
        accept.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Opened += (_, _) => accept.Focus(NavigationMethod.Tab);
        return dialog;
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
                try { await OpenAsImageTabAsync(image); await Task.Delay(700); } catch { /* one image failing to open must not stop the rest */ }
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

    private bool TryGetNativeIconic(out bool iconic)
    {
        iconic = false;
        if (!OperatingSystem.IsWindows()) return false;
        var handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return false;
        iconic = IsIconic(handle.Handle);
        return true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

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
