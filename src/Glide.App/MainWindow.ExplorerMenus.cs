using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Glide.App.Explorer;
using Glide.App.Services;
using Glide.Core.Commands;
using Glide.Core.Workspace;
using Glide.Core.Navigation;

namespace Glide.App;

/// <summary>Managed Explorer tab, part 3: right-click menus (item, folder, multi-selection, background) and the history dropdown.</summary>
public partial class MainWindow
{
    /// <summary>
    /// One Explorer menu item. <paramref name="tag"/> is the master-registry action it performs (Explorer's fixed
    /// list keys are registry entries of kind FixedShortcut, so the shortcut text shown here is read from the
    /// registry instead of being typed a second time), or a MenuActions context tag for item-specific commands.
    /// </summary>
    private MenuItem ExplorerMenuItem(string header, Action action, string tag, string? icon = null, bool enabled = true, bool check = false)
    {
        var item = new MenuItem { Header = check ? header + "  ✓" : header, IsEnabled = enabled };
        if (icon is not null) item.Icon = MenuIcon(icon);
        var shortcut = ExplorerMenuShortcut(tag);
        if (shortcut is not null)
        {
            try { item.InputGesture = KeyGesture.Parse(shortcut); } catch { /* an unparsable shortcut text just shows no gesture */ }
        }
        item.Click += (_, _) => action();
        return tag.StartsWith(MenuActions.ContextPrefix, StringComparison.Ordinal)
            ? MenuActions.Context(item, tag[MenuActions.ContextPrefix.Length..])
            : MenuActions.Tag(item, tag);
    }

    /// <summary>The first key of a fixed Explorer shortcut ("F5 / Ctrl+R" -> "F5"), for the menu's shortcut column.</summary>
    private static string? ExplorerMenuShortcut(string tag)
    {
        if (AppActionRegistry.Find(tag) is not { Kind: AppActionKind.FixedShortcut, FixedShortcut: { } keys }) return null;
        var first = keys.Split(" / ")[0].Trim();
        return first is "Letters" || first.Contains(' ') ? null : first;
    }

    private static MenuItem ExplorerSeparator() => new() { Header = "-" };

