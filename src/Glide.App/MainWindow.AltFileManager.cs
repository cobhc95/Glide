using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Commands;

namespace Glide.App;

/// <summary>
/// The alternative file manager (settings explorer.altFileManagerPath / explorer.altFileManagerArguments) as a
/// "Show in" target. One helper builds the target entries for both the title-bar locate button's context menu and the
/// Explorer toolbar's Show in File Explorer button, so neither list is written twice. Without a program set, the
/// entry (and the explorer.showInAlternative action) opens Settings at the path setting instead.
/// </summary>
public partial class MainWindow
{
    internal const string AlternativeFileManagerSettingId = "explorer.altFileManagerPath";

    /// <summary>The entry's text while no program is set (the menu entry and the Explorer toolbar button's tooltip).</summary>
    private const string AlternativeFileManagerSetUpLabel = "Show in other file manager…";

    /// <summary>The configured program's display name: its FileDescription, else its file name without extension; null when none is set.</summary>
    internal static string? AlternativeFileManagerDisplayName(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        exe = exe.Trim().Trim('"');
        try
        {
            if (File.Exists(exe) && FileVersionInfo.GetVersionInfo(exe).FileDescription is { } description && !string.IsNullOrWhiteSpace(description))
                return description.Trim();
        }
        catch { /* unreadable version info: fall back to the file name */ }
        var name = Path.GetFileNameWithoutExtension(exe);
        return string.IsNullOrWhiteSpace(name) ? exe : name;
    }

    /// <summary>
    /// The "Show in" target entries. <paramref name="tickTarget"/>: title-bar mode, where each entry chooses the button's
    /// left-click target and the chosen one carries the ✓; otherwise each entry simply runs.
    /// </summary>
    private List<MenuItem> BuildFileManagerTargetMenuItems(bool includeGlideTab, bool tickTarget)
    {
        var items = new List<MenuItem>();
        if (includeGlideTab) items.Add(BuildShowInExplorerTargetMenuItem("Show in Glide's Explorer tab", ShowInExplorerTargets.GlideTab, "Browser"));
        if (tickTarget) items.Add(BuildShowInExplorerTargetMenuItem("Show in Windows File Explorer", ShowInExplorerTargets.WindowsExplorer, "FolderLocate"));
        else
        {
            var windows = MenuActions.Tag(new MenuItem { Header = "Show in Windows File Explorer", Icon = MenuIcon("FolderLocate") }, "explorer.showInWindows");
            windows.Click += (_, _) => _ = ExecuteCommandAsync(GlideCommand.ShowFolderInWindowsExplorer);
            items.Add(windows);
        }
        items.Add(BuildAlternativeFileManagerMenuItem(tickTarget));
        return items;
    }

    private MenuItem BuildAlternativeFileManagerMenuItem(bool tickTarget)
    {
        var name = AlternativeFileManagerDisplayName(_settings.AlternativeFileManagerPath);
        MenuItem item;
        if (name is null)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(new TextBlock { Text = AlternativeFileManagerSetUpLabel });
            header.Children.Add(new TextBlock { Text = "Set up", Opacity = 0.55, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            item = MenuActions.Tag(new MenuItem { Header = header }, "explorer.showInAlternative");
            item.Click += (_, _) => OpenSettingsAtSetting(AlternativeFileManagerSettingId);
        }
        else
        {
            var text = "Show in " + name;
            var selected = tickTarget && ShowInExplorerTargets.IsAlternative(_settings.ShowInExplorerTarget);
            item = MenuActions.Tag(new MenuItem { Header = selected ? $"{text}  ✓" : text }, "explorer.showInAlternative");
            item.Click += (_, _) =>
            {
                // Title bar: the entry also becomes the button's left-click target, like its siblings.
                if (tickTarget && !selected) SetShowInExplorerTarget(ShowInExplorerTargets.Alternative);
                ShowInAlternativeFileManager();
            };
        }
        item.Icon = MenuIcon("FolderLocate");
        return item;
    }

    /// <summary>
    /// The Explorer toolbar's Show in File Explorer button tooltip: the left-click action and, on a second line, what its
    /// right-click menu adds (the alternative file manager, or setting one up), each with its live keys.
    /// </summary>
    private string ExplorerRevealButtonTip()
    {
        var windows = WithActionShortcuts("Show in Windows File Explorer, with the selected item selected", "explorer.showInWindows");
        var alternative = AlternativeFileManagerDisplayName(_settings.AlternativeFileManagerPath) is { } name
            ? WithActionShortcuts("Show in " + name, "explorer.showInAlternative")
            : AlternativeFileManagerSetUpLabel + " (set up)";
        return $"{windows}\nRight-click for more: {alternative}";
    }

    /// <summary>The Explorer toolbar's Show in File Explorer button: right-click lists the targets (built on each opening).</summary>
    private void AttachExplorerToolbarRevealMenu(Button button)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) => menu.ItemsSource = BuildFileManagerTargetMenuItems(includeGlideTab: false, tickTarget: false);
        menu.ItemsSource = BuildFileManagerTargetMenuItems(includeGlideTab: false, tickTarget: false);
        button.ContextMenu = PrepareOwnedContextMenu(menu);
    }

    /// <summary>OpenContainingFolder / ExploreParentFolder: the alternative target, or the usual locate behaviour.</summary>
    private Task OpenContainingFolderOrAlternativeAsync()
    {
        if (!ShowInExplorerTargets.IsAlternative(_settings.ShowInExplorerTarget)) return OpenContainingFolderAsync();
        ShowInAlternativeFileManager();
        return Task.CompletedTask;
    }
}
