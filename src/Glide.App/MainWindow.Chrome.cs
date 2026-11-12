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
        CornerCounterText.Text = text;
        CornerCounterShadowText.Text = text;
        UpdateFolderCounter(folderPath);
    }

    private async void UpdateFolderCounter(string folderPath)
    {
        FolderCounterText.IsVisible = false;
        if (!_settings.ShowFolderCounter || string.IsNullOrWhiteSpace(folderPath)) return;
        var includeHidden = _settings.FolderNavIncludeHidden;
        var order = _settings.FolderNavOrder;
        var grandparent = _settings.FolderCounterIncludeGrandparent;
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

    private string CurrentAccentHex() => string.Equals(_settings.AccentChoice, "Custom", StringComparison.OrdinalIgnoreCase) ? NormalizeHex(_settings.CustomAccentHex, ThemePalette.GlideBlue) : ResolveAccentHex(_settings.AccentChoice);

    private static string ResolveAccentHex(string? choice) => ThemePalette.ResolveAccentHex(choice);

    private static string ResolveAccentSoftHex(string? choice) => ThemePalette.ResolveAccentSoftHex(choice);

    private static string ResolveGlowHex(string? choice, string accentChoice) => ThemePalette.ResolveGlowHex(choice, accentChoice);

    private static string NormalizeHex(string? value, string fallback) => ThemePalette.NormalizeHex(value, fallback);

    private static void ApplyPalette(string? themeChoice, string accentChoice, string? glowChoice, int glowIntensityPercent, string customAccentHex, string customGlowHex, string backgroundChoice, string customBackgroundHex)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        static SolidColorBrush B(string value) => new(Color.Parse(value));

        var theme = themeChoice is Glide.Core.Settings.SettingChoiceNames.Light or Glide.Core.Settings.SettingChoiceNames.Neutral ? themeChoice : Glide.Core.Settings.SettingChoiceNames.Dark;
        var accent = string.Equals(accentChoice, "Custom", StringComparison.OrdinalIgnoreCase) ? NormalizeHex(customAccentHex, ThemePalette.GlideBlue) : ResolveAccentHex(accentChoice);
        var accentColor = Color.Parse(accent);
        var glowHex = string.Equals(glowChoice, "Custom", StringComparison.OrdinalIgnoreCase) ? NormalizeHex(customGlowHex, accent) : ResolveGlowHex(glowChoice, accentChoice);
        var glowColor = Color.Parse(glowHex);
        var glowStrength = Math.Clamp(glowIntensityPercent, 0, 100) / 100.0;
        static Color Shift(Color c, double factor) => Color.FromArgb(c.A,
            (byte)Math.Clamp((int)Math.Round(c.R * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(c.G * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(c.B * factor), 0, 255));
        static SolidColorBrush A(Color c, byte alpha) => new(Color.FromArgb(alpha, c.R, c.G, c.B));
        byte GlowAlpha(int full) => (byte)Math.Clamp((int)Math.Round(full * glowStrength), 0, 255);

        resources["SystemAccentColor"] = accentColor;
        resources["SystemAccentColorLight1"] = Shift(accentColor, 1.12);
        resources["SystemAccentColorLight2"] = Shift(accentColor, 1.24);
        resources["SystemAccentColorLight3"] = Shift(accentColor, 1.36);
        resources["SystemAccentColorDark1"] = Shift(accentColor, 0.86);
        resources["SystemAccentColorDark2"] = Shift(accentColor, 0.72);
        resources["SystemAccentColorDark3"] = Shift(accentColor, 0.58);
        resources["BrushAccent"] = B(accent);
        resources["BrushNavAccent"] = B(accent);

        if (theme == "Light")
        {
            // Existing Light profile intentionally remains unchanged.
            resources["BrushWindow"] = B("#F4F5F6"); resources["BrushViewport"] = B("#E9EBED"); resources["BrushChrome"] = B("#EEF0F1");
            resources["BrushTabStrip"] = B("#E6E9EB"); resources["BrushSurface"] = B("#FFFFFF"); resources["BrushSurfaceRaised"] = B("#FFFFFF");
            resources["BrushSurfaceHover"] = B("#DDEAF5"); resources["BrushCard"] = B("#FFFFFF"); resources["BrushCardAccent"] = B("#D8E8F7");
            resources["BrushBorder"] = B("#BCC5CC"); resources["BrushBorderSoft"] = B("#D4DADF"); resources["BrushText"] = B("#171A1C");
            resources["BrushMuted"] = B("#56616B"); resources["BrushMuted2"] = B("#79838C"); resources["BrushSettingsRail"] = B("#E8EBED");
            resources["BrushChromeButton"] = B("#F3F3F3"); resources["BrushChromeButtonHover"] = B("#E5E5E5"); resources["BrushChromeButtonPressed"] = B("#DADADA"); resources["BrushChromeButtonBorder"] = B("#F3F3F3");
            resources["BrushTabHover"] = B("#E7E7E7"); resources["BrushTabActive"] = B("#FFFFFF"); resources["BrushTabCloseHover"] = B("#D0D0D0");
            resources["BrushStatusSurface"] = B("#EAF7F8F9"); resources["BrushStatusButton"] = B("#FFFFFF"); resources["BrushStatusButtonHover"] = B("#E4EFF7"); resources["BrushStatusButtonPressed"] = B("#D5E7F4");
            resources["BrushStatusBorder"] = B("#C8D0D6"); resources["BrushStatusButtonBorder"] = B("#C7D0D7"); resources["BrushStatusNavBorder"] = B("#8FBAD3");
            resources["BrushSettingsNavHover"] = B("#DDE5EB"); resources["BrushSettingsNavSelected"] = B("#CFE1F0");
            resources["BrushInput"] = B("#FFFFFF"); resources["BrushInputBorder"] = B("#AEB8C0"); resources["BrushSearch"] = B("#FFFFFF"); resources["BrushFooter"] = B("#ECEFF1");
            resources["BrushPrimaryAction"] = B("#2C76AA"); resources["BrushPrimaryActionHover"] = B("#3787BC"); resources["BrushSecondaryAction"] = B("#F8F9FA"); resources["BrushSecondaryActionHover"] = B("#E5EAEE");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }
        else if (theme == "Neutral")
        {
            resources["BrushWindow"] = B("#C4C7C9"); resources["BrushViewport"] = B("#B8BCC0"); resources["BrushChrome"] = B("#B8BBBD");
            resources["BrushTabStrip"] = B("#B7BABD"); resources["BrushSurface"] = B("#D4D6D8"); resources["BrushSurfaceRaised"] = B("#DADCDD");
            resources["BrushSurfaceHover"] = B("#C5D3DD"); resources["BrushCard"] = B("#D1D3D5"); resources["BrushCardAccent"] = B("#C5D4DF");
            resources["BrushBorder"] = B("#9EA4A9"); resources["BrushBorderSoft"] = B("#B4B9BD"); resources["BrushText"] = B("#202428");
            resources["BrushMuted"] = B("#525B62"); resources["BrushMuted2"] = B("#687177"); resources["BrushSettingsRail"] = B("#BEC1C4");
            resources["BrushChromeButton"] = B("#B8BBBD"); resources["BrushChromeButtonHover"] = B("#C6C9CB"); resources["BrushChromeButtonPressed"] = B("#AEB2B4"); resources["BrushChromeButtonBorder"] = B("#B8BBBD");
            resources["BrushTabHover"] = B("#C8CBCD"); resources["BrushTabActive"] = B("#D8DADB"); resources["BrushTabCloseHover"] = B("#B5B9BC");
            resources["BrushStatusSurface"] = B("#E6CDD0D2"); resources["BrushStatusButton"] = B("#D6D8D9"); resources["BrushStatusButtonHover"] = B("#C7D8E3"); resources["BrushStatusButtonPressed"] = B("#B9CDD9");
            resources["BrushStatusBorder"] = B("#A3AAAF"); resources["BrushStatusButtonBorder"] = B("#A7AEB3"); resources["BrushStatusNavBorder"] = B("#769CB4");
            resources["BrushSettingsNavHover"] = B("#C6CDD2"); resources["BrushSettingsNavSelected"] = B("#B9CCD9");
            resources["BrushInput"] = B("#D9DBDC"); resources["BrushInputBorder"] = B("#969EA4"); resources["BrushSearch"] = B("#D9DBDC"); resources["BrushFooter"] = B("#BEC2C5");
            resources["BrushPrimaryAction"] = B("#2C76AA"); resources["BrushPrimaryActionHover"] = B("#3787BC"); resources["BrushSecondaryAction"] = B("#D2D4D5"); resources["BrushSecondaryActionHover"] = B("#C4C8CB");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }
        else
        {
            // Existing Dark profile intentionally remains unchanged and remains the default.
            resources["BrushWindow"] = B("#181A1D"); resources["BrushViewport"] = B("#0E0F10"); resources["BrushChrome"] = B("#1A2224");
            resources["BrushTabStrip"] = B("#161616"); resources["BrushSurface"] = B("#1B1E23"); resources["BrushSurfaceRaised"] = B("#202329");
            resources["BrushSurfaceHover"] = B("#21364A"); resources["BrushCard"] = B("#191C1F"); resources["BrushCardAccent"] = B("#22384C");
            resources["BrushBorder"] = B("#343C42"); resources["BrushBorderSoft"] = B("#2A3035"); resources["BrushText"] = B("#F2F4F6");
            resources["BrushMuted"] = B("#AAB5C2"); resources["BrushMuted2"] = B("#7E8995"); resources["BrushSettingsRail"] = B("#1A1D20");
            resources["BrushChromeButton"] = B("#161616"); resources["BrushChromeButtonHover"] = B("#303030"); resources["BrushChromeButtonPressed"] = B("#3A3A3A"); resources["BrushChromeButtonBorder"] = B("#161616");
            resources["BrushTabHover"] = B("#292929"); resources["BrushTabActive"] = B("#333333"); resources["BrushTabCloseHover"] = B("#474747");
            resources["BrushStatusSurface"] = B("#D9202327"); resources["BrushStatusButton"] = B("#1B1F24"); resources["BrushStatusButtonHover"] = B("#23384A"); resources["BrushStatusButtonPressed"] = B("#2A455E");
            resources["BrushStatusBorder"] = B("#30373D"); resources["BrushStatusButtonBorder"] = B("#303840"); resources["BrushStatusNavBorder"] = B("#28536A");
            resources["BrushSettingsNavHover"] = B("#202A33"); resources["BrushSettingsNavSelected"] = B("#29465F");
            resources["BrushInput"] = B("#202226"); resources["BrushInputBorder"] = B("#30363C"); resources["BrushSearch"] = B("#202328"); resources["BrushFooter"] = B("#181B1E");
            resources["BrushPrimaryAction"] = B("#2C76AA"); resources["BrushPrimaryActionHover"] = B("#3787BC"); resources["BrushSecondaryAction"] = B("#1A1C20"); resources["BrushSecondaryActionHover"] = B("#242A30");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        }

        var bright = theme != "Dark";
        var background = backgroundChoice switch
        {
            "Black" => "#000000", "Dark gray" => "#202124", "Neutral gray" => "#777777", "Light gray" => "#D0D0D0", "White" => "#FFFFFF",
            "Custom" => NormalizeHex(customBackgroundHex, ThemePalette.FallbackBackground), _ => null
        };
        if (background is not null) resources["BrushViewport"] = B(background);

        // Glow is deliberately limited to transient/selection surfaces. At the default Follow-accent
        // 100% setting, Dark/Light retain Alpha-5's established accent-derived interaction strengths.
        // At 0%, the ordinary theme hover surfaces remain intact rather than becoming transparent.
        resources["BrushAccentSoft"] = string.Equals(glowChoice, "Follow accent", StringComparison.OrdinalIgnoreCase) && glowStrength >= 0.999
            ? B(ResolveAccentSoftHex(accentChoice))
            : A(glowColor, GlowAlpha(bright ? 54 : 66));
        if (glowStrength > 0)
        {
            resources["BrushSurfaceHover"] = A(glowColor, GlowAlpha(bright ? 30 : 34));
            resources["BrushCardAccent"] = A(glowColor, GlowAlpha(bright ? 34 : 38));
            resources["BrushStatusButtonHover"] = A(glowColor, GlowAlpha(bright ? 34 : 40));
            resources["BrushStatusButtonPressed"] = A(glowColor, GlowAlpha(bright ? 52 : 62));
            resources["BrushSettingsNavHover"] = A(glowColor, GlowAlpha(bright ? 28 : 32));
            resources["BrushSettingsNavSelected"] = A(glowColor, GlowAlpha(bright ? 48 : 58));
        }
        resources["BrushStatusNavBorder"] = A(accentColor, bright ? (byte)150 : (byte)135);
        resources["BrushPrimaryAction"] = B(accent);
        resources["BrushPrimaryActionHover"] = new SolidColorBrush(Shift(accentColor, bright ? 0.90 : 1.10));
    }
}
