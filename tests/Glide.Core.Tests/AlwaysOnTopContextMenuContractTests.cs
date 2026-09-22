namespace Glide.Core.Tests;

/// <summary>
/// Contract for the tickable "Always on top" context-menu item. It must be available in both the
/// ordinary windowed viewer menu and the fullscreen chrome menu, and its tick must reflect the
/// active topmost state for the current mode.
/// </summary>
public sealed class AlwaysOnTopContextMenuContractTests
{
    [Fact]
    public void Windowed_and_fullscreen_context_menus_expose_a_tickable_always_on_top_item()
    {
        var mainWindow = ReadSource("src", "Glide.App", "MainWindow.axaml.cs").Replace("\r\n", "\n");

        var viewerMenu = MethodBody(mainWindow, "private ContextMenu BuildViewerContextMenu()");
        Assert.Contains("BuildAlwaysOnTopMenuItem()", viewerMenu, StringComparison.Ordinal);

        var fullscreenMenu = MethodBody(mainWindow, "private void ShowFullscreenChromeContextMenu()");
        Assert.Contains("BuildAlwaysOnTopMenuItem()", fullscreenMenu, StringComparison.Ordinal);

        var factory = MethodBody(mainWindow, "private MenuItem BuildAlwaysOnTopMenuItem()");
        Assert.Contains("ActiveAlwaysOnTop", factory, StringComparison.Ordinal);
        Assert.Contains("AlwaysOnTopClicked", factory, StringComparison.Ordinal);
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
