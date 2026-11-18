using Glide.Core.Settings;
using Glide.Core.Status;

namespace Glide.Core.Tests;

[Trait("Area", "StatusBar")]
public sealed class ViewerStatusFormatterTests
{
    private static readonly string ImagePath = Path.Combine(Path.GetTempPath(), "album", "photo.jpg");

    [Fact]
    public void Stats_without_an_image_read_ready()
    {
        var model = new ViewerStatusModel(null, 0, 0, 0, 0, 0, 100);
        Assert.Equal("Ready", ViewerStatusFormatter.StatsText(model, new StatusBarOptions(new GlideSettingsState()), false));
    }

    [Fact]
    public void Stats_join_enabled_segments_with_non_breaking_spaces()
    {
        var state = new GlideSettingsState
        {
            StatusStatFileName = true, StatusStatIndex = true, StatusStatResolution = true,
            StatusStatZoom = false, StatusStatFileSize = true, StatusStatFormat = true
        };
        var model = new ViewerStatusModel(ImagePath, 2, 10, 3744, 5616, 2 * 1024 * 1024, 50, (10, 20));
        var text = ViewerStatusFormatter.StatsText(model, new StatusBarOptions(state), false);
        Assert.Equal("photo.jpg  •  3 / 10  •  3744 × 5616  •  2.0 MB  •  JPG  •  Sel 10 × 20", text);
    }

    [Fact]
    public void Counter_replaces_tokens_and_falls_back_to_the_default_template()
    {
        var model = new ViewerStatusModel(ImagePath, 0, 4, 2000, 1000, 500, 75);
        Assert.Equal("[1/4]", ViewerStatusFormatter.CounterText(model, " "));
        Assert.Equal("photo 2000×1000 2 MP 75% 500 B album",
            ViewerStatusFormatter.CounterText(model, "{stem} {dimensions} {megapixels} MP {zoom} {filesize} {folder}"));
    }
}
