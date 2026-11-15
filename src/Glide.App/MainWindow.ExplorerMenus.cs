using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Glide.App.Explorer;
using Glide.App.Services;
using Glide.Core.Commands;
using Glide.Core.Workspace;

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

    private void ShowExplorerContextMenu(ExplorerEntry? entry, Control target)
    {
        var menu = BuildExplorerContextMenu(entry);
        menu.Open(target);
    }

    private void ShowExplorerContextMenuAtCurrent()
    {
        if (_browserSurface is not { } surface) return;
        var entry = _browserCurrentIndex >= 0 && _browserCurrentIndex < _browserEntries.Count && _browserSelection.Count > 0
            ? _browserEntries[_browserCurrentIndex] : null;
        Control target = entry is not null && ExplorerTileFor(_browserCurrentIndex) is { } tile ? tile : surface.ScrollViewer;
        ShowExplorerContextMenu(entry, target);
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
            for (var i = 0; i < ExplorerViewModes.Names.Length; i++)
            {
                var mode = i;
                viewItems.Add(ExplorerMenuItem(ExplorerViewModes.Names[i], () => SetExplorerViewMode(mode), Ctx + "explorer.viewModeOfThisTab", check: mode == _browserViewMode));
            }
            view.ItemsSource = viewItems;
            items.Add(view);

            var sort = new MenuItem { Header = "Sort by", Icon = MenuIcon("Navigate") };
            var sortItems = new List<MenuItem>
            {
                ExplorerMenuItem("Name", () => SetExplorerSort(ExplorerSortKey.Name, _explorerSortAscending), Ctx + "explorer.sortOfThisTab", check: _explorerSortKey == ExplorerSortKey.Name),
                ExplorerMenuItem("Date modified", () => SetExplorerSort(ExplorerSortKey.Modified, _explorerSortAscending), Ctx + "explorer.sortOfThisTab", check: _explorerSortKey == ExplorerSortKey.Modified),
                ExplorerMenuItem("Type", () => SetExplorerSort(ExplorerSortKey.Type, _explorerSortAscending), Ctx + "explorer.sortOfThisTab", check: _explorerSortKey == ExplorerSortKey.Type),
                ExplorerMenuItem("Size", () => SetExplorerSort(ExplorerSortKey.Size, _explorerSortAscending), Ctx + "explorer.sortOfThisTab", check: _explorerSortKey == ExplorerSortKey.Size),
                ExplorerSeparator(),
                ExplorerMenuItem("Ascending", () => SetExplorerSort(_explorerSortKey, true), Ctx + "explorer.sortOfThisTab", check: _explorerSortAscending),
                ExplorerMenuItem("Descending", () => SetExplorerSort(_explorerSortKey, false), Ctx + "explorer.sortOfThisTab", check: !_explorerSortAscending),
                ExplorerSeparator(),
                ExplorerMenuItem("Group folders first", () => SetExplorerFoldersFirst(!_explorerFoldersFirst), Ctx + "explorer.sortOfThisTab", check: _explorerFoldersFirst)
            };
            sort.ItemsSource = sortItems;
            items.Add(sort);
            items.Add(ExplorerMenuItem("Refresh", () => BrowserRefreshClicked(null, new RoutedEventArgs()), "fixed.explorer.refresh", "Refresh"));
            items.Add(ExplorerSeparator());
            items.Add(ExplorerMenuItem("Paste", () => _ = ExplorerPasteAsync(folder!), "fixed.explorer.paste", enabled: folder is not null && clipboardHasFiles));
            items.Add(ExplorerSeparator());
            items.Add(ExplorerMenuItem("New folder", () => _ = ExplorerNewFolderAsync(), "fixed.explorer.newFolder", "Plus", enabled: folder is not null));
            items.Add(ExplorerMenuItem("Select all", () => SelectAllBrowserEntries(), "fixed.explorer.selectAll", "Check", enabled: _browserEntries.Count > 0));
            items.Add(ExplorerSeparator());
            items.Add(ExplorerMenuItem("Show in Windows File Explorer", () => { if (folder is not null) ExplorerShell.Reveal(folder, directory: true); }, "explorer.showInWindows", "FolderLocate", enabled: folder is not null));
            items.Add(ExplorerMenuItem("Properties", () => ShowExplorerProperties(), "fixed.explorer.properties", "Info", enabled: folder is not null));
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
            // Multiple selection: every action applies to all selected items.
            var paths = selected.Select(s => s.Path).ToList();
            var openable = selected.Where(s => s.IsDirectory || s.IsImage).ToList();
            if (openable.Count > 0)
            {
                items.Add(ExplorerMenuItem(openable.Count == selected.Count ? $"Open all {selected.Count} in new tabs" : $"Open {openable.Count} in new tabs",
                    () => _ = OpenExplorerEntriesInNewTabsAsync(openable), Ctx + "explorer.openSelectionInTabs", "Open"));
                items.Add(ExplorerSeparator());
            }
            items.Add(ExplorerMenuItem("Cut", () => ExplorerCopyOrCut(cut: true), "fixed.explorer.cut"));
            items.Add(ExplorerMenuItem("Copy", () => ExplorerCopyOrCut(cut: false), "fixed.explorer.copy", "Copy"));
            items.Add(ExplorerMenuItem("Copy path", () => _ = ExplorerCopyPathsAsync(paths), Ctx + "explorer.copyPathsOfSelection", "Copy"));
            items.Add(ExplorerSeparator());
            items.Add(ExplorerMenuItem($"Delete {selected.Count} items", () => _ = ExplorerDeleteSelectionAsync(permanent: false), "fixed.explorer.delete", "Delete"));
            items.Add(ExplorerSeparator());
            items.Add(ExplorerMenuItem("Properties", () => ShowExplorerProperties(selected[0]), "fixed.explorer.properties", "Info"));
        }

        var menu = new ContextMenu { ItemsSource = items };
        return PrepareOwnedContextMenu(menu);
    }

    private void AddCutCopyPaste(List<MenuItem> items, ExplorerEntry one, string? folder, bool clipboardHasFiles)
    {
        items.Add(ExplorerMenuItem("Cut", () => ExplorerCopyOrCut(cut: true), "fixed.explorer.cut"));
        items.Add(ExplorerMenuItem("Copy", () => ExplorerCopyOrCut(cut: false), "fixed.explorer.copy", "Copy"));
        items.Add(ExplorerMenuItem("Copy path", () => _ = ExplorerCopyPathsAsync(new[] { one.Path }), MenuActions.ContextPrefix + "explorer.copyPathOfThisItem", "Copy"));
        // Paste targets the folder under the cursor; files paste into the folder being viewed.
        var pasteInto = one.IsDirectory ? one.Path : folder;
        items.Add(ExplorerMenuItem(one.IsDirectory ? "Paste into folder" : "Paste", () => _ = ExplorerPasteAsync(pasteInto!), "fixed.explorer.paste",
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
        if (_workspace.Active is BrowserTabState && ActiveHistory is { } history)
        {
            // Newest first, like Explorer's "Recent locations". The tab history also holds an image opened from this tab
            // and Home; choosing one goes there exactly like Back / Forward would.
            var shown = 0;
            for (var i = history.Count - 1; i >= 0 && shown < 15; i--, shown++)
            {
                var index = i;
                var entry = history.Entries[i];
                var (header, icon, tip) = HistoryEntryLabel(entry);
                var item = ExplorerMenuItem(header, () => NavigateExplorerHistoryIndex(index), MenuActions.ContextPrefix + "explorer.historyEntry", icon, check: i == history.Index);
                ToolTip.SetTip(item, tip);
                item.Tag = entry.Path;
                items.Add(item);
            }
        }
        if (items.Count == 0) items.Add(MenuActions.Context(new MenuItem { Header = "No recent locations", IsEnabled = false }, "explorer.historyPlaceholder"));
        return PrepareOwnedContextMenu(new ContextMenu { ItemsSource = items });
    }

    private void ShowExplorerHistoryMenu()
    {
        if (_browserSurface is not { } surface) return;
        var menu = BuildExplorerHistoryMenu();
        menu.Open(surface.HistoryButton);
    }
}
