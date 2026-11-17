using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Explorer;
using Glide.App.Platform;
using Glide.App.Settings;
using Glide.App.Services;
using Glide.Core;
using Glide.Core.Commands;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// Managed Explorer tab, part 1: folder population, tile/row creation, selection, view/sort/filter,
/// thumbnails and per-tab view state. Input is in MainWindow.ExplorerInput.cs, menus in
/// MainWindow.ExplorerMenus.cs and file operations in MainWindow.ExplorerOps.cs.
/// </summary>
public partial class MainWindow
{
    private const double ExplorerGridSpacing = 6;
    private const double ExplorerScrollPadding = 10;

    /// <summary>Per-tab Explorer view state: survives switching to another tab and back.</summary>
    private sealed class ExplorerTabView
    {
        public string Folder = "";
        public int ViewMode = ExplorerViewModes.Large;
        public ExplorerSortKey SortKey = ExplorerSortKey.Name;
        public bool SortAscending = true;
        public bool FoldersFirst = true;
        public string FilterText = "";
        public List<string> Selection = new();
        public int CurrentIndex = -1;
        public int AnchorIndex = -1;
        public double ScrollY;
    }

    private readonly Dictionary<Guid, ExplorerTabView> _explorerTabViews = new();
    private List<ExplorerEntry> _explorerAllEntries = new();
    private ExplorerSortKey _explorerSortKey = ExplorerSortKey.Name;
    private bool _explorerSortAscending = true;
    private bool _explorerFoldersFirst = true;
    private string _explorerFilterText = "";
    private string? _explorerLoadedFolder;
    private Guid _explorerDisplayedTabId;
    private Task _explorerRefreshTask = Task.CompletedTask;
    private long _explorerViewVersion;
    private bool _explorerApplyingState;
    private List<string>? _explorerPendingSelection;
    private string? _explorerPendingRename;
    private double? _explorerPendingScrollY;
    private string? _explorerPendingEnsureVisiblePath;
    private readonly HashSet<string> _explorerCutPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly double[] _explorerColumnWidths = { 320, 160, 130, 90 };
    private bool _explorerListHasFocus;

    // ------------------------------------------------------------------------------------------
    // Population
    // ------------------------------------------------------------------------------------------

    private void CancelBrowserThumbnailWork()
    {
        _browserThumbnailCts?.Cancel();
        _browserThumbnailCts?.Dispose();
        _browserThumbnailCts = null;
        CancelAllBrowserRealizations();
    }

    /// <summary>Captures the displayed Explorer tab's view state (selection, scroll, view, sort, filter).</summary>
    private void CaptureExplorerTabView()
    {
        if (_explorerDisplayedTabId == Guid.Empty || _browserSurface is null) return;
        var view = new ExplorerTabView
        {
            Folder = _explorerLoadedFolder ?? "",
            ViewMode = _browserViewMode,
            SortKey = _explorerSortKey,
            SortAscending = _explorerSortAscending,
            FoldersFirst = _explorerFoldersFirst,
            FilterText = _explorerFilterText,
            Selection = _browserSelection.ToList(),
            CurrentIndex = _browserCurrentIndex,
            AnchorIndex = _browserSelectionAnchor,
            ScrollY = _browserSurface.ScrollViewer.Offset.Y
        };
        if (_explorerPendingScrollY is { } pending) view.ScrollY = pending;
        _explorerTabViews[_explorerDisplayedTabId] = view;
    }

    /// <summary>Makes the shared surface show the given Explorer tab: restores its view state or starts from the defaults.</summary>
    private void ApplyExplorerTabView(BrowserTabState browser, bool applySavedView = true)
    {
        var surface = EnsureBrowserSurface();
        if (_explorerDisplayedTabId == browser.Id && _explorerLoadedFolder is not null) return;
        _explorerDisplayedTabId = browser.Id;
        _explorerApplyingState = true;
        try
        {
            if (_explorerTabViews.TryGetValue(browser.Id, out var view))
            {
                _browserViewMode = view.ViewMode;
                _explorerSortKey = view.SortKey;
                _explorerSortAscending = view.SortAscending;
                _explorerFoldersFirst = view.FoldersFirst;
                _explorerFilterText = view.FilterText;
                _browserSelection.Clear();
                foreach (var path in view.Selection) _browserSelection.Add(path);
                _browserCurrentIndex = view.CurrentIndex;
                _browserSelectionAnchor = view.AnchorIndex;
                _explorerPendingScrollY = view.ScrollY;
                _explorerLoadedFolder = string.IsNullOrEmpty(view.Folder) ? null : view.Folder;
            }
            else
            {
                _browserViewMode = ExplorerViewModes.IndexOf(_settings.ExplorerDefaultView);
                _explorerSortKey = ExplorerSortKey.Name;
                _explorerSortAscending = true;
                _explorerFoldersFirst = true;
                _explorerFilterText = "";
                _browserSelection.Clear();
                _browserCurrentIndex = -1;
                _browserSelectionAnchor = -1;
                _explorerPendingScrollY = null;
                _explorerLoadedFolder = null;
                // First time this window shows the tab: reopen it the way the saved session left it.
                if (applySavedView && browser.View is { } saved)
                {
                    if (!string.IsNullOrWhiteSpace(saved.SelectedPath) && (File.Exists(saved.SelectedPath) || Directory.Exists(saved.SelectedPath)))
                    {
                        _explorerPendingSelection = new List<string> { saved.SelectedPath };
                        _explorerPendingEnsureVisiblePath = saved.SelectedPath;
                    }
                    if (saved.ScrollY > 0) _explorerPendingScrollY = saved.ScrollY;
                }
            }
            surface.ViewModeCombo.SelectedIndex = _browserViewMode;
            surface.FilterBox.Text = _explorerFilterText;
            UpdateExplorerSortButton();
        }
        finally { _explorerApplyingState = false; }
        ConfigureBrowserRepeaterLayout();
    }

    /// <summary>Populates the Explorer for <paramref name="folder"/>. Selection is kept when the same folder is reloaded.</summary>
    private void RefreshManagedBrowser(string folder)
    {
        _explorerRefreshTask = RefreshManagedBrowserAsync(folder);
    }

