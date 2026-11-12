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

/// <summary>Standby, external-open handling, file operations and diagnostics export. Split from MainWindow (4.5.2).</summary>
public partial class MainWindow
{
    private void ClearPresentedImageForSpeedBoostStandby()
    {
        _gifPlayer?.Stop();
        // Privacy invariant: standby leaves no displayed bitmap attached to either viewport render
        // path. Request/cache invalidation happens before this detach so stale work cannot reattach it.
        var viewportBitmap = Viewport.Bitmap;
        var interactionBitmap = _interactionPreviewBitmap;

        Viewport.CancelInteraction();
        Viewport.SetInteractionBitmap(null);
        _interactionPreviewBitmap = null;
        Viewport.SwapPresentedFrame(null, 0, 0, 0, null);

        _currentPath = null;
        _presentedPath = null;
        _selectionZoomDemandPath = null;
        _currentMetadata = new();
        _currentDecodeMs = 0;
        _currentFirstFrameDecodeMs = 0;
        _currentFirstFrameWidth = 0;
        _currentFirstFrameHeight = 0;
        _currentPixelWidth = 0;
        _currentPixelHeight = 0;
        _currentFileSize = 0;
        _currentDecodeRoute = "none";
        _currentFrameIsPreview = false;
        CancelPreviewQualityRecovery();
        _loader.SetActivePath(null);

        DisposeDetachedBitmap(viewportBitmap);
        if (!ReferenceEquals(interactionBitmap, viewportBitmap))
            DisposeDetachedBitmap(interactionBitmap);
        try { _loader.PurgeCaches(); } catch { }
    }

    private void DisposeDetachedBitmap(Bitmap? bitmap)
    {
        // Cached bitmaps are disposed by ImageLoadCoordinator.PurgeCaches. Only dispose an image
        // that was not in the loader cache, avoiding double-disposal across both ownership domains.
        if (bitmap is null || _loader.IsBitmapCached(bitmap)) return;
        bitmap.Dispose();
    }

    private void ReturnToSpeedBoostStandbyAfterDelegatedOpen()
    {
        _speedBoostStandbyActive = true;
        _warmPresentationGateActive = false;
        Opacity = _sessionOpacity;
        WarmOpenTransitionHost.IsVisible = false;
        // Remove taskbar eligibility before hiding the warm HWND. Waiting until Closed can leave
        // a stale button behind while the next Speed Boost window is being created.
        ShowInTaskbar = false;
        Hide();
        GlideWindowRegistry.RefreshTaskbarRepresentative();
    }

    private static void ShowSecondaryWindow(MainWindow child)
    {
        // Secondary windows are ordinary top-level windows: each owns its own taskbar button and a
        // distinct AppUserModelID, so Windows shows it as a separate taskbar entry instead of
        // combining it with the primary window.
        child.IsSecondaryWindow = true;
        child.ShowInTaskbar = true;
        child.Show();
        child.ApplyDistinctTaskbarIdentity();
    }

