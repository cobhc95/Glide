namespace Glide.Core.Tests;

/// <summary>
/// Contracts for the chrome resize behaviour: title-bar utility icons yield one at a time, the
/// status bar wraps by real content width (no premature partial third line) and buttons only shrink
/// while an opt-in Auto-resize toggle is enabled.
/// </summary>
public sealed class StatusChromeLayoutContractTests
{
    [Fact]
    public void Title_bar_icons_disappear_one_at_a_time()
    {
        var mainWindow = ReadSource("src", "Glide.App", "MainWindow.axaml.cs").Replace("\r\n", "\n");
        var compact = MethodBody(mainWindow, "private void ApplyCompactChromeLayout()");

        Assert.Contains("visibleActions", compact, StringComparison.Ordinal);
        Assert.Contains("button.IsVisible = i < visibleActions", compact, StringComparison.Ordinal);
        // The old all-or-nothing threshold must not return.
        Assert.DoesNotContain("TitleActionHost.IsVisible = width >= 720", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_bar_rows_are_derived_from_wrapped_content_width()
    {
        var mainWindow = ReadSource("src", "Glide.App", "MainWindow.axaml.cs").Replace("\r\n", "\n");
        var applySize = MethodBody(mainWindow, "private void ApplyStatusBarSize()");

        Assert.Contains("NaturalControlsWidth(baseIconSize)", applySize, StringComparison.Ordinal);
        Assert.Contains("rowsAtConfiguredSize", applySize, StringComparison.Ordinal);
        Assert.Contains("ViewerStatusSurface.Height = double.NaN", applySize, StringComparison.Ordinal);
        // The old width-blind premature cap that made the arrows form a third line must be gone.
        Assert.DoesNotContain("metrics.Icon * scale * 2.15", applySize, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_resize_setting_exists_and_is_toggleable_from_the_status_bar_menu()
    {
        var catalog = ReadSource("src", "Glide.Core", "Settings", "SettingsCatalog.cs");
        Assert.Contains("\"status.autoResize\"", catalog, StringComparison.Ordinal);
        Assert.Contains("Auto-resize status bar", catalog, StringComparison.Ordinal);

        var mainWindow = ReadSource("src", "Glide.App", "MainWindow.axaml.cs").Replace("\r\n", "\n");
        var menu = MethodBody(mainWindow, "private void StatusSurfacePointerPressed(");
        Assert.Contains("Auto-resize", menu, StringComparison.Ordinal);
        Assert.Contains("_settings.StatusBarAutoFit = !_settings.StatusBarAutoFit", menu, StringComparison.Ordinal);

        var applySize = MethodBody(mainWindow, "private void ApplyStatusBarSize()");
        Assert.Contains("_settings.StatusBarAutoFit && rowsAtConfiguredSize > 2", applySize, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method: {signature}");
        var brace = source.IndexOf('{', start);
        Assert.True(brace > start, $"Missing body for: {signature}");
        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(brace, i - brace + 1);
        }
        throw new InvalidOperationException($"Unterminated body for: {signature}");
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
