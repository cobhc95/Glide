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

/// <summary>Managed browser surface: thumbnails, shell verbs and browser UI events. Split from MainWindow (4.5.2); behavior unchanged.</summary>
public partial class MainWindow
{
    private void CancelBrowserThumbnailWork()
    {
        _browserThumbnailCts?.Cancel();
        _browserThumbnailCts?.Dispose();
        _browserThumbnailCts = null;
        CancelAllBrowserRealizations();
    }

    private async void RefreshManagedBrowser(string folder)
    {
        _browserThumbnailCts?.Cancel();
        _browserThumbnailCts?.Dispose();
        _browserThumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
        var token = _browserThumbnailCts.Token;
        var generation = Interlocked.Increment(ref _browserGeneration);
        var activeTabId = (_workspace.Active as BrowserTabState)?.Id ?? Guid.Empty;
        CancelAllBrowserRealizations();
        _browserSelection.Clear();
        _browserSelectionAnchor = -1;
        _browserCurrentIndex = -1;
        // Never mutate the list currently owned by ItemsRepeater. During a folder transition the
        // repeater may still be clearing old realized elements; mutating its source in place lets
        // old and new entries briefly share indices and produces mixed-folder contents.
        _browserEntries = new List<BrowserEntry>();
        ApplyManagedExplorerTheme();
        BrowserRepeater.ItemsSource = _browserEntries;
        try
        {
            // ItemsRepeater clears realized visuals at a render boundary. Let that pass complete
            // before assigning a different folder source, otherwise old tiles can be reused under
            // new indices while Up/Back/Refresh requests overlap.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (generation != Volatile.Read(ref _browserGeneration) ||
                _workspace.Active is not BrowserTabState current || current.Id != activeTabId ||
                !string.Equals(current.Folder, folder, StringComparison.OrdinalIgnoreCase)) return;
            // Enumeration/sorting is the only O(N) folder operation. UI controls, context menus,
            // tooltips and thumbnail decoders are created strictly by ItemsRepeater realization.
            var entries = await Task.Run(() => EnumerateBrowserEntries(folder, token), token);
            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _browserGeneration) ||
                _workspace.Active is not BrowserTabState active || active.Id != activeTabId ||
                !string.Equals(active.Folder, folder, StringComparison.OrdinalIgnoreCase)) return;
            _browserEntries = entries;
            BrowserRepeater.ItemsSource = _browserEntries;
            ConfigureBrowserRepeaterLayout();
            SelectBrowserHighlight(active.Id);
            _diagnostics.Write("browser", "managed_population_complete", new { folder, count = entries.Count, virtualized = true, generation });
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private sealed record BrowserEntry(string Path, bool Directory);

    private static List<BrowserEntry> EnumerateBrowserEntries(string folder, CancellationToken token)
    {
        var directories = new List<string>();
        var files = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            token.ThrowIfCancellationRequested();
            directories.Add(directory);
        }
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            token.ThrowIfCancellationRequested();
            files.Add(file);
        }
        directories.Sort((a, b) => Glide.Core.NaturalStringComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        files.Sort(Glide.Core.ImageSortOrder.CreateComparer());
        var entries = new List<BrowserEntry>(directories.Count + files.Count);
        entries.AddRange(directories.Select(path => new BrowserEntry(path, true)));
        entries.AddRange(files.Select(path => new BrowserEntry(path, false)));
        return entries;
    }

    private ListBoxItem CreateBrowserItem(BrowserEntry entry)
    {
        var path = entry.Path;
        var directory = entry.Directory;
        var generation = Volatile.Read(ref _browserGeneration);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_browserThumbnailCts?.Token ?? CancellationToken.None);
        var token = lifetime.Token;
        var compact = _browserViewMode == 2;
        var tileWidth = compact ? 250d : _browserViewMode == 1 ? 126d : 176d;
        var tileHeight = compact ? 42d : _browserViewMode == 1 ? 146d : 188d;
        var previewWidth = _browserViewMode == 1 ? 104 : 152;
        var previewHeight = _browserViewMode == 1 ? 98 : 136;

        Control content;
        Image? thumbnailTarget = null;
        Control? thumbnailFallback = null;
        if (compact)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*"), Margin = new Thickness(2, 0) };
            var icon = new GlideIconView
            {
                Kind = directory ? "Browser" : "Image",
                Width = 18, Height = 18, StrokeWidth = 1.5,
                IconBrush = BrowserBrush("muted"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var text = new TextBlock { Text = Path.GetFileName(path), FontSize = 12.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, 1);
            grid.Children.Add(icon); grid.Children.Add(text);
            content = grid;
        }
        else
        {
            var panel = new StackPanel { Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var previewBorder = new Border
            {
                Width = previewWidth,
                Height = previewHeight,
                CornerRadius = new CornerRadius(6),
                Background = BrowserBrush("surface"),
                BorderBrush = BrowserBrush("border"),
                BorderThickness = new Thickness(1),
                ClipToBounds = true
            };
            if (directory)
            {
                previewBorder.Child = new GlideIconView
                {
                    Kind = "Browser", Width = 48, Height = 48, StrokeWidth = 1.3,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    IconBrush = BrowserBrush("muted")
                };
            }
            else
            {
                // The browser never equates "thumbnail unavailable" with "file unsupported". Core
                // raster formats use Avalonia's lightweight thumbnail path; provider/specialist formats
                // retain an honest image icon and still open through the authoritative viewer backend.
                var fallbackIcon = new GlideIconView
                {
                    Kind = "Image", Width = 42, Height = 42, StrokeWidth = 1.2,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    IconBrush = BrowserBrush("muted")
                };
                if (ImageNavigator.IsSupported(path) && CanUseBrowserThumbnailDecoder(path))
                {
                    var previewStack = new Grid();
                    thumbnailTarget = new Image
                    {
                        Tag = path,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                    };
                    previewStack.Children.Add(fallbackIcon);
                    previewStack.Children.Add(thumbnailTarget);
                    previewBorder.Child = previewStack;
                    thumbnailFallback = fallbackIcon;
                }
                else
                {
                    previewBorder.Child = fallbackIcon;
                }
            }
            panel.Children.Add(previewBorder);
            panel.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(path), FontSize = 12.5, Width = previewWidth,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2
            });
            content = panel;
        }

        var item = new ListBoxItem
        {
            Tag = path,
            IsSelected = _browserSelection.Contains(path),
            Content = content,
            Width = tileWidth,
            Height = tileHeight,
            Margin = new Thickness(0),
            Padding = compact ? new Thickness(7, 4) : new Thickness(7),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = compact ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Center
        };
        item.Classes.Add("browserItem");
        _browserRealizationCts[item] = lifetime;
        item.PointerPressed += BrowserItemPointerPressed;
        item.DoubleTapped += BrowserItemDoubleTapped;
        item.ContextMenu = BuildBrowserItemContextMenu(path, directory);
        // Expensive file metadata is loaded off the UI thread only for realized tiles.
        ToolTip.SetTip(item, path);
        ToolTip.SetShowDelay(item, 500);
        _ = LoadBrowserMetadataTooltipAsync(path, directory, item, token);

        if (thumbnailTarget is not null)
            _ = LoadBrowserThumbnailAsync(path, thumbnailTarget, thumbnailFallback, previewWidth, token, generation);
        return item;
    }

    private void ConfigureBrowserRepeaterLayout()
    {
        var compact = _browserViewMode == 2;
        BrowserRepeater.Layout = new UniformGridLayout
        {
            Orientation = Orientation.Horizontal,
            MinItemWidth = compact ? 250 : _browserViewMode == 1 ? 126 : 176,
            MinItemHeight = compact ? 42 : _browserViewMode == 1 ? 146 : 188,
            MinColumnSpacing = 6,
            MinRowSpacing = 6
        };
    }

    private double BrowserItemStrideWidth() => (_browserViewMode == 2 ? 250d : _browserViewMode == 1 ? 126d : 176d) + 6d;

    private int BrowserEstimatedColumnCount()
    {
        var width = Math.Max(1, BrowserScrollViewer.Bounds.Width - 20);
        return Math.Max(1, (int)(width / BrowserItemStrideWidth()));
    }

    private void SelectBrowserIndex(int index, bool extend, bool toggle, bool bringIntoView)
    {
        if ((uint)index >= (uint)_browserEntries.Count) return;
        BrowserSelectionPolicy.Apply(_browserEntries.Count, i => _browserEntries[i].Path, _browserSelection,
            ref _browserSelectionAnchor, ref _browserCurrentIndex, index, extend, toggle);
        UpdateRealizedBrowserSelection();
        if (bringIntoView)
        {
            try { BrowserRepeater.GetOrCreateElement(index).BringIntoView(); } catch { }
        }
    }

    private void UpdateRealizedBrowserSelection()
    {
        foreach (var item in BrowserRepeater.GetVisualDescendants().OfType<ListBoxItem>())
            item.IsSelected = item.Tag is string path && _browserSelection.Contains(path);
    }

    private void BrowserRepeaterElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        // FuncDataTemplate is configured without recycling; ElementPrepared still re-applies model
        // selection after realization so off-screen selection cannot depend on stale visual state.
        if (e.Element is not ListBoxItem item) return;
        var path = item.Tag as string;
        item.IsSelected = path is not null && _browserSelection.Contains(path);
    }

    private void BrowserRepeaterElementClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
    {
        // The template is non-recycled but the element instance can still clear repeatedly: detach
        // per-item handlers so a cleared tile cannot fire stale pointer routing, then cancel/dispose
        // its CTS (dict removal is the single ownership handoff) before dropping bitmap pixels.
        e.Element.PointerPressed -= BrowserItemPointerPressed;
        e.Element.DoubleTapped -= BrowserItemDoubleTapped;
        if (_browserRealizationCts.Remove(e.Element, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        foreach (var image in e.Element.GetVisualDescendants().OfType<Image>())
        {
            if (image.Source is IDisposable disposable) disposable.Dispose();
            image.Source = null;
        }
    }

    private void CancelAllBrowserRealizations()
    {
        foreach (var cts in _browserRealizationCts.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _browserRealizationCts.Clear();
        if (_browserSurface is null) return;
        foreach (var image in _browserSurface.Repeater.GetVisualDescendants().OfType<Image>())
        {
            if (image.Source is IDisposable disposable) disposable.Dispose();
            image.Source = null;
        }
    }

    private static bool CanUseBrowserThumbnailDecoder(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff";

    private async Task LoadBrowserMetadataTooltipAsync(string path, bool directory, Control target, CancellationToken token)
    {
        if (directory) return;
        try
        {
            var tip = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                return $"{path}\n{info.Length / 1024d / 1024d:F2} MB\nModified {info.LastWriteTime:g}";
            }, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested) ToolTip.SetTip(target, tip);
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task LoadBrowserThumbnailAsync(string path, Image target, Control? fallback, int width, CancellationToken token, long generation)
    {
        var entered = false;
        try
        {
            await _browserThumbnailGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            // Thumbnail work is deliberately independent from the foreground image pipeline: opening
            // a browser tab must never steal the decode generation or cancel an image-view request.
            var bitmap = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024, FileOptions.SequentialScan);
                return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.LowQuality);
            }, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || generation != Volatile.Read(ref _browserGeneration)) { bitmap.Dispose(); return; }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || generation != Volatile.Read(ref _browserGeneration) ||
                    !string.Equals(target.Tag as string, path, StringComparison.OrdinalIgnoreCase)) { bitmap.Dispose(); return; }
                if (target.Source is IDisposable previous) previous.Dispose();
                target.Source = bitmap;
                if (fallback is not null) fallback.IsVisible = false;
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { if (entered) _browserThumbnailGate.Release(); }
    }

    private ContextMenu BuildBrowserItemContextMenu(string path, bool directory)
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        var open = new MenuItem { Header = directory ? "Open folder" : "Open", Icon = MenuIcon("Open") };
        open.Click += async (_, _) =>
        {
            if (directory && Directory.Exists(path)) NavigateBrowserTo(path, addHistory: true);
            else if (File.Exists(path) && ImageNavigator.IsSupported(path)) await OpenAsImageTabAsync(path);
            else OpenWithWindowsShell(path);
        };
        items.Add(open);
        if (OperatingSystem.IsWindows())
        {
            var reveal = new MenuItem { Header = "Show in Windows Explorer", Icon = MenuIcon("FolderLocate") };
            reveal.Click += (_, _) => RevealInWindowsExplorer(path, directory);
            items.Add(reveal);
        }
        var copy = new MenuItem { Header = "Copy path", Icon = MenuIcon("Copy") };
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(path);
        };
        items.Add(copy);
        var delete = new MenuItem { Header = "Delete to Recycle Bin", Icon = MenuIcon("Delete") };
        delete.Click += (_, _) => DeleteBrowserPath(path, directory);
        items.Add(delete);
        if (!directory && File.Exists(path) && ImageNavigator.IsSupported(path))
        {
            var print = new MenuItem { Header = "Print…", Icon = MenuIcon("Print") };
            print.Click += async (_, _) => await PrintFileAsync(path);
            items.Add(print);
        }
        items.Add(new MenuItem { Header = "-" });
        AppendWholeAppOverlayMenuItems(items);
        menu.ItemsSource = items;
        return PrepareOwnedContextMenu(menu);
    }

    // Shell-launch hardening: reject embedded quotes/CR/LF so a crafted file name cannot break
    // out of quoting into extra explorer switches. explorer.exe is driven via ArgumentList (no
    // manual quote-joining); valid paths behave exactly as before. Callers keep their existing
    // File.Exists/IsSupported gates — this only filters unsafe strings.
    private static bool IsSafeShellPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.IndexOfAny(new[] { '"', '\r', '\n' }) < 0;

    private static void OpenWithWindowsShell(string path)
    {
        if (!OperatingSystem.IsWindows() || (!File.Exists(path) && !Directory.Exists(path)) || !IsSafeShellPath(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    private static void RevealInWindowsExplorer(string path, bool directory)
    {
        if (!OperatingSystem.IsWindows() || !IsSafeShellPath(path)) return;
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            if (directory) psi.ArgumentList.Add(path);
            else psi.ArgumentList.Add("/select," + path);
            Process.Start(psi);
        }
        catch { }
    }

    private bool DeleteBrowserPath(string path, bool directory, bool refresh = true)
    {
        try
        {
            if (directory && Directory.Exists(path))
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else if (File.Exists(path))
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else return false;
            if (refresh && _workspace.Active is BrowserTabState browser) RefreshManagedBrowser(browser.Folder);
            return true;
        }
        catch (Exception ex)
        {
            _diagnostics.Write("browser", "delete_failed", new { path, error = ex.GetType().Name, ex.Message });
            return false;
        }
    }

    private async void BrowserViewModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_browserUiReady || sender is not ComboBox combo || combo.SelectedIndex < 0) return;

        var requestedMode = combo.SelectedIndex;
        var selectedPath = _browserCurrentIndex >= 0 && _browserCurrentIndex < _browserEntries.Count
            ? _browserEntries[_browserCurrentIndex].Path
            : _browserSelection.FirstOrDefault();

        // Retire the complete old visual generation before changing geometry. Reusing a measured
        // ItemsRepeater tree across Large/Medium/Compact was the source of overlapping/"melted"
        // icons after repeated mode changes.
        // Do not advance _browserGeneration here: that generation owns folder-population races.
        // Realization cancellation is sufficient for view-mode work and avoids cancelling an
        // in-flight population of the current folder.
        CancelAllBrowserRealizations();
        BrowserRepeater.ItemsSource = null;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        if (!_browserUiReady || !ReferenceEquals(sender, BrowserViewModeCombo) || combo.SelectedIndex != requestedMode) return;
        _browserViewMode = requestedMode;
        BrowserRepeater.ItemTemplate = new FuncDataTemplate<BrowserEntry>((entry, _) => CreateBrowserItem(entry), supportsRecycling: false);
        ConfigureBrowserRepeaterLayout();
        BrowserRepeater.ItemsSource = _browserEntries;

        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            var index = _browserEntries.FindIndex(entry => string.Equals(entry.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) SelectBrowserIndex(index, extend: false, toggle: false, bringIntoView: true);
        }

        _diagnostics.Write("browser", "view_mode_rebuilt", new { mode = _browserViewMode, count = _browserEntries.Count });
    }

    private void BrowserWindowsExplorerClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is BrowserTabState browser) RevealInWindowsExplorer(browser.Folder, directory: true);
    }

    private async void BrowserListKeyDown(object? sender, KeyEventArgs e)
    {
        var selected = _browserSelection.ToArray();
        if (e.Key == Key.Enter && selected.Length == 1)
        {
            var openPath = selected[0];
            if (Directory.Exists(openPath)) NavigateBrowserTo(openPath, addHistory: true);
            else if (File.Exists(openPath) && ImageNavigator.IsSupported(openPath)) await OpenAsImageTabAsync(openPath);
            else OpenWithWindowsShell(openPath);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete)
        {
            var changed = false;
            foreach (var path in selected)
                changed |= DeleteBrowserPath(path, Directory.Exists(path), refresh: false);
            if (changed && _workspace.Active is BrowserTabState browser) RefreshManagedBrowser(browser.Folder);
            e.Handled = selected.Length > 0;
            return;
        }
        if (_browserEntries.Count == 0) return;
        var current = _browserCurrentIndex >= 0 ? _browserCurrentIndex : _browserSelectionAnchor >= 0 ? _browserSelectionAnchor : 0;
        var next = e.Key switch
        {
            Key.Left => Math.Max(0, current - 1),
            Key.Right => Math.Min(_browserEntries.Count - 1, current + 1),
            Key.Up => Math.Max(0, current - BrowserEstimatedColumnCount()),
            Key.Down => Math.Min(_browserEntries.Count - 1, current + BrowserEstimatedColumnCount()),
            Key.Home => 0,
            Key.End => _browserEntries.Count - 1,
            _ => -1
        };
        if (next < 0) return;
        SelectBrowserIndex(next, extend: e.KeyModifiers.HasFlag(KeyModifiers.Shift), toggle: false, bringIntoView: true);
        e.Handled = true;
    }

    private void BrowserItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBoxItem { Tag: string path }) return;
        var index = _browserEntries.FindIndex(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        SelectBrowserIndex(index,
            extend: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            toggle: e.KeyModifiers.HasFlag(KeyModifiers.Control),
            bringIntoView: false);
        (sender as Control)?.Focus();
    }

    private async void BrowserItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBoxItem { Tag: string path }) return;
        if (Directory.Exists(path)) NavigateBrowserTo(path, addHistory: true);
        else if (File.Exists(path) && ImageNavigator.IsSupported(path)) await OpenAsImageTabAsync(path);
        else if (File.Exists(path)) OpenWithWindowsShell(path);
        e.Handled = true;
    }

    private async void BrowserBackClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is not BrowserTabState browser || !_browserSessions.TryGetValue(browser.Id, out var session)) return;
        if (session.Index > 0)
        {
            CaptureCurrentBrowserSelection(session);
            session.Index--;
            NavigateBrowserTo(session.History[session.Index], addHistory: false);
            return;
        }

        if (_tabForwardImageTargets.TryGetValue(browser.Id, out var imagePath) &&
            File.Exists(imagePath) && ImageNavigator.IsSupported(imagePath))
        {
            _workspace.ReplaceTab(new ImageTabState(browser.Id, imagePath));
            _tabForwardImageTargets.Remove(browser.Id);
            _tabForwardFolderTargets.Remove(browser.Id);
            _browserHighlightTargets.Remove(browser.Id);
            _browserSessions.Remove(browser.Id);
            await ActivateWorkspaceAsync();
            UpdateTabNavigationButtons();
            _diagnostics.Write("browser", "back_to_image", new { path = imagePath, tab = browser.Id });
        }
    }

    private void BrowserForwardClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is not BrowserTabState browser || !_browserSessions.TryGetValue(browser.Id, out var session) || session.Index >= session.History.Count - 1) return;
        CaptureCurrentBrowserSelection(session);
        session.Index++;
        NavigateBrowserTo(session.History[session.Index], addHistory: false);
    }

    private void BrowserUpClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is not BrowserTabState browser) return;
        var child = browser.Folder;
        var parent = Directory.GetParent(child)?.FullName;
        if (parent is not null) NavigateBrowserTo(parent, addHistory: true, highlightPath: child);
    }

    private void BrowserRefreshClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is BrowserTabState browser) NavigateBrowserTo(browser.Folder, addHistory: false);
    }

    private void BrowserAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var folder = BrowserAddressBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) NavigateBrowserTo(folder, addHistory: true);
        e.Handled = true;
    }
}
