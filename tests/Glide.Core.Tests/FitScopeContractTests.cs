namespace Glide.Core.Tests;

/// <summary>
/// Contract for the "Fit control scope" setting. Pressing the status bar Fit Width / Fit Height
/// controls must route through <c>ApplyStatusBarFit</c> so the chosen mode can carry to the rest of
/// the session and, when configured, persist as the default view for future launches.
/// </summary>
// Source-text contract kept: this asserts on production source (UI code-behind, native/installer files or a repo-wide scan) that cannot be run headlessly; behavioural coverage lives in the other test files.
// Stage B: the routing and scope checks moved to a behaviour test (HotkeyEndToEndTests.Status_bar_Fit_buttons_apply_their_mode_through_the_Fit_control_scope).
[Trait("Area", "Navigation")]
public sealed class FitScopeContractTests
{
    [Fact]
    public void New_images_use_the_effective_session_default_view_mode()
    {
        var mainWindow = ReadSource("src", "Glide.App", "MainWindow.axaml.cs").Replace("\r\n", "\n");
        Assert.Contains("Viewport.ApplyViewMode(EffectiveDefaultViewMode())", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_bar_has_a_fit_image_button_beside_fit_width_and_height()
    {
        var axaml = ReadSource("src", "Glide.App", "MainWindow.axaml").Replace("\r\n", "\n");

        var groupStart = axaml.IndexOf("x:Name=\"FitGroup\"", StringComparison.Ordinal);
        Assert.True(groupStart >= 0, "The status bar Fit group must exist.");
        var groupEnd = axaml.IndexOf("</StackPanel>", groupStart, StringComparison.Ordinal);
        Assert.True(groupEnd > groupStart, "The status bar Fit group must be a closed panel.");
        var group = axaml.Substring(groupStart, groupEnd - groupStart);

        Assert.Contains("StatusFitImageButton", group, StringComparison.Ordinal);
        Assert.Contains("StatusFitWidthButton", group, StringComparison.Ordinal);
        Assert.Contains("StatusFitHeightButton", group, StringComparison.Ordinal);
        Assert.Contains("Click=\"FitImageClicked\"", group, StringComparison.Ordinal);
        // The Fit image control must reuse the shared vector "Fit" glyph rather than a font/emoji.
        Assert.Contains("Kind=\"Fit\"", group, StringComparison.Ordinal);
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
            if (File.Exists(candidate)) return SourceText.Read(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
