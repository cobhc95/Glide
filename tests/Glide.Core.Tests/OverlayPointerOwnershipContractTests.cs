namespace Glide.Core.Tests;

// Source-text contract kept: this asserts on production source (UI code-behind, native/installer files or a repo-wide scan) that cannot be run headlessly; behavioural coverage lives in the other test files.
public sealed class OverlayPointerOwnershipContractTests
{
    [Fact]
    public void Overlay_release_does_not_cancel_viewport_owned_fullscreen_clicks()
    {
        var source = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs").Replace("\r\n", "\n");

        Assert.Contains("var ownsOverlayPointer = ReferenceEquals(_capturedPointer, e.Pointer)", source, StringComparison.Ordinal);
        Assert.Contains("if (!ownsOverlayPointer) return;", source, StringComparison.Ordinal);
        Assert.Matches(@"try \{ _capturedPointer\?\.Capture\(null\); \} catch( \{ /\*[^*]*\*/ \}| \{ \})", source);
        Assert.DoesNotContain("var middle = e.InitialPressMouseButton == MouseButton.Middle;\n        e.Pointer.Capture(null);", source, StringComparison.Ordinal);
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
