using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Glide.App.Services;
using Glide.Core;
using Glide.Core.Settings;
using Glide.Core.Status;

namespace Glide.App;

/// <summary>Presentation chrome: counters, status stats, title, theme and accent palette. Split from MainWindow (4.5.2); behavior unchanged.</summary>
public partial class MainWindow
{
    /// <summary>Stage D (C9): the one snapshot of what the status bar and the text overlay show.</summary>
    private ViewerStatusModel CurrentViewerStatus(bool includeSelection = false)
    {
        (int, int)? selection = includeSelection && Viewport.TryGetSelectionPixelRect(out var rect) ? (rect.Width, rect.Height) : null;
        return new ViewerStatusModel(_currentPath, _navigator.Index, _navigator.Count, _currentPixelWidth, _currentPixelHeight,
            _currentFileSize, Viewport.ZoomPercent, selection);
    }

    /// <summary>Stage D (C9): the current image, its index or its dimensions changed; refresh both status surfaces.</summary>
    private void NotifyViewerStatusChanged()
    {
        UpdatePictureCounter();
        UpdateStatusStats();
        UpdatePictureAddressRow();
    }

    private void UpdatePictureCounter()
    {
        if (CornerCounterText is null || CornerCounterShadowText is null) return;
        var model = CurrentViewerStatus();
        var text = ApplyTextOverlayLineBreaks(ViewerStatusFormatter.CounterText(model, OverlayOptionsView.PictureCounterTemplate));
        CornerCounterText.Text = text;
        CornerCounterShadowText.Text = text;
        UpdateFolderCounter(ViewerStatusFormatter.FolderPathOf(model.Path));
    }

