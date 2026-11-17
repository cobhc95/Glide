using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Glide.App.Services;
using Glide.Core;

namespace Glide.App;

/// <summary>Presentation chrome: counters, status stats, title, theme and accent palette. Split from MainWindow (4.5.2); behavior unchanged.</summary>
public partial class MainWindow
{
    private void UpdatePictureCounter()
    {
        if (CornerCounterText is null || CornerCounterShadowText is null) return;

        var path = _currentPath ?? string.Empty;
        var file = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
        var stem = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileNameWithoutExtension(path);
        var extension = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetExtension(path).TrimStart('.');
        var folderPath = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
        var folder = string.IsNullOrWhiteSpace(folderPath)
            ? string.Empty
            : Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(folderPath)) folder = folderPath;
        var width = Math.Max(0, _currentPixelWidth);
        var height = Math.Max(0, _currentPixelHeight);
        var megapixels = width > 0 && height > 0 ? (width * (double)height / 1_000_000d).ToString("0.##") : "0";
        var format = string.IsNullOrWhiteSpace(path) ? string.Empty : CodecCapabilityRegistry.GetLongestExtension(path).TrimStart('.').ToUpperInvariant();
        var template = string.IsNullOrWhiteSpace(_settings.PictureCounterTemplate) ? "[{index}/{total}]" : _settings.PictureCounterTemplate;
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{index}"] = _navigator.Count > 0 ? (_navigator.Index + 1).ToString() : "0",
            ["{total}"] = _navigator.Count.ToString(),
            ["{file}"] = file,
            ["{filename}"] = file,
            ["{stem}"] = stem,
            ["{extension}"] = extension,
            ["{format}"] = format,
            ["{folder}"] = folder,
            ["{folderpath}"] = folderPath,
            ["{path}"] = path,
            ["{width}"] = width.ToString(),
            ["{height}"] = height.ToString(),
            ["{dimensions}"] = width > 0 && height > 0 ? $"{width}×{height}" : string.Empty,
            ["{megapixels}"] = megapixels,
            ["{filesize}"] = _currentFileSize > 0 ? FormatFileSize(_currentFileSize) : "0 B",
            ["{filesizebytes}"] = Math.Max(0, _currentFileSize).ToString(),
            ["{zoom}"] = $"{Viewport.ZoomPercent}%"
        };
        var text = template;
        foreach (var (token, value) in replacements) text = text.Replace(token, value, StringComparison.OrdinalIgnoreCase);
        text = ApplyTextOverlayLineBreaks(text);
        CornerCounterText.Text = text;
        CornerCounterShadowText.Text = text;
        UpdateFolderCounter(folderPath);
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
        if (!_settings.ShowFolderCounter || string.IsNullOrWhiteSpace(folderPath))
        {
            FolderCounterText.IsVisible = false;
            return;
        }
        var includeHidden = _settings.FolderNavIncludeHidden;
        var order = _settings.FolderNavOrder;
        var grandparent = _settings.FolderCounterIncludeGrandparent;
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
        if (!_settings.ShowFolderCounter || !string.Equals(Path.GetDirectoryName(_currentPath ?? ""), folderPath, StringComparison.OrdinalIgnoreCase) || grandparent != _settings.FolderCounterIncludeGrandparent || includeHidden != _settings.FolderNavIncludeHidden || order != _settings.FolderNavOrder) return;
        _folderCounterCache = (folderPath, includeHidden, order, grandparent, result);
        FolderCounterText.Text = result;
        FolderCounterText.IsVisible = result.Length > 0;
    }

    private void ApplyPictureCounterVisuals()
    {
        if (CornerCounterHost is null) return;
        var size = Math.Clamp(_settings.PictureCounterFontSize, 8, 48);
        CornerCounterText.FontSize = size;
        CornerCounterShadowText.FontSize = size;
        var weight = _settings.PictureCounterBold ? FontWeight.SemiBold : FontWeight.Normal;
        CornerCounterText.FontWeight = weight;
        CornerCounterShadowText.FontWeight = weight;
        CornerCounterText.Opacity = Math.Clamp(_settings.PictureCounterOpacity, 10, 100) / 100.0;
        CornerCounterShadowText.Opacity = _settings.PictureCounterShadow ? CornerCounterText.Opacity * 0.85 : 0;
        var colour = _settings.PictureCounterColor switch
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

        var position = _settings.PictureCounterPosition ?? "Top right";
        CornerCounterHost.HorizontalAlignment = position.Contains("left", StringComparison.OrdinalIgnoreCase)
            ? Avalonia.Layout.HorizontalAlignment.Left
            : position.Contains("center", StringComparison.OrdinalIgnoreCase)
                ? Avalonia.Layout.HorizontalAlignment.Center
                : Avalonia.Layout.HorizontalAlignment.Right;
        CornerCounterHost.VerticalAlignment = position.StartsWith("Bottom", StringComparison.OrdinalIgnoreCase)
            ? Avalonia.Layout.VerticalAlignment.Bottom
            : Avalonia.Layout.VerticalAlignment.Top;
        CornerCounterHost.Margin = CornerCounterHost.VerticalAlignment == Avalonia.Layout.VerticalAlignment.Top
            ? new Thickness(15, 28, 15, 0)
            : new Thickness(15, 0, 15, _settings.ShowStatusSurface ? 82 : 16);
        UpdatePictureCounter();
    }

    /// <summary>
    /// "Text overlay border": one box around every line of the overlay (the counter and the folder position line), styled
    /// like the Image information panel; a light box in the light theme so the overlay colours stay readable.
    /// </summary>
    private void ApplyTextOverlayBorder()
    {
        if (!_settings.PictureCounterBorder)
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
        if (string.IsNullOrWhiteSpace(_currentPath) || _navigator.Count == 0)
        {
            StatusStatsText.Text = "Ready";
            return;
        }

        var parts = new List<string>();
        if (_settings.StatusStatFileName) parts.Add(_settings.FullPathInTitle ? _currentPath : Path.GetFileName(_currentPath));
        if (_settings.StatusStatIndex) parts.Add($"{_navigator.Index + 1} / {_navigator.Count}");
        if (_settings.StatusStatResolution && _currentPixelWidth > 0) parts.Add($"{_currentPixelWidth} × {_currentPixelHeight}");
        if (_settings.StatusStatZoom) parts.Add($"{Viewport.ZoomPercent}%");
        if (_settings.StatusStatFileSize && _currentFileSize > 0) parts.Add(FormatFileSize(_currentFileSize));
        if (_settings.StatusStatFormat) parts.Add(CodecCapabilityRegistry.GetLongestExtension(_currentPath).TrimStart('.').ToUpperInvariant());
        if (Viewport.TryGetSelectionPixelRect(out var selection)) parts.Add($"Sel {selection.Width} × {selection.Height}");
        // Segments use non-breaking spaces so a wrapped stats line breaks only at the separators,
        // never in the middle of a value such as "3744 × 5616".
        StatusStatsText.Text = parts.Count == 0
            ? Path.GetFileName(_currentPath)
            : string.Join("  •  ", parts.Select(p => p.Replace(' ', ' ')));
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

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

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
