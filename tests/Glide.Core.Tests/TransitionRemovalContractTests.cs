namespace Glide.Core.Tests;

public sealed class TransitionRemovalContractTests
{
    [Fact]
    public void Image_and_slideshow_transition_settings_are_removed()
    {
        var catalog = ReadSource("src", "Glide.Core", "Settings", "SettingsCatalog.cs");
        var settings = ReadSource("src", "Glide.App", "Settings", "GlideSettingsState.cs");
        var window = ReadSource("src", "Glide.App", "MainWindow.axaml.cs");

        Assert.DoesNotContain("\"view.transitionEnabled\"", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("\"view.transitionDuration\"", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("\"view.fullscreenTransitionEnabled\"", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("\"slideshow.useTransition\"", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowedTransitionEnabled", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SlideshowUseTransition", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("TransitionDurationDialog", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Image transitions", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Slideshow transitions", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Viewport_frame_swap_has_no_transition_engine()
    {
        var viewport = ReadSource("src", "Glide.App", "Controls", "ImageViewport.cs");

        Assert.DoesNotContain("_transitionActive", viewport, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginPendingTransition", viewport, StringComparison.Ordinal);
        Assert.DoesNotContain("TransitionOutgoingBitmap", viewport, StringComparison.Ordinal);
        Assert.Contains("transition = false", viewport, StringComparison.Ordinal);
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