    /// <summary><c>&lt;br&gt;</c> (also <c>&lt;br/&gt;</c>, <c>&lt;br /&gt;</c>, any case) and the typed two-character
    /// escape <c>\n</c> in the text overlay template start a new line.</summary>
    internal static string ApplyTextOverlayLineBreaks(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"<br\s*/?>|\\n", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Last folder-position result. Restyling the text overlay (font size spinner, colour...) re-runs UpdatePictureCounter on
    // every tick; without this each tick hid the folder line, re-enumerated the sibling folders on the thread pool and
    // re-laid the overlay out when the result arrived, which is what made dragging the font size feel slow.
    private (string Folder, bool Hidden, string? Order, bool Grandparent, string Result)? _folderCounterCache;

    private async void UpdateFolderCounter(string folderPath)
    {
        if (!NavigationOptionsView.Counter || string.IsNullOrWhiteSpace(folderPath))
        {
            FolderCounterText.IsVisible = false;
            return;
        }
        var includeHidden = NavigationOptionsView.FolderNavIncludeHidden;
        var order = NavigationOptionsView.FolderNavOrder;
        var grandparent = NavigationOptionsView.FolderCounterIncludeGrandparent;
        if (_folderCounterCache is { } cached && cached.Hidden == includeHidden && cached.Grandparent == grandparent &&
            string.Equals(cached.Order, order, StringComparison.Ordinal) && string.Equals(cached.Folder, folderPath, StringComparison.OrdinalIgnoreCase))
        {
            FolderCounterText.Text = cached.Result;
            FolderCounterText.IsVisible = cached.Result.Length > 0;
            return;
        }
        FolderCounterText.IsVisible = false;
        var result = await Task.Run(() =>
        {
            try
            {
                var options = new SiblingFolderNavigationOptions(includeHidden, false, false, true, false, order, false);
                var parent = Directory.GetParent(folderPath)?.FullName;
                if (parent is null) return "";
                var siblings = SiblingFolderNavigationService.EnumerateChildDirectories(parent, options, folderPath);
                var index = Array.FindIndex(siblings, p => string.Equals(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
                if (index < 0) return "";
                var value = $"{index + 1}/{siblings.Length}";
                var grand = Directory.GetParent(parent)?.FullName;
                if (grandparent && grand is not null)
                {
                    var parents = SiblingFolderNavigationService.EnumerateChildDirectories(grand, options, parent);
                    var parentIndex = Array.FindIndex(parents, p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
                    if (parentIndex >= 0) value += $"/{parentIndex + 1}";
                }
                return value;
            }
            catch { return ""; }
        });
        if (!NavigationOptionsView.Counter || !string.Equals(Path.GetDirectoryName(_currentPath ?? ""), folderPath, StringComparison.OrdinalIgnoreCase) || grandparent != NavigationOptionsView.FolderCounterIncludeGrandparent || includeHidden != NavigationOptionsView.FolderNavIncludeHidden || order != NavigationOptionsView.FolderNavOrder) return;
        _folderCounterCache = (folderPath, includeHidden, order, grandparent, result);
        FolderCounterText.Text = result;
        FolderCounterText.IsVisible = result.Length > 0;
    }

    private void ApplyPictureCounterVisuals()
    {
        if (CornerCounterHost is null) return;
        var size = Math.Clamp(OverlayOptionsView.PictureCounterFontSize, 8, 48);
        CornerCounterText.FontSize = size;
        CornerCounterShadowText.FontSize = size;
        var weight = OverlayOptionsView.PictureCounterBold ? FontWeight.SemiBold : FontWeight.Normal;
        CornerCounterText.FontWeight = weight;
        CornerCounterShadowText.FontWeight = weight;
        CornerCounterText.Opacity = Math.Clamp(OverlayOptionsView.PictureCounterOpacity, 10, 100) / 100.0;
        CornerCounterShadowText.Opacity = OverlayOptionsView.PictureCounterShadow ? CornerCounterText.Opacity * 0.85 : 0;
        var colour = OverlayOptionsView.PictureCounterColor switch
        {
            "Soft gray" => IsDarkTheme() ? "#D2D8DE" : "#56616B",
            "Accent" => CurrentAccentHex(),
            "Amber" => "#F1B84B",
            _ => IsDarkTheme() ? "#FFFFFF" : "#20262C"
        };
        CornerCounterText.Foreground = new SolidColorBrush(Color.Parse(colour));
        FolderCounterText.Foreground = CornerCounterText.Foreground;
        FolderCounterText.FontSize = size;
        FolderCounterText.FontWeight = weight;
        FolderCounterText.Opacity = CornerCounterText.Opacity * 0.7;
        ApplyTextOverlayBorder();
        if (!PlaceTextOverlay())
        {
            // In the side tab pane or the title bar: no picture position, margins or offsets. The lines form one block:
            // Automatic aligns them together (title bar: by its left/center/right side; tab pane: left).
            var barPlacement = EffectiveTextOverlayPlacement;
            ApplyTextOverlayAlignment(EffectiveTextOverlayAlignment, BarAutomaticAlignment(barPlacement));
            UpdatePictureCounter();
            return;
        }

        var position = OverlayOptionsView.PictureCounterAnchor(SideTabsActive);   // Custom: its anchor (+ offset below)
        CornerCounterHost.HorizontalAlignment = position.Contains("left", StringComparison.OrdinalIgnoreCase)
            ? Avalonia.Layout.HorizontalAlignment.Left
            : position.Contains("center", StringComparison.OrdinalIgnoreCase)
                ? Avalonia.Layout.HorizontalAlignment.Center
                : Avalonia.Layout.HorizontalAlignment.Right;
        CornerCounterHost.VerticalAlignment = position.StartsWith("Bottom", StringComparison.OrdinalIgnoreCase)
            ? Avalonia.Layout.VerticalAlignment.Bottom
            : Avalonia.Layout.VerticalAlignment.Top;
        ApplyTextOverlayAlignment(EffectiveTextOverlayAlignment);   // the alignment of the card for the current tab layout
        var (offsetX, offsetY) = OverlayOptionsView.PictureCounterOffset(SideTabsActive);
        CornerCounterHost.RenderTransform = offsetX == 0 && offsetY == 0 ? null : new TranslateTransform(offsetX, offsetY);
        CornerCounterHost.Margin = TextOverlayPictureMargin();
        UpdatePictureCounter();
    }

    /// <summary>"Text overlay alignment": Automatic keeps the original layout (text left, folder position line right).</summary>
    /// <remarks>
    /// In a bar (<paramref name="barAutomatic"/> set) Automatic must not split the lines: its picture layout puts the
    /// counter at the left and the folder line at the right of the block, which in the title bar read as two labels
    /// drifting apart (one up-left, one down-right). There Automatic aligns every line the same way instead.
    /// </remarks>
    private void ApplyTextOverlayAlignment(string? alignment, string? barAutomatic = null)
    {
        if (barAutomatic is not null && alignment is not ("Left" or "Center" or "Right")) alignment = barAutomatic;
        var (text, line) = alignment switch
        {
            "Left" => (TextAlignment.Left, Avalonia.Layout.HorizontalAlignment.Left),
            "Center" => (TextAlignment.Center, Avalonia.Layout.HorizontalAlignment.Center),
            "Right" => (TextAlignment.Right, Avalonia.Layout.HorizontalAlignment.Right),
            _ => (TextAlignment.Left, Avalonia.Layout.HorizontalAlignment.Right)
        };
        CornerCounterText.TextAlignment = text;
        CornerCounterShadowText.TextAlignment = text;
        FolderCounterText.TextAlignment = text;
        FolderCounterText.HorizontalAlignment = line;
    }

    private static string BarAutomaticAlignment(string placement) => placement switch
    {
        TextOverlayPlacements.TitleBarCenter => "Center",
        TextOverlayPlacements.TitleBarRight => "Right",
        _ => "Left"
    };

    /// <summary>
    /// "Text overlay border": one box around every line of the overlay (the counter and the folder position line), styled
    /// like the Image information panel; a light box in the light theme so the overlay colours stay readable.
    /// </summary>
    private void ApplyTextOverlayBorder()
    {
        if (!OverlayOptionsView.PictureCounterBorder)
        {
            CornerCounterHost.Background = null;
            CornerCounterHost.BorderBrush = null;
            CornerCounterHost.BorderThickness = default;
            CornerCounterHost.CornerRadius = default;
            CornerCounterHost.Padding = default;
            return;
        }
        CornerCounterHost.Background = new SolidColorBrush(Color.Parse(IsDarkTheme() ? "#D91B1E23" : "#E6F7F8FA"));
        CornerCounterHost.BorderBrush = new SolidColorBrush(Color.Parse(CurrentAccentHex()));
        CornerCounterHost.BorderThickness = new Thickness(1);
        CornerCounterHost.CornerRadius = new CornerRadius(8);
        CornerCounterHost.Padding = new Thickness(11, 7);
    }

    private void UpdateViewportScrollbars()
    {
        if (ViewportHScroll is null || ViewportVScroll is null) return;
        if (_wholeAppOverlayMode)
        {
            ViewportHScroll.IsVisible = false;
            ViewportVScroll.IsVisible = false;
            return;
        }
        var state = Viewport.GetScrollState();
        _syncingViewportScrollbars = true;
        try
        {
            ViewportHScroll.Minimum = 0;
            ViewportHScroll.Maximum = Math.Max(0, state.HorizontalMaximum);
            ViewportHScroll.ViewportSize = Math.Max(1, Viewport.Bounds.Width);
            ViewportHScroll.Value = Math.Clamp(state.HorizontalValue, 0, ViewportHScroll.Maximum);
            ViewportHScroll.IsVisible = _settings.ShowScrollbars && state.HorizontalVisible && ImageView.IsVisible;

            ViewportVScroll.Minimum = 0;
            ViewportVScroll.Maximum = Math.Max(0, state.VerticalMaximum);
            ViewportVScroll.ViewportSize = Math.Max(1, Viewport.Bounds.Height);
            ViewportVScroll.Value = Math.Clamp(state.VerticalValue, 0, ViewportVScroll.Maximum);
            ViewportVScroll.IsVisible = _settings.ShowScrollbars && state.VerticalVisible && ImageView.IsVisible;
        }
        finally
        {
            _syncingViewportScrollbars = false;
        }
    }

    private void UpdateStatusStats()
    {
        if (StatusStatsText is null) return;
        UpdateSaveButtons();
        var model = CurrentViewerStatus(includeSelection: true);
        StatusStatsText.Text = ViewerStatusFormatter.StatsText(model, StatusBarOptionsView, _settings.FullPathInTitle);
        if (!model.HasImage) return;
        ToolTip.SetTip(StatusStatsText, _currentPath);
        ScheduleStatusRearrangeForStats(StatusStatsText.Text?.Length ?? 0);
    }

    private void UpdateWindowTitle(string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(_currentPath) || !ImageView.IsVisible)
        {
            if (HomeHost.IsVisible) Title = AppVersion.HomeTitle;
            return;
        }
        var display = _settings.FullPathInTitle ? _currentPath : Path.GetFileName(_currentPath);
        var index = _navigator.Count > 0 ? $"[{_navigator.Index + 1}/{_navigator.Count}]" : string.Empty;
        Title = prefix is null
            ? AppVersion.ViewerTitle(display, index, Viewport.ZoomPercent)
            : AppVersion.ViewerTitle(prefix, display);
    }

    private static string FormatFileSize(long bytes) => ViewerStatusFormatter.FormatFileSize(bytes);

    private static void SetActiveClass(Control control, bool active)
    {
        if (active)
        {
            if (!control.Classes.Contains("active")) control.Classes.Add("active");
        }
        else control.Classes.Remove("active");
    }

    private bool IsDarkTheme() => string.Equals(_settings.ThemeChoice, Glide.Core.Settings.SettingChoiceNames.Dark, StringComparison.OrdinalIgnoreCase);

    private string CurrentAccentHex() => string.Equals(_settings.AccentChoice, "Custom", StringComparison.OrdinalIgnoreCase) ? ThemePalette.NormalizeHex(_settings.CustomAccentHex, ThemePalette.GlideBlue) : ThemePalette.ResolveAccentHex(_settings.AccentChoice);
}