    /// <summary>
    /// Gives this window its own taskbar identity so Windows renders a separate button for it. The
    /// primary window keeps the process default so taskbar pinning still matches the pinned shortcut.
    /// </summary>
    internal void ApplyDistinctTaskbarIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            WindowsTaskbarIdentity.TrySetWindowAppUserModelId(handle, $"cobhc95.Glide.Window.{Guid.NewGuid():N}");
        }
        catch { /* Taskbar identity is cosmetic; never let it disturb a window. */ }
    }

    private bool ShouldEnterSpeedBoostStandby()
        => !_forceProcessExit && !_closeForSpeedBoostStandby && !_speedBoostStandbyActive &&
           _settings.SpeedBoostEnabled && App.IsTrayIconAvailable &&
           GlideWindowRegistry.Snapshot().Count <= 1;

    internal void EnterSpeedBoostStandby(bool persistSession = true)
    {
        if (_closeForSpeedBoostStandby) return;
        // The caption X (and any other direct standby entry) bypasses the Closing handler, which is
        // where the "Open last session" snapshot used to be written. Persist it here too, before the
        // workspace is cleared, so closing to Speed Boost standby never loses the open tabs. A
        // background-startup window has no user workspace and must not overwrite the snapshot.
        if (persistSession && IsVisible && !_speedBoostStandbyActive && _workspace.Tabs.Count > 0)
        {
            try
            {
                CaptureActiveTabRuntimeState();
                WorkspaceSessionStore.Save(_workspace.Tabs, _workspace.ActiveIndex);
            }
            catch (Exception ex)
            {
                _diagnostics.Write("window", "standby_session_persist_failed", new { error = ex.GetType().Name, ex.Message });
            }
        }
        // Mark the transition before Hide(). The broker can receive another Explorer request
        // synchronously during this method; App must wait for this owner to finish its internal
        // close instead of creating a second hidden receiver.
        _standbyClosePending = true;
        _closeForSpeedBoostStandby = true;
        // Caption-close enters standby directly and therefore bypasses the normal async Closing
        // persistence branch. Capture the current state here as well, otherwise a maximized
        // window can come back as normal after the warm window is recreated.
        SaveWindowPlacement();
        _speedBoostStandbyActive = true;
        _warmPresentationGateActive = false;
        // Silence overlay videos before the window disappears; the overlays are disposed with it.
        _overlays?.SetVideosSuspended(true);
        Opacity = _sessionOpacity;

        // Cancel both UI request epochs and the loader generation before detaching the old frame.
        AdvanceWorkspaceEpoch();
        Interlocked.Increment(ref _imageRequestId);
        _loader.InvalidatePending();
        ClearPresentedImageForSpeedBoostStandby();

        // A close-to-tray/standby transition must never leave the temporary warm-open surface or
        // the empty image surface visible if the user presses X before an image request finishes.
        WarmOpenTransitionHost.IsVisible = false;
        ImageView.IsVisible = false;
        // Make the taskbar removal happen while this HWND is still valid. If it is deferred to the
        // Closed callback, Windows can retain the old Glide button when the next warm window opens.
        ShowInTaskbar = false;
        Hide();
        GlideWindowRegistry.RefreshTaskbarRepresentative();
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            if (OperatingSystem.IsWindows())
            {
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
        }
        catch { }
        App.PublishCurrentSettings(_settings);
        App.UpdateTrayIconState(_settings);
        // Closing the warm window is what releases Avalonia's composition target. The process and
        // broker remain resident; App creates a fresh render window on the next activation.
        Dispatcher.UIThread.Post(Close, DispatcherPriority.Send);
    }

    internal void ExitStandby()
    {
        // Standby removes the taskbar button before hiding; a restored window must own one again.
        if (!ShowInTaskbar) ShowInTaskbar = true;
        var warmOpenTransitionVisible = WarmOpenTransitionHost.IsVisible;
        if (!IsVisible)
        {
            ArmWarmPresentationGate();
            Show();
            // Speed Boost reuses this HWND instead of running the normal Opened/attach path.
            // Refresh the native frame so Windows Snap Layouts and edge snapping remain available
            // after the Hide/Show standby transition.
            _windowsBrowserFrame?.RefreshForWarmRestore();
            Dispatcher.UIThread.Post(EnsureNativeMaximizedState, DispatcherPriority.Loaded);
        }
        _speedBoostStandbyActive = false;
        App.UpdateTrayIconState(_settings);

        // X from an image clears the viewport, so a no-file warm activation lands on normal Home
        // instead of exposing an empty viewer. An incoming Explorer image owns the transition path.
        if (!warmOpenTransitionVisible && _workspace.Active is ImageTabState && _presentedPath is null)
            ShowHomeSurface();
    }

    internal async Task<ExternalLaunchItemResult> HandleExternalActivateAsync(Guid requestId)
    {
        ExitStandby();
        RestoreFromMinimizedIfNeeded();
        Activate();
        await ReleaseWarmPresentationGateAsync(safeFrameReady: true);
        return new ExternalLaunchItemResult(requestId, ExternalLaunchItemStatus.Accepted, "Activated");
    }

    internal void ForceExit()
    {
        _diagnostics.Write("window", "force_exit_requested", new { state = WindowState.ToString(), active = _workspace.Active?.Kind.ToString() });
        // Force-exit bypasses the ordinary Closing persistence branch, so commit placement first.
        // In particular, never persist Minimized as a launch state: SaveWindowPlacement uses the
        // remembered pre-minimize state and the last normal bounds.
        try
        {
            // A dormant Speed Boost window holds no user workspace (its tabs were saved when it
            // entered standby); saving it here would replace the real last session with nothing.
            if (!_speedBoostStandbyActive && !_closeForSpeedBoostStandby && _workspace.Tabs.Count > 0)
            {
                CaptureActiveTabRuntimeState();
                WorkspaceSessionStore.Save(_workspace.Tabs, _workspace.ActiveIndex);
                SaveWindowPlacement();
            }
        }
        catch (Exception ex)
        {
            _diagnostics.Write("window", "force_exit_persistence_failed", new { error = ex.GetType().Name, ex.Message });
        }

        _forceProcessExit = true;
        Close();
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    internal void OpenSettingsDialog()
    {
        SettingsClicked(this, new RoutedEventArgs());
    }

    internal async Task<ExternalLaunchItemResult> HandleExternalFolderOpenAsync(Guid requestId, string path)
    {
        // A post-standby receiver is itself the requested warm window. Do not spawn a second
        // child when the user's normal Explorer policy is "Open new window".
        var isPendingWarmReceiver = _pendingExternalReceiver;
        if (isPendingWarmReceiver) _pendingExternalReceiver = false;
        ExitStandby();
        if (!Directory.Exists(path))
        {
            await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
            return new(requestId, ExternalLaunchItemStatus.Rejected, "Folder does not exist.");
        }
        var fullPath = Path.GetFullPath(path);
        var behavior = CurrentExternalOpenBehavior;
        if (!isPendingWarmReceiver && string.Equals(behavior, "Open new window", StringComparison.OrdinalIgnoreCase))
        {
            var child = new MainWindow();
            child.QueueOpen(fullPath);
            ShowSecondaryWindow(child);
            await ReleaseWarmPresentationGateAsync(safeFrameReady: true);
            return new(requestId, ExternalLaunchItemStatus.Accepted, "Opened folder in new window.");
        }

        if (string.Equals(behavior, "Overwrite existing tab", StringComparison.OrdinalIgnoreCase) &&
            _workspace.Active is { } active)
        {
            _workspace.ReplaceTab(new BrowserTabState(active.Id, fullPath));
            await ActivateWorkspaceAsync();
        }
        else
        {
            _workspace.AddBrowser(fullPath);
            await ActivateWorkspaceAsync();
        }
        RestoreFromMinimizedIfNeeded();
        Activate();
        await ReleaseWarmPresentationGateAsync(safeFrameReady: true);
        return new(requestId, ExternalLaunchItemStatus.Accepted, "Folder accepted.");
    }

    internal bool ContainsOpenImage(string path)
    {
        var full = Path.GetFullPath(path);
        return _workspace.Tabs.OfType<ImageTabState>().Any(tab =>
            string.Equals(Path.GetFullPath(tab.Path), full, StringComparison.OrdinalIgnoreCase));
    }

    internal async Task RefreshExistingImageFromExternalAsync(string path)
    {
        var full = Path.GetFullPath(path);
        var existing = _workspace.Tabs.OfType<ImageTabState>().FirstOrDefault(tab =>
            string.Equals(Path.GetFullPath(tab.Path), full, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        _workspace.Select(existing.Id);
        await ActivateWorkspaceAsync();
        // Force an actual refresh rather than silently reusing the currently presented frame.
        _loader.InvalidatePath(full);
        await LoadPathAsync(full, updateActiveTab: false);
        RestoreFromMinimizedIfNeeded();
        Activate();
    }

    private bool IsDefaultHomeOnlyWorkspace()
        => _workspace.Tabs.Count == 1 && _workspace.Active is HomeTabState;

    internal async Task<ExternalLaunchItemResult> HandleExternalOpenAsync(Guid requestId, string path, string behavior)
    {
        // After Speed Boost standby the broker creates one fresh render receiver. That receiver is
        // already the logical "new window" for this Explorer launch. If the normal external-open
        // policy is also "Open new window", spawning a child here leaves the receiver behind as an
        // Alt+Tab-only ghost. Consume the first forwarded request in the receiver itself.
        var isPendingWarmReceiver = _pendingExternalReceiver;
        if (isPendingWarmReceiver) _pendingExternalReceiver = false;
        var wasSpeedBoostStandby = _speedBoostStandbyActive || isPendingWarmReceiver;
        if (!File.Exists(path))
            return new(requestId, ExternalLaunchItemStatus.Rejected, "File does not exist.");
        if (!ImageNavigator.IsSupported(path))
            return new(requestId, ExternalLaunchItemStatus.Rejected, "Unsupported image type.");

        // Set the transition surface while the warm window is still hidden. Showing the HWND first
        // allows Windows to present the last composed image for one frame before this handler runs.
        if (wasSpeedBoostStandby)
            ShowWarmOpenTransitionSurface();
        ExitStandby();

        var duplicateWindow = ExternalLaunchBroker.FindWindowContainingImage(path);
        if (duplicateWindow is not null)
        {
            // Standby deliberately clears the displayed bitmap and presented path for privacy, but
            // retains the image tab's path so workspace identity and navigation survive. If Explorer
            // opens that same path again, this warm window is the receiver we want; applying the
            // normal duplicate policy here would incorrectly spawn a cold process (the default is
            // "Open new instance"). Refresh through the existing tab so the privacy gate remains in
            // place until the new frame is actually ready.
            if (wasSpeedBoostStandby && ReferenceEquals(duplicateWindow, this))
            {
                await duplicateWindow.RefreshExistingImageFromExternalAsync(path);
                RestoreFromMinimizedIfNeeded();
                Activate();
                return new(requestId, ExternalLaunchItemStatus.Accepted, "Reopened image in warm Glide instance.");
            }

            var duplicateBehavior = _settings.SameImageAlreadyOpenBehavior ?? "Open new instance";
            if (string.Equals(duplicateBehavior, "Refresh existing image", StringComparison.OrdinalIgnoreCase))
            {
                await duplicateWindow.RefreshExistingImageFromExternalAsync(path);
                if (wasSpeedBoostStandby && !ReferenceEquals(duplicateWindow, this))
                    ReturnToSpeedBoostStandbyAfterDelegatedOpen();
                return new(requestId, ExternalLaunchItemStatus.Accepted, "Refreshed already-open image.");
            }
            if (string.Equals(duplicateBehavior, "Open new tab", StringComparison.OrdinalIgnoreCase))
            {
                await OpenAsImageTabAsync(path, openInNewTab: true);
                if (wasSpeedBoostStandby)
                    ShowImageSurface();
                RestoreFromMinimizedIfNeeded();
                Activate();
                return new(requestId, ExternalLaunchItemStatus.Accepted, "Opened duplicate image in new tab.");
            }
            var duplicateChild = new MainWindow();
            duplicateChild.QueueOpen(path);
            ShowSecondaryWindow(duplicateChild);
            if (wasSpeedBoostStandby)
                ReturnToSpeedBoostStandbyAfterDelegatedOpen();
            return new(requestId, ExternalLaunchItemStatus.Accepted, "Opened duplicate image in a separate Glide window.");
        }

        if (!isPendingWarmReceiver && string.Equals(behavior, "Open new window", StringComparison.OrdinalIgnoreCase))
        {
            var child = new MainWindow();
            child.QueueOpen(path);
            ShowSecondaryWindow(child);
            if (wasSpeedBoostStandby)
                ReturnToSpeedBoostStandbyAfterDelegatedOpen();
            return new(requestId, ExternalLaunchItemStatus.Accepted, "Opened image in new window.");
        }
        var overwriteExistingTab = string.Equals(behavior, "Overwrite existing tab", StringComparison.OrdinalIgnoreCase);
        // Cold opens and warm starts activated from the Start menu retain the normal welcome surface.
        // Only the external image-open path from Speed Boost uses the neutral transition surface.
        if (!wasSpeedBoostStandby && HomeHost.IsVisible)
            ShowImageSurface();
        // OpenAsImageTabAsync replaces the active tab when requested. Do not close that tab first:
        // closing it can activate a neighbouring tab and then overwrite the wrong destination.
        // A warm/previously standalone Glide shell can still contain its untouched default Home
        // tab. The first explicit Explorer image open should turn that placeholder into the image,
        // even when the normal external policy is "Open in new tab"; otherwise Explorer launches
        // visibly retain an unrelated Home tab beside the requested picture.
        var replaceDefaultHome = IsDefaultHomeOnlyWorkspace();
        await OpenAsImageTabAsync(path, openInNewTab: !overwriteExistingTab && !replaceDefaultHome);
        if (wasSpeedBoostStandby)
        {
            ShowImageSurface();
            // PresentCurrentAsync releases the gate only after the matching image draw/composition.
            // If loading was rejected before that point, reveal a newly composed Home surface.
            if (_warmPresentationGateActive && _presentedPath is null)
                await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
        }
        RestoreFromMinimizedIfNeeded();
        Activate();
        return new(requestId, ExternalLaunchItemStatus.Accepted, "Image accepted.");
    }

    private async Task<string> ShowMultiTabCloseDialogAsync()
    {
        var remember = new CheckBox { Content = "Remember my choice" };
        var closeCurrent = new Button { Content = "Close only current tab", MinWidth = 170 };
        var closeAll = new Button { Content = "Close all", MinWidth = 130, Classes = { "primaryAction" } };
        var dialog = new Window
        {
            Title = "Close Glide tabs?", Width = 470, Height = 230, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(22), Spacing = 16, Children =
            {
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { new TextBlock { Text = "⚠", FontSize = 26, Foreground = new SolidColorBrush(Color.Parse("#E5B53B")) }, new TextBlock { Text = "This Glide window contains multiple image tabs.\nWhat would you like to close?", TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } } },
                remember,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 10, Children = { closeCurrent, closeAll } }
            }}
        };
        PopupPlacementStore.Track(dialog, "multi-tab-close-confirm");
        string result = "Cancel";
        closeCurrent.Click += (_, _) => { result = "Close current tab"; dialog.Close(); };
        closeAll.Click += (_, _) => { result = "Close all"; dialog.Close(); };
        await ShowOwnedDialogAsync(dialog);
        if (remember.IsChecked == true && result != "Cancel") { _settings.RememberedMultiTabCloseChoice = result; _settings.ConfirmCloseMultipleTabs = false; SaveSettingsAndPublish(); }
        return result;
    }

    private void OpenContainingFolder()
    {
        // The embedded Explorer remains removed. This title-bar action opens the real Windows
        // Explorer only, selecting the current image.
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        var path = _currentPath;
        var opened = _fileOperations.TryOpenContainingFolder(path);
        _diagnostics.Write("file", opened ? "open_containing_folder_external" : "open_containing_folder_failed", new { path });
    }

    private async Task OpenContainingFolderInCurrentTabAsync(Guid tabId, string imagePath, string folder)
    {
        _tabForwardImageTargets[tabId] = imagePath;
        _tabForwardFolderTargets[tabId] = new Stack<string>();
        _browserHighlightTargets[tabId] = imagePath;

        var session = new BrowserSession();
        session.History.Add(folder);
        session.Highlights.Add(imagePath);
        session.Index = 0;
        _browserSessions[tabId] = session;

        _workspace.ReplaceTab(new BrowserTabState(tabId, folder)
        {
            Navigation = new BrowserNavigationState(session.History.ToArray(), session.Index, session.Highlights.ToArray())
        });
        await ActivateWorkspaceAsync();
        SelectBrowserHighlight(tabId);
        _diagnostics.Write("file", "open_containing_folder_internal", new { path = imagePath, folder, sameTab = true });
    }

    private async Task RenameCurrentFileAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentPath) || !File.Exists(_currentPath)) return;
        var oldPath = _currentPath;
        var box = new TextBox { Text = Path.GetFileName(oldPath), MinWidth = 330 };
        var dialog = CreateContentSizedDialog("Rename image", 430, 620);
        PopupPlacementStore.Track(dialog, "rename-image");
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true };
        var rename = new Button { Content = "Rename", MinWidth = 88, IsDefault = true };
        var accepted = false;
        cancel.Click += (_, _) => dialog.Close();
        rename.Click += (_, _) => { accepted = true; dialog.Close(); };
        // Explorer-style editing: select only the name part (so Right Arrow lands just before the
        // extension's dot), Enter renames and Escape cancels even while the text box has focus.
        dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(); }
            else if (e.Key is Key.Enter or Key.Return) { e.Handled = true; accepted = true; dialog.Close(); }
        }, RoutingStrategies.Tunnel);
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            var text = box.Text ?? string.Empty;
            var dot = text.LastIndexOf('.');
            box.SelectionStart = 0;
            box.SelectionEnd = dot > 0 ? dot : text.Length;
        }, DispatcherPriority.Input);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "New file name", FontWeight = FontWeight.SemiBold }, box,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, rename } }
            }
        };
        await ShowOwnedDialogAsync(dialog);
        if (!accepted || string.IsNullOrWhiteSpace(box.Text)) return;
        var newPath = _fileOperations.ValidateRename(oldPath, box.Text.Trim());
        if (newPath is null) { _diagnostics.Write("file", "rename_rejected", new { path = oldPath }); return; }
        try
        {
            newPath = await _fileOperations.RenameAsync(oldPath, box.Text.Trim());
            if (newPath is null) return;
            _workspace.ReplaceActiveImage(newPath);
            await LoadPathAsync(newPath, updateActiveTab: true);
            RebuildTabStrip();
        }
        catch { }
    }

    private async Task DeleteCurrentFileAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentPath) || !File.Exists(_currentPath)) return;
        var oldPath = _currentPath;
        var oldIndex = Math.Max(0, _navigator.Index);
        var folder = Path.GetDirectoryName(oldPath);
        try
        {
            if (!await _fileOperations.RecycleAsync(oldPath)) return;
            _diagnostics.Write("file", "recycle_delete", new { path = oldPath });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _diagnostics.Write("file", "recycle_delete_failed", new { path = oldPath, error = ex.Message });
            return;
        }

        string? replacement = null;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            try
            {
                var remaining = Directory.EnumerateFiles(folder).Where(ImageNavigator.IsSupported)
                    .OrderBy(p => p, Glide.Core.ImageSortOrder.CreateComparer()).ToList();
                if (remaining.Count > 0) replacement = remaining[Math.Min(oldIndex, remaining.Count - 1)];
            }
            catch { }
        }

        if (replacement is not null)
        {
            await LoadPathAsync(replacement, updateActiveTab: true);
            RebuildTabStrip();
        }
        else
        {
            await CloseActiveTabAsync();
        }
    }

    private void ShowViewerContextMenu()
    {
        var menu = BuildViewerContextMenu();
        menu.Open(Viewport);
    }

    private ContextMenu BuildViewerContextMenu()
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        var hasImage = Viewport.Bitmap is not null;
        var hasFile = _currentPath is not null;
        MenuItem Item(string label, GlideCommand command, bool enabled = true, string? icon = null)
        {
            var suffix = ShortcutFor(command);
            var item = new MenuItem { Header = suffix == "Unassigned" ? label : $"{label}\t{suffix}", IsEnabled = enabled };
            if (icon is not null) item.Icon = MenuIcon(icon);
            item.Click += async (_, _) => await ExecuteCommandAsync(command);
            return item;
        }
        MenuItem Group(string header, string icon, params object[] children)
        {
            var group = new MenuItem { Header = header, Icon = MenuIcon(icon) };
            group.ItemsSource = children;
            return group;
        }
        MenuItem Separator() => new() { Header = "-" };

        // The most-used action stays at the top level; everything else is grouped by purpose.
        items.Add(Item(Viewport.HasSelection ? "Copy selected pixels" : "Copy image pixels", GlideCommand.CopyImage, hasImage, "Copy"));
        items.Add(Separator());

        items.Add(Group("Fit and zoom", "ZoomIn",
            Item("Fit image (shrink only)", GlideCommand.FitImage, hasImage, "Fit"),
            Item("Fit to window (enlarge or shrink)", GlideCommand.FitWindow, hasImage, "Fit"),
            Item("Fit width", GlideCommand.FitWidth, hasImage, "FitWidth"),
            Item("Fit height", GlideCommand.FitHeight, hasImage, "FitHeight"),
            Item("Actual size (100%)", GlideCommand.ActualSize, hasImage, "Actual")));

        items.Add(Group("Rotate and flip", "RotateRight",
            Item("Rotate left", GlideCommand.RotateLeft, hasImage, "RotateLeft"),
            Item("Rotate right", GlideCommand.RotateRight, hasImage, "RotateRight"),
            Separator(),
            Item("Flip horizontally", GlideCommand.FlipHorizontal, hasImage, "FlipHorizontal"),
            Item("Flip vertically", GlideCommand.FlipVertical, hasImage, "FlipVertical")));

        items.Add(Group("Copy and export", "Copy",
            Item("Copy image file", GlideCommand.CopyFile, hasFile, "File"),
            Item("Copy full path", GlideCommand.CopyFullPath, hasFile, "Copy"),
            Item("Copy file name", GlideCommand.CopyFileName, hasFile, "Copy"),
            Item("Copy folder path", GlideCommand.CopyFolderPath, hasFile, "Open"),
            Separator(),
            Item("Export selected region…", GlideCommand.ExportSelection, Viewport.HasSelection && hasImage, "Crop")));
        items.Add(Separator());

        var fileItems = new List<object>
        {
            Item("Save", GlideCommand.SaveImage, CanSaveCurrentImage(), "SaveFile"),
            Item("Save as…", GlideCommand.SaveImageAs, hasImage && hasFile, "SaveAs"),
            Item("Rename…", GlideCommand.RenameFile, hasFile, "Rename"),
            Item("Delete to Recycle Bin", GlideCommand.DeleteFile, hasFile, "Delete"),
            Separator(),
            Item("Print…", GlideCommand.Print, hasImage, "Print")
        };
        if (!string.IsNullOrWhiteSpace(_settings.ExternalProgram1) || !string.IsNullOrWhiteSpace(_settings.ExternalProgram2) || !string.IsNullOrWhiteSpace(_settings.ExternalProgram3))
        {
            var externalItems = new List<MenuItem>();
            if (!string.IsNullOrWhiteSpace(_settings.ExternalProgram1)) externalItems.Add(Item(Path.GetFileNameWithoutExtension(_settings.ExternalProgram1), GlideCommand.ExternalProgram1, hasFile, "OpenWith"));
            if (!string.IsNullOrWhiteSpace(_settings.ExternalProgram2)) externalItems.Add(Item(Path.GetFileNameWithoutExtension(_settings.ExternalProgram2), GlideCommand.ExternalProgram2, hasFile, "OpenWith"));
            if (!string.IsNullOrWhiteSpace(_settings.ExternalProgram3)) externalItems.Add(Item(Path.GetFileNameWithoutExtension(_settings.ExternalProgram3), GlideCommand.ExternalProgram3, hasFile, "OpenWith"));
            fileItems.Add(Separator());
            fileItems.Add(Group("Open with configured program", "OpenWith", externalItems.Cast<object>().ToArray()));
        }
        items.Add(Group("File", "File", fileItems.ToArray()));

        items.Add(Group("Navigate", "Navigate",
            Item("Previous image", GlideCommand.PreviousImage, _navigator.Count > 1, "Previous"),
            Item("Next image", GlideCommand.NextImage, _navigator.Count > 1, "Next"),
            Item("First image", GlideCommand.FirstImage, _navigator.Count > 1, "First"),
            Item("Last image", GlideCommand.LastImage, _navigator.Count > 1, "Last")));

        var statusShown = _settings.ShowStatusSurface && !_statusCollapsed;
        var showStatus = new MenuItem { Header = statusShown ? "Show status bar  ✓" : "Show status bar", Icon = MenuIcon("Status") };
        showStatus.Click += (_, _) =>
        {
            // A collapsed bar is "not shown": this item brings it back rather than closing it.
            _settings.ShowStatusSurface = !statusShown;
            _statusCollapsed = false;
            _statusSessionClosed = false;
            SaveSettingsAndPublish();
            ApplyStatusVisibility();
        };
        var viewItems = new List<object>
        {
            showStatus,
            Item(ImageInfoOverlay.IsVisible ? "Hide image information" : "Show image information", GlideCommand.ToggleImageInfo, hasImage, "Info"),
            Separator(),
            Item(WindowState == WindowState.FullScreen ? "Exit fullscreen" : "Fullscreen", GlideCommand.ToggleFullscreen, true, "Fullscreen"),
            Item(_slideshow?.IsRunning == true ? "Pause slideshow" : "Start slideshow", GlideCommand.StartPauseSlideshow, _navigator.Count > 0, _slideshow?.IsRunning == true ? "Pause" : "Play"),
            Item("Stop slideshow", GlideCommand.StopSlideshow, _slideshow?.IsActive == true, "Stop")
        };
        // Directly visible, tickable Always-on-top toggle for the ordinary windowed viewer. In
        // Overlay mode the whole-app Overlay menu already exposes the equivalent Overlay-topmost
        // item, so avoid a duplicate here.
        if (!_wholeAppOverlayMode)
        {
            var alwaysOnTop = BuildAlwaysOnTopMenuItem();
            alwaysOnTop.Icon = MenuIcon("AlwaysOnTop");
            viewItems.Add(Separator());
            viewItems.Add(alwaysOnTop);
        }
        items.Add(Group("View and present", "Present", viewItems.ToArray()));

        var overlayItems = new List<MenuItem>();
        AppendWholeAppOverlayMenuItems(overlayItems);
        if (overlayItems.Count == 1)
        {
            overlayItems[0].Icon = MenuIcon("Overlays");
            items.Add(overlayItems[0]);
        }
        else
        {
            items.Add(Group("Overlay mode", "Overlays", overlayItems.Cast<object>().ToArray()));
        }

        items.Add(Separator());
        items.Add(Item("Settings", GlideCommand.Settings, true, "Settings"));
        items.Add(Item("Exit program", GlideCommand.CloseWindow, true, "Close"));
        menu.ItemsSource = items;
        return PrepareOwnedContextMenu(menu);
    }

    private void LaunchExternal(int slot)
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        var program = slot switch { 1 => _settings.ExternalProgram1, 2 => _settings.ExternalProgram2, _ => _settings.ExternalProgram3 };
        var launched = _fileOperations.TryLaunchExternal(program, _currentPath);
        _diagnostics.Write("file", launched ? "external_launched" : "external_launch_failed", new { slot, program, path = _currentPath });
    }

    internal async Task CaptureDiagnosticVisualScenariosAsync(string folder)
    {
        var originalState = WindowState;
        var originalWidth = Width;
        var originalHeight = Height;
        var originalPosition = Position;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;

        var rows = new List<string> { "scenario,physical_width,physical_height,capture,status" };
        var scenarios = new (string Name, int Width, int Height)[]
        {
            ("compact", 900, 600),
            ("landscape", 1400, 760),
            ("portrait", 850, 950),
            ("ultrawide", 1800, 760)
        };

        try
        {
            WindowState = WindowState.Normal;
            foreach (var scenario in scenarios)
            {
                Width = Math.Max(MinWidth, scenario.Width / scale);
                Height = Math.Max(MinHeight, scenario.Height / scale);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(120);
                var file = $"main_resize_{scenario.Name}.png";
                var ok = UiDiagnosticCapture.TryCapture(this, Path.Combine(folder, file), out var error);
                UiDiagnosticCapture.WriteGeometry(this, Path.Combine(folder, $"main_resize_{scenario.Name}_geometry.txt"));
                UiDiagnosticCapture.WriteLayoutAudit(this, Path.Combine(folder, $"main_resize_{scenario.Name}_layout_audit.txt"));
                rows.Add($"{scenario.Name},{scenario.Width},{scenario.Height},{file},{(ok ? "CAPTURED" : "FAIL:" + (error ?? "unknown").Replace(',', ';'))}");
            }
        }
        finally
        {
            Width = originalWidth;
            Height = originalHeight;
            Position = originalPosition;
            WindowState = originalState;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }

        File.WriteAllLines(Path.Combine(folder, "main_resize_scenarios.csv"), rows);

        var combinedVisual = Path.Combine(folder, "combined_visual_results.csv");
        if (File.Exists(combinedVisual))
        {
            var appended = scenarios.Select(scenario =>
                $"\"main_resize_{scenario.Name}\",\"Chrome/content/status stay inside bounds after resize and repaint settles\",\"CAPTURED\",\"main_resize_{scenario.Name}.png\",true,\"Review geometry and stale/ghost paint against legacy Glide\"");
            File.AppendAllLines(combinedVisual, appended);
        }
    }

    public void WriteDiagnosticEvidence(string folder)
    {
        Directory.CreateDirectory(folder);
        _diagnostics.ExportJsonLines(Path.Combine(folder, "behaviour_events.jsonl"));
        File.WriteAllText(Path.Combine(folder, "codec_provider_inventory.json"), JsonSerializer.Serialize(CodecProviderRuntime.Shared.Artifacts, AppJsonContext.Default.IReadOnlyListCodecProviderArtifact));
        File.WriteAllLines(Path.Combine(folder, "main_runtime_state.txt"), new[]
        {
            $"title={Title}",
            $"windowState={WindowState}",
            $"position={Position}",
            $"logicalSize={Width:F1}x{Height:F1}",
            $"renderScaling={TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0:F3}",
            $"activeTab={_workspace.Active?.Kind}:{_workspace.Active?.Title}",
            $"tabCount={_workspace.Tabs.Count}",
            $"closedTabCount={_workspace.ClosedCount}",
            $"currentPath={_currentPath ?? ""}",
            $"imageIndex={_navigator.Index}",
            $"imageCount={_navigator.Count}",
            $"imagePixels={_currentPixelWidth}x{_currentPixelHeight}",
            $"firstFrameDecodeMs={_currentFirstFrameDecodeMs:F3}",
            $"firstFrameDecodeRoute={_currentDecodeRoute}",
            $"zoomPercent={Viewport.ZoomPercent}",
            $"selection={Viewport.HasSelection}",
            $"slideshowSessionActive={_slideshow?.IsActive == true}",
            $"slideshowRunning={_slideshow?.IsRunning == true}",
            $"statusCollapsed={_statusCollapsed}",
            $"sessionOpacity={_sessionOpacity:F2}",
            $"topmost={Topmost}",
            $"settingsPath={SettingsStore.GetSettingsPath()}"
        });
    }

    private void WriteStartupDiagnostics(string phase)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(local, "Glide", "Startup Diagnostics");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "startup-timings.jsonl");
            var cache = _loader.GetCacheSnapshot();
            var row = new System.Text.Json.Nodes.JsonObject
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow,
                ["phase"] = phase,
                ["elapsedMs"] = Math.Round(_startupStopwatch.Elapsed.TotalMilliseconds, 3),
                ["queuedOpen"] = _queuedOpen,
                ["currentPath"] = _currentPath,
                ["currentDecodeMs"] = Math.Round(_currentDecodeMs, 3),
                ["firstFrameDecodeMs"] = Math.Round(_currentFirstFrameDecodeMs, 3),
                ["currentDecodeRoute"] = _currentDecodeRoute,
                ["folderIndexReady"] = _folderIndexReady,
                ["folderIndexCanonicalReady"] = _folderIndexCanonicalReady,
                ["profile"] = _settings.InitialImageQuality,
                ["compressedItems"] = cache.CompressedItems,
                ["compressedBytes"] = cache.CompressedBytes,
                ["decodedItems"] = cache.DecodedItems,
                ["decodedBytes"] = cache.DecodedBytes
            }.ToJsonString();
            File.AppendAllText(path, row + Environment.NewLine);
        }
        catch { }
    }
    internal async Task<string> RunFullDiagnosticExportAsync(string? targetDir = null)
    {
        var settingsWindow = new SettingsWindow(_settings, ApplySettingsFromDialog)
        {
            Topmost = Topmost
        };
        settingsWindow.Show(this);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(100);
            return await settingsWindow.ExportDiagnosticsAsync(targetDir, this);
        }
        finally
        {
            try { settingsWindow.Close(); } catch { }
        }
    }

    private sealed class BrowserSession
    {
        public List<string> History { get; } = new();
        public List<string?> Highlights { get; } = new();
        public int Index { get; set; } = -1;
    }
}
