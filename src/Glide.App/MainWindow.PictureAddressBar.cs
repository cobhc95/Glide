using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Glide.App.Controls;
using Glide.App.Services;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>
/// 5.1.0 "Show address bar while viewing pictures" (explorer.addressBarOverPictures, off by default): the Explorer tab's
/// navigation row (Back, Forward, Recent locations, Up, Refresh and the breadcrumb address bar) also shows above a
/// picture, with the picture's folder in the breadcrumb. It takes its own layout row (the picture is never covered) and is
/// hidden in fullscreen and Overlay mode. Back / Forward / Recent use the tab history; Up, Refresh and a breadcrumb
/// segment open that folder in the Explorer view of this tab (Up and Refresh select the picture). The Explorer-only
/// parts (filter, sort, view) are not part of it. Right-clicking the row (here or in the Explorer tab) toggles it.
/// </summary>
public partial class MainWindow
{
    private Border? _pictureAddressRow;
    private ExplorerAddressBar? _pictureAddressBar;
    private Button? _pictureBackButton, _pictureForwardButton;
    private bool _pictureAddressWatchingWindowState;

    /// <summary>True when the row should show now (setting on, picture tab, not fullscreen or Overlay mode).</summary>
    private bool PictureAddressRowWanted =>
        ExplorerOptionsView.AddressBarOverPictures && _workspace.Active is ImageTabState && !_wholeAppOverlayMode && WindowState != WindowState.FullScreen;

    private void UpdatePictureAddressRow()
    {
        if (!_pictureAddressWatchingWindowState)
        {
            _pictureAddressWatchingWindowState = true;
            PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) UpdatePictureAddressRow(); };
        }
        if (!PictureAddressRowWanted)
        {
            if (_pictureAddressRow is not null) _pictureAddressRow.IsVisible = false;
            return;
        }
        EnsurePictureAddressRow();
        _pictureAddressRow!.IsVisible = true;
        var folder = PictureAddressFolder();
        if (folder is not null && !_pictureAddressBar!.IsEditing) _pictureAddressBar.Path = folder;
        var canBack = CrossTabHistory.CanGo(ActiveHistory, -1, ExplorerOptionsView.BackReturnsToImage);
        var canForward = CrossTabHistory.CanGo(ActiveHistory, 1, ExplorerOptionsView.BackReturnsToImage);
        _pictureBackButton!.IsEnabled = canBack;
        _pictureForwardButton!.IsEnabled = canForward;
    }

    /// <summary>The folder of the picture on screen (or of the tab's picture before it is shown).</summary>
    private string? PictureAddressFolder()
    {
        var path = _workspace.Active is ImageTabState image ? (NavigationEntry.PathsEqual(_currentPath, image.Path) || _currentPath is null ? image.Path : _currentPath) : _currentPath;
        try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private void EnsurePictureAddressRow()
    {
        if (_pictureAddressRow is not null) return;
        // Reserve a layout row above the picture (like the title bar): every viewer layer moves to the second row.
        ImageView.RowDefinitions = new RowDefinitions("Auto,*");
        foreach (var child in ImageView.Children) Grid.SetRow(child, 1);

        Button Tool(string icon, string tip, Action click)
        {
            var button = new Button { Content = new GlideIconView { Kind = icon, Classes = { "toolbarGlyph" } }, Margin = new Thickness(0, 0, 2, 0) };
            button.Classes.Add("browserTool");
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => click();
            return button;
        }
        _pictureBackButton = Tool("Previous", "Back", () => _ = NavigateBackAsync());
        _pictureForwardButton = Tool("Next", "Forward", () => _ = NavigateForwardAsync());
        var recent = Tool("History", "Recent locations", () => { });
        recent.Click += (_, _) => BuildExplorerHistoryMenu().Open(recent);
        var up = Tool("Up", "Up: open this folder in the Explorer view", () => _ = OpenPictureFolderInThisTabAsync(selectPicture: true));
        var refresh = Tool("Refresh", "Show this folder in the Explorer view", () => _ = OpenPictureFolderInThisTabAsync(selectPicture: true));
        _pictureAddressBar = new ExplorerAddressBar { Margin = new Thickness(6, 0, 0, 0) };
        _pictureAddressBar.NavigateRequested += (_, target) => _ = OpenFolderFromPictureAddressAsync(target);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,*"), VerticalAlignment = VerticalAlignment.Center };
        Control[] parts = [_pictureBackButton, _pictureForwardButton, recent, up, refresh, _pictureAddressBar];
        for (var i = 0; i < parts.Length; i++) { Grid.SetColumn(parts[i], i); grid.Children.Add(parts[i]); }
        _pictureAddressRow = new Border
        {
            Name = "PictureAddressRow",
            Child = grid,
            Padding = new Thickness(8, 5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            ContextMenu = BuildAddressRowContextMenu(),
        };
        _pictureAddressRow.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushSurface"));
        _pictureAddressRow.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorderSoft"));
        Grid.SetRow(_pictureAddressRow, 0);
        ImageView.Children.Add(_pictureAddressRow);
    }

    /// <summary>Up / Refresh: this tab shows the picture's folder in the Explorer view, the picture selected (Back returns).</summary>
    private Task OpenPictureFolderInThisTabAsync(bool selectPicture)
    {
        var path = _workspace.Active is ImageTabState ? _currentPath : null;
        return selectPicture && path is not null ? ShowPathInSameTabAsync(path) : ShowPathInSameTabAsync(PictureAddressFolder());
    }

    /// <summary>A breadcrumb segment or a typed path: that folder in the Explorer view of this tab.</summary>
    private Task OpenFolderFromPictureAddressAsync(string target)
    {
        target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        if (!Directory.Exists(target))
        {
            _pictureAddressBar?.ShowError($"Can't find '{target}'.");
            return Task.CompletedTask;
        }
        return ShowPathInSameTabAsync(Path.GetFullPath(target));
    }

    /// <summary>The right-click menu of the navigation row (Explorer toolbar and the row over pictures).</summary>
    internal ContextMenu BuildAddressRowContextMenu()
    {
        var menu = new ContextMenu();
        // The setting's own registry action (setting.explorer.addressBarOverPictures): bindable to a hotkey like every toggle.
        var item = MenuActions.Tag(new MenuItem
        {
            Header = "Show address bar while viewing pictures",
            ToggleType = MenuItemToggleType.CheckBox,
        }, "setting.explorer.addressBarOverPictures");
        menu.Opening += (_, _) => item.IsChecked = _settings.ShowAddressBarOverPictures;
        item.Click += (_, _) => SetAddressBarOverPictures(!_settings.ShowAddressBarOverPictures);
        menu.ItemsSource = new List<MenuItem> { item };
        return menu;
    }

    private void SetAddressBarOverPictures(bool on)
    {
        if (_settings.ShowAddressBarOverPictures == on) return;
        _settings.ShowAddressBarOverPictures = on;
        SaveSettingsAndPublish();
        UpdatePictureAddressRow();
    }
}