    /// <summary>
    /// Every Explorer menu goes through here. A popup inherits inherited properties (TextAlignment among them) from the
    /// control it opens on; pinning the alignment keeps every header left-aligned whatever that control is.
    /// </summary>
    private ContextMenu NewExplorerMenu(IReadOnlyList<MenuItem> items)
    {
        var menu = new ContextMenu { ItemsSource = items };
        menu.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Left);
        return PrepareOwnedContextMenu(menu);
    }

    /// <summary>The item the open context menu acts on: it keeps the focus border while the menu has the keyboard focus.</summary>
    private ExplorerEntry? _explorerMenuEntry;
    private ContextMenu? _explorerEntryMenu;

    private void ShowExplorerContextMenu(ExplorerEntry? entry, Control target)
    {
        var menu = BuildExplorerContextMenu(entry);
        _explorerEntryMenu = menu;
        SetExplorerMenuEntry(entry);
        // Only this menu's own closing clears the border (a previous menu may close after the next one has opened).
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_explorerEntryMenu, menu)) return;
            _explorerEntryMenu = null;
            SetExplorerMenuEntry(null);
        };
        menu.Open(target);
    }

    private void SetExplorerMenuEntry(ExplorerEntry? entry)
    {
        _explorerMenuEntry = entry;
        foreach (var tile in RealizedExplorerTiles()) UpdateExplorerTileVisual(tile);
    }

    /// <summary>A file or folder name for a menu header, shortened in the middle when long.</summary>
    // A MenuItem header string is AccessText: "_" in a file name must be doubled or it becomes a mnemonic underline.
    private static string ExplorerMenuName(string name, int max = 40) =>
        (name.Length <= max ? name : string.Concat(name.AsSpan(0, max / 2 - 1), "…", name.AsSpan(name.Length - (max - max / 2)))).Replace("_", "__");

    private void ShowExplorerContextMenuAtCurrent()
    {
        if (_browserSurface is null) return;
        var entry = _browserSelection.Count > 0 ? ExplorerCurrentEntry : null;
        ShowExplorerContextMenu(entry, ExplorerMenuAnchor(entry));
    }

    /// <summary>
    /// Builds the Explorer context menu for an item (single file/folder or the multi-selection) or,
    /// with no entry, for the empty background. Built per right-click so it always reflects the
    /// current selection and clipboard.
    /// </summary>
    internal ContextMenu BuildExplorerContextMenu(ExplorerEntry? entry)
    {
        const string Ctx = MenuActions.ContextPrefix;
        var items = new List<MenuItem>();
        var folder = ExplorerCurrentFolder;
        var selected = SelectedExplorerEntries();
        if (entry is not null && !selected.Any(s => ReferenceEquals(s, entry))) selected = new List<ExplorerEntry> { entry };
        var clipboardHasFiles = ExplorerFileClipboard.Current.TryGet() is { Paths.Count: > 0 };

        if (entry is null || selected.Count == 0)
        {
            // Empty background: View, Sort by, Refresh, Paste, New folder, Select all, Properties.
            var view = new MenuItem { Header = "View", Icon = MenuIcon("Eye") };
            var viewItems = new List<MenuItem>();
            for (var i = 0; i < ExplorerViewModes.Labels.Length; i++)
            {
                var mode = i;
                viewItems.Add(ExplorerMenuItem(ExplorerViewModes.Labels[i], () => SetExplorerViewMode(mode), Ctx + "explorer.viewModeOfThisTab", check: mode == _browserViewMode));
            }
            view.ItemsSource = viewItems;
            items.Add(view);

            var sort = new MenuItem { Header = "Sort by", Icon = MenuIcon("Sort"), ItemsSource = BuildExplorerSortMenuItems() };
            items.Add(sort);
            items.Add(ExplorerMenuItem("Refresh", () => BrowserRefreshClicked(null, new RoutedEventArgs()), "fixed.explorer.refresh", "Refresh"));
            items.Add(ExplorerSeparator());
            items.Add(ExplorerMenuItem("Paste", () => _ = ExplorerPasteAsync(folder!), "fixed.explorer.paste", "Paste", enabled: folder is not null && clipboardHasFiles));
            items.Add(ExplorerSeparator());
            items.Add(ExplorerMenuItem("New folder", () => _ = ExplorerNewFolderAsync(), "fixed.explorer.newFolder", "Plus", enabled: folder is not null));
            items.Add(ExplorerMenuItem("Select all", () => SelectAllBrowserEntries(), "fixed.explorer.selectAll", "Check", enabled: _browserEntries.Count > 0));
            items.Add(ExplorerSeparator());
            items.Add(ExplorerMenuItem("Show in Windows File Explorer", () => { if (folder is not null) ExplorerShell.Reveal(folder, directory: true); }, "explorer.showInWindows", "FolderLocate", enabled: folder is not null));
            // Round 3 #16: nothing selected, so Properties is the folder's: say which ("Properties of Pictures"; a drive root
            // keeps its path, "Properties of D:\").
            var folderName = folder is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            var propertiesHeader = folder is null ? "Properties" : $"Properties of {ExplorerMenuName(string.IsNullOrEmpty(folderName) ? folder : folderName)}";
            items.Add(ExplorerMenuItem(propertiesHeader, () => ShowExplorerProperties(), "fixed.explorer.properties", "Info", enabled: folder is not null));
        }
        else if (selected.Count == 1)
        {
            var one = selected[0];
            var directory = one.IsDirectory;
            var path = one.Path;
            items.Add(ExplorerMenuItem(directory ? "Open folder" : "Open", () => _ = OpenBrowserEntryAsync(path, alternate: false, fromMenu: true), "fixed.explorer.open", "Open"));
            if (directory || one.IsImage)
            {
                // The other action (middle-click) is offered explicitly too.
                var alternate = directory ? "Open in new Explorer tab"
                    : ExplorerMiddleClickOpensInCurrentTab ? "Open in this tab" : "Open in new tab";
                items.Add(ExplorerMenuItem(alternate, () => _ = OpenBrowserEntryAsync(path, alternate: true, fromMenu: true),
                    alternate == "Open in this tab" ? "gesture.openInCurrentTab" : "gesture.openInNewTab", directory ? "Browser" : "Image"));
            }
            if (!directory) items.Add(ExplorerMenuItem("Open with…", () => ExplorerOpenWith(path), Ctx + "explorer.openThisItemWith", "OpenWith"));
            if (OperatingSystem.IsWindows())
                items.Add(ExplorerMenuItem("Show in Windows File Explorer", () => ExplorerShell.Reveal(path, directory), Ctx + "explorer.revealThisItem", "FolderLocate"));
            items.Add(ExplorerSeparator());
            AddCutCopyPaste(items, one, folder, clipboardHasFiles);
            items.Add(ExplorerSeparator());
            items.Add(ExplorerMenuItem("Rename", () => BeginExplorerRename(ExplorerIndexOf(one)), "fixed.explorer.rename", "Rename"));
            items.Add(ExplorerMenuItem("Delete", () => _ = ExplorerDeleteSelectionAsync(permanent: false), "fixed.explorer.delete", "Delete"));
            if (!directory && File.Exists(path) && one.IsImage)
                items.Add(ExplorerMenuItem("Print…", () => _ = PrintFileAsync(path), Ctx + "explorer.printThisItem", "Print"));
            items.Add(ExplorerSeparator());
            items.Add(ExplorerMenuItem("Properties", () => ShowExplorerProperties(one), "fixed.explorer.properties", "Info"));
        }
        else
        {
            // Multiple selection: every action applies to all selected items, except Properties, which names the item that was
            // right-clicked (or, from the keyboard, the focused item): the one with the focus border.
            var paths = selected.Select(s => s.Path).ToList();
            var openable = selected.Where(s => s.IsDirectory || s.IsImage).ToList();
            if (openable.Count > 0)
            {
                items.Add(ExplorerMenuItem(openable.Count == selected.Count ? $"Open all {selected.Count} in new tabs" : $"Open {openable.Count} in new tabs",
                    () => _ = OpenExplorerEntriesInNewTabsAsync(openable), Ctx + "explorer.openSelectionInTabs", "Open"));
                items.Add(ExplorerSeparator());
            }
            items.Add(ExplorerMenuItem("Cut", () => ExplorerCopyOrCut(cut: true), "fixed.explorer.cut", "Cut"));
            items.Add(ExplorerMenuItem("Copy", () => ExplorerCopyOrCut(cut: false), "fixed.explorer.copy", "Copy"));
            items.Add(ExplorerMenuItem($"Copy {paths.Count} paths", () => _ = ExplorerCopyPathsAsync(paths), Ctx + "explorer.copyPathsOfSelection", "Copy"));
            items.Add(ExplorerSeparator());
            items.Add(ExplorerMenuItem($"Delete {selected.Count} items", () => _ = ExplorerDeleteSelectionAsync(permanent: false), "fixed.explorer.delete", "Delete"));
            items.Add(ExplorerSeparator());
            var target = entry!;
            items.Add(ExplorerMenuItem($"Properties of {ExplorerMenuName(target.Name)}", () => ShowExplorerProperties(target), "fixed.explorer.properties", "Info"));
        }

        return NewExplorerMenu(items);
    }

    /// <summary>
    /// The Sort by list, shared by the background menu's "Sort by" submenu and the toolbar's Sort button: the sort fields
    /// (keeping the direction), the direction, and Group folders first. Ticks show the current tab's sort.
    /// </summary>
    private List<MenuItem> BuildExplorerSortMenuItems()
    {
        const string Tag = MenuActions.ContextPrefix + "explorer.sortOfThisTab";
        // Each field twice ("Name ▲" / "Name ▼"): one pick sets the field and the direction.
        IEnumerable<MenuItem> Both((ExplorerSortKey Key, string Label) field) => new[] { true, false }.Select(ascending =>
            ExplorerMenuItem($"{field.Label} {ExplorerSortFields.Triangle(ascending)}", () => SetExplorerSort(field.Key, ascending), Tag,
                check: _explorerSortKey == field.Key && _explorerSortAscending == ascending));
        var items = ExplorerSortFields.All.SelectMany(Both).ToList();
        var dimensions = MenuActions.Context(new MenuItem
        {
            Header = ExplorerSortFields.IsDimension(_explorerSortKey) ? "Dimensions  ✓" : "Dimensions",
            ItemsSource = ExplorerSortFields.Dimensions.SelectMany(Both).ToList()
        }, "explorer.sortOfThisTab");
        items.Add(dimensions);
        items.Add(ExplorerSeparator());
        items.Add(ExplorerMenuItem("Group folders first", () => SetExplorerFoldersFirst(!_explorerFoldersFirst), Tag, check: _explorerFoldersFirst));
        items.Add(ExplorerMenuItem("Keep selection in view", ToggleExplorerKeepSelectionInView, Tag, check: ExplorerOptionsView.KeepSelectionInView));
        return items;
    }

    private void ShowExplorerSortMenu()
    {
        if (_browserSurface is not { } surface) return;
        NewExplorerMenu(BuildExplorerSortMenuItems()).Open(surface.SortButton);
    }

    /// <summary>Shows the current tab's sort on the toolbar's Sort button.</summary>
    private void UpdateExplorerSortButton() =>
        _browserSurface?.ShowSort(ExplorerSortFields.Label(_explorerSortKey), _explorerSortAscending,
            ExplorerSortFields.Describe(_explorerSortKey, _explorerSortAscending, _explorerFoldersFirst));

    private void AddCutCopyPaste(List<MenuItem> items, ExplorerEntry one, string? folder, bool clipboardHasFiles)
    {
        items.Add(ExplorerMenuItem("Cut", () => ExplorerCopyOrCut(cut: true), "fixed.explorer.cut", "Cut"));
        items.Add(ExplorerMenuItem("Copy", () => ExplorerCopyOrCut(cut: false), "fixed.explorer.copy", "Copy"));
        items.Add(ExplorerMenuItem("Copy path", () => _ = ExplorerCopyPathsAsync(new[] { one.Path }), MenuActions.ContextPrefix + "explorer.copyPathOfThisItem", "Copy"));
        // Paste targets the folder under the cursor; files paste into the folder being viewed.
        var pasteInto = one.IsDirectory ? one.Path : folder;
        items.Add(ExplorerMenuItem(one.IsDirectory ? "Paste into folder" : "Paste", () => _ = ExplorerPasteAsync(pasteInto!), "fixed.explorer.paste", "Paste",
            enabled: pasteInto is not null && clipboardHasFiles));
    }

    private async Task OpenExplorerEntriesInNewTabsAsync(IReadOnlyList<ExplorerEntry> entries)
    {
        foreach (var entry in entries)
            await OpenBrowserEntryAsync(entry.Path, alternate: true, fromMenu: true);
    }

    // ------------------------------------------------------------------------------------------
    // History dropdown (Back/Forward chevron)
    // ------------------------------------------------------------------------------------------

    internal ContextMenu BuildExplorerHistoryMenu()
    {
        var items = new List<MenuItem>();
        if (_workspace.Active is BrowserTabState or ImageTabState && ActiveHistory is { } history)
        {
            // Round 3 #13: full paths, the most recently visited place first (each place once), not Back / Forward
            // order. The tab history also holds an image opened from this tab and Home; choosing one goes there exactly
            // like Back / Forward would.
            foreach (var i in history.RecentOrder(15))
            {
                var index = i;
                var entry = history.Entries[i];
                var (_, icon, tip) = HistoryEntryLabel(entry);
                var header = entry.Kind == NavigationEntryKind.Home || string.IsNullOrEmpty(entry.Path) ? "Home" : entry.Path;
                var item = ExplorerMenuItem(header, () => NavigateExplorerHistoryIndex(index), MenuActions.ContextPrefix + "explorer.historyEntry", icon, check: i == history.Index);
                ToolTip.SetTip(item, tip);
                item.Tag = entry.Path;
                items.Add(item);
            }
        }
        if (items.Count == 0) items.Add(MenuActions.Context(new MenuItem { Header = "No recent locations", IsEnabled = false }, "explorer.historyPlaceholder"));
        return NewExplorerMenu(items);
    }

    private void ShowExplorerHistoryMenu()
    {
        if (_browserSurface is not { } surface) return;
        var menu = BuildExplorerHistoryMenu();
        menu.Open(surface.HistoryButton);
    }
}