    private async Task RefreshManagedBrowserAsync(string folder)
    {
        // A folder change or reload cancels a pending click-to-rename.
        CancelExplorerClickRename();
        _browserThumbnailCts?.Cancel();
        _browserThumbnailCts?.Dispose();
        _browserThumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
        var token = _browserThumbnailCts.Token;
        var generation = Interlocked.Increment(ref _browserGeneration);
        var activeTabId = (_workspace.Active as BrowserTabState)?.Id ?? Guid.Empty;
        CancelAllBrowserRealizations();
        var sameFolder = string.Equals(_explorerLoadedFolder, folder, StringComparison.OrdinalIgnoreCase);
        var carried = _explorerPendingSelection ?? (sameFolder ? _browserSelection.ToList() : null);
        _explorerPendingSelection = null;
        var ensureVisiblePath = _explorerPendingEnsureVisiblePath;
        _explorerPendingEnsureVisiblePath = null;
        var carriedCurrent = sameFolder && _browserCurrentIndex >= 0 && _browserCurrentIndex < _browserEntries.Count
            ? _browserEntries[_browserCurrentIndex].Path : null;
        if (!sameFolder)
        {
            // Navigating elsewhere starts clean: Explorer also clears its search box.
            _explorerFilterText = "";
            SetExplorerFilterBoxText("");
        }
        _browserSelection.Clear();
        _browserSelectionAnchor = -1;
        _browserCurrentIndex = -1;
        // Never mutate the list currently owned by ItemsRepeater (see ApplyExplorerViewAsync).
        _browserEntries = new List<ExplorerEntry>();
        ApplyManagedExplorerTheme();
        BrowserRepeater.ItemsSource = _browserEntries;
        UpdateExplorerStatus();
        try
        {
            // ItemsRepeater clears realized visuals at a render boundary. Let that pass complete
            // before assigning a different folder source, otherwise old tiles can be reused under
            // new indices while Up/Back/Refresh requests overlap.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (generation != Volatile.Read(ref _browserGeneration) ||
                _workspace.Active is not BrowserTabState current || current.Id != activeTabId ||
                !string.Equals(current.Folder, folder, StringComparison.OrdinalIgnoreCase)) return;
            var showHidden = _settings.ExplorerShowHiddenFiles;
            var onlyImages = _settings.ExplorerShowOnlyImages;
            // Enumeration/sorting is the only O(N) folder operation. UI controls, context menus,
            // tooltips and thumbnail decoders are created strictly by ItemsRepeater realization.
            var entries = await Task.Run(() => ExplorerModel.Enumerate(folder, showHidden, onlyImages, token), token);
            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _browserGeneration) ||
                _workspace.Active is not BrowserTabState active || active.Id != activeTabId ||
                !string.Equals(active.Folder, folder, StringComparison.OrdinalIgnoreCase)) return;
            _explorerAllEntries = entries;
            _explorerLoadedFolder = folder;
            _browserEntries = ComputeExplorerDisplayList();
            if (carried is not null)
                foreach (var path in carried)
                    if (_browserEntries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))) _browserSelection.Add(path);
            if (carriedCurrent is not null)
                _browserCurrentIndex = _browserEntries.FindIndex(e => string.Equals(e.Path, carriedCurrent, StringComparison.OrdinalIgnoreCase));
            if (_browserSelection.Count > 0 && _browserCurrentIndex < 0)
                _browserCurrentIndex = _browserEntries.FindIndex(e => _browserSelection.Contains(e.Path));
            if (_browserSelectionAnchor < 0) _browserSelectionAnchor = _browserCurrentIndex;
            BrowserRepeater.ItemsSource = _browserEntries;
            ConfigureBrowserRepeaterLayout();
            UpdateExplorerStatus();
            SelectBrowserHighlight(active.Id);
            if (_explorerPendingRename is { } renamePath)
            {
                _explorerPendingRename = null;
                var index = _browserEntries.FindIndex(e => string.Equals(e.Path, renamePath, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    SelectBrowserIndex(index, extend: false, toggle: false, bringIntoView: true);
                    BeginExplorerRename(index);
                }
            }
            if (_explorerPendingScrollY is not null || ensureVisiblePath is not null)
            {
                var restoredScroll = _explorerPendingScrollY;
                _explorerPendingScrollY = null;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                if (generation == Volatile.Read(ref _browserGeneration))
                {
                    if (restoredScroll is { } y)
                    {
                        // A just-created surface may not have measured its extent yet; the offset would clamp to 0.
                        if (ensureVisiblePath is not null && BrowserScrollViewer.Extent.Height < y + BrowserScrollViewer.Viewport.Height) BrowserScrollViewer.UpdateLayout();
                        BrowserScrollViewer.Offset = new Vector(BrowserScrollViewer.Offset.X, y);
                    }
                    // Session restore: the remembered item is brought into view even if the remembered scroll missed it.
                    if (ensureVisiblePath is not null)
                    {
                        var index = _browserEntries.FindIndex(e => string.Equals(e.Path, ensureVisiblePath, StringComparison.OrdinalIgnoreCase));
                        if (index >= 0) EnsureExplorerIndexVisible(index);
                    }
                }
            }
            _diagnostics.Write("browser", "managed_population_complete", new { folder, count = entries.Count, virtualized = true, generation });
        }
        catch (OperationCanceledException) { /* cancellation is the expected way to stop this work */ }
        catch (UnauthorizedAccessException ex) { ShowExplorerMessage($"Can't open this folder: {ex.Message}", isError: true); }
        catch (IOException ex) { ShowExplorerMessage($"Can't read this folder: {ex.Message}", isError: true); }
    }

    private List<ExplorerEntry> ComputeExplorerDisplayList() =>
        ExplorerModel.Sort(ExplorerModel.Filter(_explorerAllEntries, _explorerFilterText), _explorerSortKey, _explorerSortAscending, _explorerFoldersFirst);

    /// <summary>
    /// Re-sorts / re-filters / re-lays out the already enumerated folder (view, sort or filter changed).
    /// Retires the realized tiles first so a measured tree is never reused across geometries.
    /// <paramref name="revealCurrent"/> false (a sort change with Keep selection in view off) keeps the scroll position
    /// instead of scrolling to the selected item.
    /// </summary>
    private async Task ApplyExplorerViewAsync(bool revealCurrent = true)
    {
        var keptOffset = _browserSurface?.ScrollViewer.Offset;
        var version = Interlocked.Increment(ref _explorerViewVersion);
        var generation = Volatile.Read(ref _browserGeneration);
        var currentPath = _browserCurrentIndex >= 0 && _browserCurrentIndex < _browserEntries.Count ? _browserEntries[_browserCurrentIndex].Path : null;
        CancelAllBrowserRealizations();
        BrowserRepeater.ItemsSource = null;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        if (version != Volatile.Read(ref _explorerViewVersion) || generation != Volatile.Read(ref _browserGeneration)) return;
        _browserEntries = ComputeExplorerDisplayList();
        // Keep only the selection that is still listed.
        var listed = new HashSet<string>(_browserEntries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        _browserSelection.RemoveWhere(p => !listed.Contains(p));
        _browserCurrentIndex = currentPath is null ? -1 : _browserEntries.FindIndex(e => string.Equals(e.Path, currentPath, StringComparison.OrdinalIgnoreCase));
        _browserSelectionAnchor = _browserCurrentIndex;
        ConfigureBrowserRepeaterLayout();
        UpdateExplorerDetailsHeader();
        UpdateExplorerSortButton();
        BrowserRepeater.ItemsSource = _browserEntries;
        UpdateExplorerStatus();
        if (revealCurrent)
        {
            if (_browserCurrentIndex >= 0) EnsureExplorerIndexVisible(_browserCurrentIndex);
        }
        else if (keptOffset is { } offset && _browserSurface is { } surface)
        {
            surface.ScrollViewer.UpdateLayout();
            surface.ScrollViewer.Offset = offset;
        }
    }

    private void SetExplorerFilterBoxText(string text)
    {
        if (_browserSurface is null) return;
        _explorerApplyingState = true;
        try { _browserSurface.FilterBox.Text = text; }
        finally { _explorerApplyingState = false; }
    }

    // ------------------------------------------------------------------------------------------
    // Tiles
    // ------------------------------------------------------------------------------------------

    private static string ExplorerGlyphKind(ExplorerEntry entry) => entry.IsDirectory ? "Folder" : entry.IsImage ? "Image" : "FileGeneric";

    /// <summary>A tile remembers which view mode its content was built for.</summary>
    private sealed class ExplorerTile : Border
    {
        public int BuiltMode = -1;
    }

    private Border CreateExplorerTile(ExplorerEntry entry)
    {
        var tile = new ExplorerTile
        {
            Background = Brushes.Transparent, // a null background would make the tile invisible to hit-testing
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        tile.Classes.Add("exTile");
        // Folder tiles accept Explorer drags (the handlers look the entry up so a reused element stays correct).
        AttachExplorerDropTarget(tile, () => tile.Tag is ExplorerEntry { IsDirectory: true } folder ? folder.Path : null, "drop");
        PopulateExplorerTile(tile, entry);
        return tile;
    }

    /// <summary>
    /// (Re)builds a tile for an entry. ItemsRepeater may hand an already realized element to a different data
    /// item after the source was replaced, without invoking the data template again, so ElementPrepared calls
    /// this whenever the element's entry is not the one being shown.
    /// </summary>
    private void PopulateExplorerTile(Border tile, ExplorerEntry entry)
    {
        if (_browserRealizationCts.Remove(tile, out var previousLifetime)) { previousLifetime.Cancel(); previousLifetime.Dispose(); }
        foreach (var image in tile.GetVisualDescendants().OfType<Image>())
        {
            if (image.Source is IDisposable disposable) disposable.Dispose();
            image.Source = null;
        }
        tile.Classes.Set("selected", false);
        tile.Classes.Set("focused", false);
        tile.Classes.Set("cut", false);
        var path = entry.Path;
        var directory = entry.IsDirectory;
        var generation = Volatile.Read(ref _browserGeneration);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_browserThumbnailCts?.Token ?? CancellationToken.None);
        var token = lifetime.Token;
        var mode = _browserViewMode;
        var (tileWidth, tileHeight) = ExplorerViewModes.TileSize(mode);
        var muted = BrowserBrush("muted");

        Image? thumbnailTarget = null;
        Control? thumbnailFallback = null;
        var thumbnailWidth = 0;
        Control content;
        var nameBlock = new TextBlock
        {
            Text = entry.Name, FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Tag = "name"
        };
        nameBlock.Classes.Add("exName");
        var nameHost = new Panel { Name = "NameHost", Tag = "namehost" };
        nameHost.Children.Add(nameBlock);

        if (ExplorerViewModes.IsIconMode(mode))
        {
            var (previewWidth, previewHeight) = ExplorerViewModes.PreviewSize(mode);
            nameBlock.TextAlignment = TextAlignment.Center;
            nameBlock.MaxLines = 2;
            nameBlock.TextWrapping = TextWrapping.Wrap;
            nameHost.Width = previewWidth;
            nameHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            var panel = new StackPanel { Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var previewBorder = new Border
            {
                Width = previewWidth,
                Height = previewHeight,
                CornerRadius = new CornerRadius(6),
                Background = BrowserBrush("surface"),
                BorderBrush = BrowserBrush("border"),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                IsHitTestVisible = false
            };
            var glyph = new GlideIconView
            {
                Kind = ExplorerGlyphKind(entry),
                Width = directory ? 56 : 44, Height = directory ? 56 : 44, StrokeWidth = 1.3,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                IconBrush = directory ? BrowserFolderBrush() : muted
            };
            if (!directory && entry.IsImage && CanUseBrowserThumbnailDecoder(path))
            {
                var stack = new Grid();
                thumbnailTarget = new Image
                {
                    Tag = path,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                };
                stack.Children.Add(glyph);
                stack.Children.Add(thumbnailTarget);
                previewBorder.Child = stack;
                thumbnailFallback = glyph;
                thumbnailWidth = previewWidth;
            }
            else previewBorder.Child = glyph;
            panel.Children.Add(previewBorder);
            panel.Children.Add(nameHost);
            content = panel;
        }
        else if (mode == ExplorerViewModes.Small)
        {
            var (previewWidth, previewHeight) = ExplorerViewModes.PreviewSize(mode);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{previewWidth + 8},*"), Margin = new Thickness(2, 0) };
            var host = new Grid { Width = previewWidth, Height = previewHeight, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, IsHitTestVisible = false, ClipToBounds = true };
            var glyph = new GlideIconView
            {
                Kind = ExplorerGlyphKind(entry), Width = 20, Height = 20, StrokeWidth = 1.5,
                IconBrush = directory ? BrowserFolderBrush() : muted,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            host.Children.Add(glyph);
            if (!directory && entry.IsImage && CanUseBrowserThumbnailDecoder(path))
            {
                thumbnailTarget = new Image { Tag = path, Stretch = Stretch.UniformToFill };
                host.Children.Add(thumbnailTarget);
                thumbnailFallback = glyph;
                thumbnailWidth = previewWidth;
            }
            Grid.SetColumn(nameHost, 1);
            grid.Children.Add(host);
            grid.Children.Add(nameHost);
            content = grid;
        }
        else if (mode == ExplorerViewModes.List)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*"), Margin = new Thickness(4, 0) };
            var glyph = new GlideIconView
            {
                Kind = ExplorerGlyphKind(entry), Width = 16, Height = 16, StrokeWidth = 1.5,
                IconBrush = directory ? BrowserFolderBrush() : muted,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, IsHitTestVisible = false
            };
            Grid.SetColumn(nameHost, 1);
            grid.Children.Add(glyph);
            grid.Children.Add(nameHost);
            content = grid;
        }
        else
        {
            var grid = new Grid { Margin = new Thickness(4, 0) };
            for (var i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(_explorerColumnWidths[i], GridUnitType.Pixel));
            var nameCell = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*") };
            var glyph = new GlideIconView
            {
                Kind = ExplorerGlyphKind(entry), Width = 16, Height = 16, StrokeWidth = 1.5,
                IconBrush = directory ? BrowserFolderBrush() : muted,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, IsHitTestVisible = false
            };
            Grid.SetColumn(nameHost, 1);
            nameCell.Children.Add(glyph);
            nameCell.Children.Add(nameHost);
            grid.Children.Add(nameCell);
            TextBlock Cell(string text, int column, bool right = false)
            {
                var tb = new TextBlock
                {
                    Text = text, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(8, 0),
                    TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, IsHitTestVisible = false
                };
                tb.Classes.Add("exMuted");
                Grid.SetColumn(tb, column);
                return tb;
            }
            grid.Children.Add(Cell(ExplorerModel.FormatDate(entry.Modified), 1));
            grid.Children.Add(Cell(entry.TypeName, 2));
            grid.Children.Add(Cell(directory ? "" : ExplorerModel.FormatSize(entry.Size), 3, right: true));
            grid.Width = _explorerColumnWidths.Sum() + 8;
            content = grid;
        }

        var fill = new Border
        {
            Name = "SelectionFill", CornerRadius = new CornerRadius(5), IsHitTestVisible = false,
            Background = (IBrush?)this.FindResource("BrushAccent") ?? Brushes.DodgerBlue
        };
        fill.Classes.Add("exFill");
        var root = new Grid();
        root.Children.Add(fill);
        root.Children.Add(content);
        var compact = !ExplorerViewModes.IsIconMode(mode);
        tile.Tag = entry;
        if (tile is ExplorerTile built) built.BuiltMode = mode;
        tile.Child = root;
        tile.Padding = compact ? new Thickness(5, 2) : new Thickness(7);
        tile.Height = tileHeight;
        tile.Width = !double.IsNaN(tileWidth) ? tileWidth : _explorerColumnWidths.Sum() + 24;
        _browserRealizationCts[tile] = lifetime;
        // The listing already carries size and modified time: build the tooltip here instead of one
        // thread-pool FileInfo query plus one UI-thread post per realized tile (70+ per screen).
        ToolTip.SetTip(tile, BrowserTooltipText(entry));
        ToolTip.SetShowDelay(tile, 500);
        UpdateExplorerTileVisual(tile);

        if (thumbnailTarget is not null)
            _ = LoadBrowserThumbnailAsync(path, thumbnailTarget, thumbnailFallback, thumbnailWidth, token, generation);
    }

    private IBrush BrowserFolderBrush() => new SolidColorBrush(Color.Parse(
        string.Equals(_settings.ExplorerThemeChoice == "Follow Glide theme" ? _settings.ThemeChoice : _settings.ExplorerThemeChoice, "Dark", StringComparison.OrdinalIgnoreCase)
            ? "#E8B84A" : "#C8921E"));

    private void UpdateExplorerTileVisual(Border tile)
    {
        if (tile.Tag is not ExplorerEntry entry) return;
        tile.Classes.Set("selected", _browserSelection.Contains(entry.Path));
        tile.Classes.Set("focused", ExplorerShowsFocusBorder(entry));
        tile.Classes.Set("cut", _explorerCutPaths.Contains(entry.Path));
    }

    /// <summary>
    /// The focus border: on the focused item while the list has the keyboard focus, and on the item an open context menu
    /// acts on (the menu takes the keyboard focus, so the list alone would lose the border on right-click).
    /// </summary>
    private bool ExplorerShowsFocusBorder(ExplorerEntry entry) =>
        _explorerMenuEntry is { } menuEntry
            ? ReferenceEquals(menuEntry, entry)
            : _explorerListHasFocus && ReferenceEquals(ExplorerCurrentEntry, entry);

    private IEnumerable<Border> RealizedExplorerTiles() =>
        _browserSurface is null ? Enumerable.Empty<Border>() : _browserSurface.Repeater.Children.OfType<Border>();

    private Border? ExplorerTileFor(int index)
    {
        if ((uint)index >= (uint)_browserEntries.Count) return null;
        var entry = _browserEntries[index];
        return RealizedExplorerTiles().FirstOrDefault(t => ReferenceEquals(t.Tag, entry));
    }

    private int _explorerAppliedLayoutMode = -1;

    private void ConfigureBrowserRepeaterLayout()
    {
        if (_browserSurface is null) return;
        var (width, height) = ExplorerViewModes.TileSize(_browserViewMode);
        var details = _browserViewMode == ExplorerViewModes.Details;
        // Replacing the layout makes ItemsRepeater re-realize every tile (and drops focus from an inline editor).
        if (_explorerAppliedLayoutMode == _browserViewMode) { UpdateExplorerDetailsHeader(); return; }
        _explorerAppliedLayoutMode = _browserViewMode;
        if (details)
        {
            BrowserRepeater.Layout = new StackLayout { Orientation = Orientation.Vertical, Spacing = 0 };
            BrowserScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        else
        {
            BrowserRepeater.Layout = new UniformGridLayout
            {
                Orientation = Orientation.Horizontal,
                MinItemWidth = width,
                MinItemHeight = height,
                MinColumnSpacing = ExplorerGridSpacing,
                MinRowSpacing = ExplorerGridSpacing
            };
            BrowserScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
        UpdateExplorerDetailsHeader();
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

    private void BrowserRepeaterElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element is not Border tile) return;
        if (BrowserRepeater.ItemsSourceView is { } source && (uint)e.Index < (uint)source.Count &&
            source.GetAt(e.Index) is ExplorerEntry entry &&
            (!ReferenceEquals(tile.Tag, entry) || tile is not ExplorerTile { BuiltMode: var builtMode } || builtMode != _browserViewMode ||
             !_browserRealizationCts.ContainsKey(tile)))
            PopulateExplorerTile(tile, entry);
        UpdateExplorerTileVisual(tile);
        if (_explorerRenamePath is not null && tile.Tag is ExplorerEntry shown &&
            string.Equals(shown.Path, _explorerRenamePath, StringComparison.OrdinalIgnoreCase))
            Dispatcher.UIThread.Post(() => AttachExplorerRenameEditor(tile, shown), DispatcherPriority.Loaded);
    }

    private void BrowserRepeaterElementClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
    {
        // The template is non-recycled but the element instance can still clear repeatedly: cancel
        // and dispose its CTS (dict removal is the single ownership handoff) before dropping bitmap pixels.
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

    private static bool CanUseBrowserThumbnailDecoder(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff";

    /// <summary>Tile tooltip: the path, plus size and modified time for files (same text as before, from the listing).</summary>
    private static string BrowserTooltipText(ExplorerEntry entry) => entry.IsDirectory
        ? entry.Path
        : $"{entry.Path}\n{entry.Size / 1024d / 1024d:F2} MB\nModified {entry.Modified:g}";

    private async Task LoadBrowserThumbnailAsync(string path, Image target, Control? fallback, int width, CancellationToken token, long generation)
    {
        var entered = false;
        try
        {
            await _browserThumbnailGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            // Thumbnail work is deliberately independent from the foreground image pipeline: opening
            // a browser tab must never steal the decode generation or cancel an image-view request.
            Bitmap? bitmap;
            try
            {
                bitmap = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return ThumbnailDecode.DecodeToWidth(path, Math.Max(16, width));
                }, token).ConfigureAwait(false);
            }
            finally
            {
                // Free the decode lane as soon as the pixels exist. The lane used to stay held while the
                // result waited in the UI-thread queue, so a busy UI throttled thumbnail throughput.
                _browserThumbnailGate.Release();
                entered = false;
            }
            if (bitmap is null) return; // too large to decode safely: the tile keeps its file-type icon
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
        catch (OperationCanceledException) { /* cancellation is the expected way to stop this work */ }
        catch { /* cancellation is the expected way to stop this work */ }
        finally { if (entered) _browserThumbnailGate.Release(); }
    }

    // ------------------------------------------------------------------------------------------
    // Layout math (also used by rubber-band selection and keyboard navigation)
    // ------------------------------------------------------------------------------------------

    private bool ExplorerIsDetails => _browserViewMode == ExplorerViewModes.Details;

    private int ExplorerColumnCount()
    {
        if (ExplorerIsDetails) return 1;
        var (width, _) = ExplorerViewModes.TileSize(_browserViewMode);
        var available = _browserSurface is { } s && s.Repeater.Bounds.Width > 0 ? s.Repeater.Bounds.Width
            : Math.Max(1, BrowserScrollViewer.Bounds.Width - ExplorerScrollPadding * 2);
        return Math.Max(1, (int)Math.Floor((available + ExplorerGridSpacing) / (width + ExplorerGridSpacing)));
    }

    /// <summary>Rectangle of a tile in ItemsRepeater coordinates (the coordinate space rubber-band selection uses).</summary>
    private Rect ExplorerTileRect(int index)
    {
        var (width, height) = ExplorerViewModes.TileSize(_browserViewMode);
        if (ExplorerIsDetails)
            return new Rect(0, index * height, _explorerColumnWidths.Sum() + 24, height);
        var columns = ExplorerColumnCount();
        var row = index / columns;
        var column = index % columns;
        return new Rect(column * (width + ExplorerGridSpacing), row * (height + ExplorerGridSpacing), width, height);
    }

    private void EnsureExplorerIndexVisible(int index)
    {
        if ((uint)index >= (uint)_browserEntries.Count || _browserSurface is null) return;
        var scroll = _browserSurface.ScrollViewer;
        var rect = ExplorerTileRect(index);
        var top = rect.Top + ExplorerScrollPadding;
        var bottom = rect.Bottom + ExplorerScrollPadding;
        var viewport = scroll.Viewport.Height;
        if (viewport <= 0) return;
        var y = scroll.Offset.Y;
        if (top < y) y = Math.Max(0, top - ExplorerScrollPadding);
        else if (bottom > y + viewport) y = bottom - viewport + ExplorerScrollPadding;
        if (Math.Abs(y - scroll.Offset.Y) > 0.5) scroll.Offset = new Vector(scroll.Offset.X, y);
    }

    // ------------------------------------------------------------------------------------------
    // Selection
    // ------------------------------------------------------------------------------------------

    private void SelectBrowserIndex(int index, bool extend, bool toggle, bool bringIntoView)
    {
        if ((uint)index >= (uint)_browserEntries.Count) return;
        BrowserSelectionPolicy.Apply(_browserEntries.Count, i => _browserEntries[i].Path, _browserSelection,
            ref _browserSelectionAnchor, ref _browserCurrentIndex, index, extend, toggle);
        UpdateRealizedBrowserSelection();
        if (bringIntoView) EnsureExplorerIndexVisible(index);
    }

    private void SelectAllBrowserEntries()
    {
        _browserSelection.Clear();
        foreach (var entry in _browserEntries) _browserSelection.Add(entry.Path);
        if (_browserEntries.Count > 0)
        {
            if (_browserCurrentIndex < 0) _browserCurrentIndex = 0;
            _browserSelectionAnchor = 0;
        }
        UpdateRealizedBrowserSelection();
    }

    private void ClearBrowserSelection()
    {
        _browserSelection.Clear();
        UpdateRealizedBrowserSelection();
    }

    private void UpdateRealizedBrowserSelection()
    {
        CancelExplorerClickRenameIfSelectionChanged();
        foreach (var tile in RealizedExplorerTiles()) UpdateExplorerTileVisual(tile);
        UpdateExplorerStatus();
        NoteExplorerSessionChanged();
    }

    private List<ExplorerEntry> SelectedExplorerEntries() =>
        _browserEntries.Where(e => _browserSelection.Contains(e.Path)).ToList();

    /// <summary>The focused (current) item of the listing, or null when there is none.</summary>
    private ExplorerEntry? ExplorerCurrentEntry =>
        _browserCurrentIndex >= 0 && _browserCurrentIndex < _browserEntries.Count ? _browserEntries[_browserCurrentIndex] : null;

    private int ExplorerIndexOf(ExplorerEntry entry)
    {
        for (var i = 0; i < _browserEntries.Count; i++)
            if (ReferenceEquals(_browserEntries[i], entry)) return i;
        return -1;
    }

    // ------------------------------------------------------------------------------------------
    // Status line, empty text, navigation buttons
    // ------------------------------------------------------------------------------------------

    private void UpdateExplorerStatus()
    {
        if (_browserSurface is not { } surface) return;
        var count = _browserEntries.Count;
        surface.ItemCountText.Text = count == 1 ? "1 item" : $"{count:N0} items";
        var selected = SelectedExplorerEntries();
        if (selected.Count == 0) surface.SelectionText.Text = "";
        else
        {
            var files = selected.Where(e => !e.IsDirectory).ToList();
            var size = files.Count > 0 && files.Count == selected.Count ? "   " + ExplorerModel.FormatSize(files.Sum(f => f.Size)) : "";
            surface.SelectionText.Text = (selected.Count == 1 ? "1 item selected" : $"{selected.Count:N0} items selected") + size;
        }
        var empty = count == 0 && _explorerLoadedFolder is not null;
        surface.EmptyText.IsVisible = empty;
        if (empty)
            surface.EmptyText.Text = !string.IsNullOrWhiteSpace(_explorerFilterText) ? "No items match your filter."
                : _settings.ExplorerShowOnlyImages ? "No folders or images in this folder." : "This folder is empty.";
    }

    private void ShowExplorerMessage(string text, bool isError = false)
    {
        if (_browserSurface is not { } surface) return;
        surface.MessageText.Text = text;
        surface.MessageText.Foreground = isError ? new SolidColorBrush(Color.Parse("#FF6B6B")) : BrowserBrush("muted");
        ToolTip.SetTip(surface.MessageText, text);
    }

    private void UpdateExplorerNavigationState(string folder)
    {
        if (_browserSurface is not { } surface) return;
        // Back / Forward / history dropdown follow the tab's unified history (MainWindow.History.cs).
        UpdateHistoryNavigationState();
        surface.UpButton.IsEnabled = Directory.GetParent(folder) is not null;
        surface.AddressBar.Path = folder;
        surface.AddressBar.EndEdit();
        RefreshExplorerNavPaneSelection(folder);
    }

    // ------------------------------------------------------------------------------------------
    // Navigation pane (Quick access, This PC)
    // ------------------------------------------------------------------------------------------

    private void BuildExplorerNavPane()
    {
        if (_browserSurface is not { } surface) return;
        var host = surface.NavHost;
        host.Children.Clear();
        void Header(string text)
        {
            var header = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 10, 0, 4), Opacity = 0.75 };
            header.Bind(TextBlock.ForegroundProperty, header.GetResourceObservable("BrushMuted"));
            host.Children.Add(header);
        }
        void Item(ExplorerLocation location)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
            var icon = new GlideIconView { Kind = location.Icon, Width = 16, Height = 16, StrokeWidth = 1.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            icon.Bind(GlideIconView.IconBrushProperty, icon.GetResourceObservable("BrushText"));
            var text = new TextBlock { Text = location.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushText"));
            row.Children.Add(icon);
            row.Children.Add(text);
            var button = new Button { Content = row, Tag = location.Path };
            button.Classes.Add("exNav");
            ToolTip.SetTip(button, location.Path);
            button.Click += (_, _) => NavigateExplorerToLocation(location.Path);
            AttachExplorerDropTarget(button, () => location.Path, "drop");
            host.Children.Add(button);
        }
        var quick = ExplorerLocations.QuickAccess();
        if (quick.Count > 0) { Header("Quick access"); foreach (var location in quick) Item(location); }
        var drives = ExplorerLocations.Drives();
        if (drives.Count > 0) { Header("This PC"); foreach (var drive in drives) Item(drive); }
        if (_workspace.Active is BrowserTabState b) RefreshExplorerNavPaneSelection(b.Folder);
    }

    private void RefreshExplorerNavPaneSelection(string folder)
    {
        if (_browserSurface is not { } surface) return;
        foreach (var button in surface.NavHost.Children.OfType<Button>())
            button.Classes.Set("current", button.Tag is string path && string.Equals(path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    private void NavigateExplorerToLocation(string path)
    {
        if (_workspace.Active is not BrowserTabState || !Directory.Exists(path))
        {
            ShowExplorerMessage($"'{path}' isn't available.", isError: true);
            return;
        }
        NavigateBrowserTo(path, addHistory: true);
        FocusBrowserList();
    }

    // ------------------------------------------------------------------------------------------
    // View mode, sorting, filtering, details header
    // ------------------------------------------------------------------------------------------

    private void BrowserViewModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_explorerApplyingState || !_browserUiReady || sender is not ComboBox combo || combo.SelectedIndex < 0) return;
        SetExplorerViewMode(combo.SelectedIndex);
    }

    private void SetExplorerViewMode(int mode)
    {
        mode = Math.Clamp(mode, 0, ExplorerViewModes.Names.Length - 1);
        if (_browserSurface is { } surface && surface.ViewModeCombo.SelectedIndex != mode)
        {
            _explorerApplyingState = true;
            try { surface.ViewModeCombo.SelectedIndex = mode; }
            finally { _explorerApplyingState = false; }
        }
        if (mode == _browserViewMode) return;
        _browserViewMode = mode;
        // Retire the complete old visual generation before changing geometry. Reusing a measured
        // ItemsRepeater tree across view modes was the source of overlapping/"melted" icons.
        _explorerRefreshTask = ApplyExplorerViewAsync();
        _diagnostics.Write("browser", "view_mode_rebuilt", new { mode = _browserViewMode, count = _browserEntries.Count });
    }

    private void SetExplorerSort(ExplorerSortKey key, bool? ascending = null)
    {
        if (ascending is { } a) _explorerSortAscending = a;
        else if (key == _explorerSortKey) _explorerSortAscending = !_explorerSortAscending;
        else _explorerSortAscending = true;
        _explorerSortKey = key;
        _explorerRefreshTask = ApplyExplorerViewAsync(revealCurrent: _settings.ExplorerKeepSelectionInView);
    }

    private void SetExplorerFoldersFirst(bool value)
    {
        _explorerFoldersFirst = value;
        _explorerRefreshTask = ApplyExplorerViewAsync(revealCurrent: _settings.ExplorerKeepSelectionInView);
    }

    /// <summary>Sort menu toggle for the explorer.keepSelectionInView setting (saved like a Settings change).</summary>
    private void ToggleExplorerKeepSelectionInView()
    {
        _settings.ExplorerKeepSelectionInView = !_settings.ExplorerKeepSelectionInView;
        SaveSettingsAndPublish();
    }

    private void BrowserFilterTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_explorerApplyingState || _browserSurface is not { } surface) return;
        var text = surface.FilterBox.Text ?? "";
        if (string.Equals(text, _explorerFilterText, StringComparison.Ordinal)) return;
        _explorerFilterText = text;
        _explorerRefreshTask = ApplyExplorerViewAsync();
    }

    private void BrowserFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (_browserSurface is not { } surface) return;
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(surface.FilterBox.Text)) surface.FilterBox.Text = "";
            surface.ScrollViewer.Focus();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            surface.ScrollViewer.Focus();
            if (_browserEntries.Count > 0 && _browserSelection.Count == 0) SelectBrowserIndex(0, false, false, true);
            e.Handled = true;
        }
    }

    private void UpdateExplorerDetailsHeader()
    {
        if (_browserSurface is not { } surface) return;
        var header = surface.DetailsHeader;
        header.IsVisible = ExplorerIsDetails;
        if (!ExplorerIsDetails) return;
        var grid = new Grid { Margin = new Thickness(11, 0, 0, 0), Width = _explorerColumnWidths.Sum() + 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        for (var i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(_explorerColumnWidths[i] + (i == 0 ? 4 : 0), GridUnitType.Pixel));
        var names = ExplorerSortFields.All.Select(f => f.Label).ToArray();
        var keys = ExplorerSortFields.All.Select(f => f.Key).ToArray();
        for (var i = 0; i < 4; i++)
        {
            var column = i;
            var sorted = keys[i] == _explorerSortKey;
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            label.Children.Add(new TextBlock { Text = names[i], FontSize = 12 });
            if (sorted)
                label.Children.Add(new GlideIconView { Kind = _explorerSortAscending ? "Up" : "Down", Width = 10, Height = 10, StrokeWidth = 1.6, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var button = new Button { Content = label, Tag = keys[i], Name = "SortHeader" + names[i].Replace(" ", "") };
            button.Classes.Add("exHeader");
            if (i == 3) button.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            button.Click += (_, _) => SetExplorerSort(keys[column]);
            Grid.SetColumn(button, i);
            grid.Children.Add(button);
            // Resize grip on the right edge of every column.
            var grip = new Border
            {
                Width = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeWestEast), Name = "ColumnGrip" + i
            };
            Grid.SetColumn(grip, i);
            double startX = 0, startWidth = 0; var dragging = false;
            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
                dragging = true; startX = e.GetPosition(surface).X; startWidth = _explorerColumnWidths[column];
                e.Pointer.Capture(grip); e.Handled = true;
            };
            grip.PointerMoved += (_, e) =>
            {
                if (!dragging) return;
                ResizeExplorerColumn(column, startWidth + e.GetPosition(surface).X - startX);
                e.Handled = true;
            };
            grip.PointerReleased += (_, e) => { dragging = false; e.Pointer.Capture(null); };
            grid.Children.Add(grip);
        }
        header.Child = grid;
    }

    private void ResizeExplorerColumn(int column, double width)
    {
        width = Math.Clamp(width, 48, 900);
        if (Math.Abs(_explorerColumnWidths[column] - width) < 0.5) return;
        _explorerColumnWidths[column] = width;
        UpdateExplorerDetailsHeader();
        foreach (var tile in RealizedExplorerTiles())
        {
            tile.Width = _explorerColumnWidths.Sum() + 24;
            if (tile.Child is Grid { Children.Count: > 1 } root && root.Children[1] is Grid row && row.ColumnDefinitions.Count == 4)
            {
                for (var i = 0; i < 4; i++) row.ColumnDefinitions[i].Width = new GridLength(_explorerColumnWidths[i]);
                row.Width = _explorerColumnWidths.Sum() + 8;
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Toolbar: show the current folder in Windows File Explorer
    // ------------------------------------------------------------------------------------------

    // Left-click: Windows File Explorer (through the registry action). Right-click: the target menu (MainWindow.AltFileManager.cs).
    private void BrowserWindowsExplorerClicked(object? sender, RoutedEventArgs e) =>
        _ = ExecuteCommandAsync(GlideCommand.ShowFolderInWindowsExplorer);

    private bool HasAlternativeFileManager => !string.IsNullOrWhiteSpace(_settings.AlternativeFileManagerPath);

    /// <summary>The item to preselect when revealing an Explorer tab's folder: the current / a selected item of that folder.</summary>
    private string? ExplorerRevealSelection(BrowserTabState browser)
    {
        if (_explorerDisplayedTabId != browser.Id || !string.Equals(_explorerLoadedFolder, browser.Folder, StringComparison.OrdinalIgnoreCase)) return null;
        var current = ExplorerCurrentEntry?.Path;
        var pick = PickExplorerSessionSelection(_browserSelection, current, browser.Folder);
        return pick is not null && (File.Exists(pick) || Directory.Exists(pick)) ? pick : null;
    }

    /// <summary>Explorer tab: the selected item (or the folder); elsewhere: the current image. Nothing set: Settings at the path setting.</summary>
    private void ShowInAlternativeFileManager()
    {
        if (!HasAlternativeFileManager)
        {
            OpenSettingsAtSetting(AlternativeFileManagerSettingId);
            return;
        }
        string? path = null;
        if (_workspace.Active is BrowserTabState browser) path = ExplorerRevealSelection(browser) ?? browser.Folder;
        else if (!string.IsNullOrWhiteSpace(_currentPath) && File.Exists(_currentPath)) path = _currentPath;
        if (path is null) return;
        var ok = ExplorerShell.OpenInAlternative(_settings.AlternativeFileManagerPath, _settings.AlternativeFileManagerArguments, path);
        _diagnostics.Write("explorer", ok ? "alternative_file_manager_opened" : "alternative_file_manager_failed", new { path });
        if (!ok && _workspace.Active is BrowserTabState) ShowExplorerMessage("The alternative file manager could not be started. Check its path in Settings.", isError: true);
    }

    // ------------------------------------------------------------------------------------------
    // Opening entries and toolbar navigation
    // ------------------------------------------------------------------------------------------

    /// <summary>The Explorer middle-click row opens images in the current tab (the old "Double-click: new tab" choice).</summary>
    private bool ExplorerMiddleClickOpensInCurrentTab =>
        _inputRouter.ResolveGesture("explorer.middleClick", _settings.Gestures) == GestureCatalog.OpenInCurrentTab;

    /// <summary>
    /// Opens an Explorer entry. The default action (double-click / Enter / Open) follows the
    /// "Explorer: open images with" setting; <paramref name="alternate"/> (middle-click) does the
    /// other. Folders navigate in place, or open in a new Explorer tab as the alternate action.
    /// An image opened in the current tab replaces the Explorer tab; one opened in a new tab is placed and activated
    /// as "New tabs open at" / "Open new tabs in the foreground" say (by default next to it in the background, so several can be
    /// queued up), with Shift / Alt held flipping them when "Shift and Alt change how a new tab opens" is on (NewTabPolicy). Menu items read the keys held when they run.
    /// </summary>
    private async Task OpenBrowserEntryAsync(string path, bool alternate, KeyModifiers modifiers = KeyModifiers.None, bool fromMenu = false)
    {
        if (fromMenu && modifiers == KeyModifiers.None) modifiers = ModifierKeyState.Current();
        // Explorer gesture rows (4.8.0): the double-click row (also Enter / "Open") and the
        // middle-click row. Default = current tab for double-click, new tab for middle-click.
        var action = ResolveGestureSlot(alternate ? "explorer.middleClick" : "explorer.doubleClick", modifiers);
        if (action == GestureCatalog.Default) action = alternate ? GestureCatalog.OpenInNewTab : GestureCatalog.OpenInCurrentTab;
        if (action is not (GestureCatalog.OpenInCurrentTab or GestureCatalog.OpenInNewTab))
        {
            if (!fromMenu)
            {
                await ExecuteGestureActionAsync(action);
                return;
            }
            // The context menu's explicit Open items always open.
            action = alternate ? GestureCatalog.OpenInNewTab : GestureCatalog.OpenInCurrentTab;
        }
        if (Directory.Exists(path))
        {
            if (!alternate) { NavigateBrowserTo(path, addHistory: true); return; }
            await OpenFolderInNewTabAsync(path, modifiers, fromMenu ? "explorer_menu" : "explorer_middle_click");
            return;
        }
        if (!File.Exists(path)) return;
        if (!ImageNavigator.IsSupported(path)) { ExplorerShell.Open(path); return; }
        var newTab = action == GestureCatalog.OpenInNewTab;
        _diagnostics.Write("browser", "open_image", new { path, newTab, alternate });
        if (newTab)
        {
            await OpenImageInNewTabAsync(path, modifiers, fromMenu ? "explorer_menu" : alternate ? "explorer_middle_click" : "explorer_double_click");
            return;
        }
        if (_workspace.Active is BrowserTabState browser)
        {
            // The Explorer tab becomes this image tab. Its history and its view of the folder (sort, icon size, filter)
            // are kept: Back returns to this folder with the image selected (MainWindow.History.cs).
            _browserHighlightTargets.Remove(browser.Id);
        }
        await OpenAsImageTabAsync(path);
    }

    // The Explorer toolbar Back / Forward buttons (and explorer.back / explorer.forward) step through the same per-tab
    // history as the title-bar buttons, Alt+Left / Alt+Right and the mouse side buttons, including an image opened here.
    private async void BrowserBackClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is not BrowserTabState) return;
        await NavigateBackAsync();
    }

    private async void BrowserForwardClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is not BrowserTabState) return;
        await NavigateForwardAsync();
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

    /// <summary>Jumps to an entry of the tab's history (history dropdown): a folder, an image opened here, or Home.</summary>
    private void NavigateExplorerHistoryIndex(int index) => _ = NavigateHistoryToIndexAsync(index);

    private void BrowserAddressNavigateRequested(string text)
    {
        if (_browserSurface is not { } surface) return;
        var raw = text.Trim().Trim('"');
        string expanded;
        try { expanded = Environment.ExpandEnvironmentVariables(raw); } catch { expanded = raw; }
        try
        {
            if (Directory.Exists(expanded))
            {
                NavigateBrowserTo(Path.GetFullPath(expanded), addHistory: true);
                surface.AddressBar.EndEdit();
                FocusBrowserList();
                return;
            }
            if (File.Exists(expanded))
            {
                var full = Path.GetFullPath(expanded);
                var parent = Path.GetDirectoryName(full);
                if (parent is not null && Directory.Exists(parent))
                {
                    NavigateBrowserTo(parent, addHistory: true, highlightPath: full);
                    surface.AddressBar.EndEdit();
                    FocusBrowserList();
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { }
        surface.AddressBar.ShowError($"Can't find '{raw}'. Check the spelling and try again.");
    }
}
