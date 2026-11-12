using System.Text.Json;

namespace Glide.Core.Tests;

public sealed class OverlayAnimationAreaTests
{
    [Fact]
    public void Drag_in_any_direction_becomes_a_normalized_area()
    {
        var forward = OverlayAnimationArea.FromDrag(100, 50, 500, 250, 1000, 500);
        var backward = OverlayAnimationArea.FromDrag(500, 250, 100, 50, 1000, 500);
        Assert.Equal(new ChromeRect(.1, .1, .4, .4), forward);
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Tiny_or_degenerate_drags_are_rejected()
    {
        Assert.Null(OverlayAnimationArea.FromDrag(100, 100, 105, 400, 1000, 1000)); // 0.5% wide
        Assert.Null(OverlayAnimationArea.FromDrag(100, 100, 100, 100, 1000, 1000));
        Assert.Null(OverlayAnimationArea.FromDrag(0, 0, 100, 100, 0, 0));
        Assert.Null(OverlayAnimationArea.Normalize(double.NaN, 0, .5, .5));
    }

    [Fact]
    public void Areas_dragged_past_the_viewer_edge_are_clipped()
    {
        AssertClose(new ChromeRect(.5, 0, .5, .5), OverlayAnimationArea.Normalize(.5, -.2, .9, .7));
    }

    [Fact]
    public void Area_follows_the_viewer_when_it_is_resized()
    {
        var area = new ChromeRect(.25, .5, .5, .25);
        Assert.Equal(new ChromeRect(200, 300, 400, 150), OverlayAnimationArea.ToHost(area, 800, 600));
        Assert.Equal(new ChromeRect(480, 540, 960, 270), OverlayAnimationArea.ToHost(area, 1920, 1080));
    }

    [Fact]
    public void Overlay_top_left_range_keeps_the_whole_overlay_inside_the_area()
    {
        // Area 200..600 x 300..450 in an 800x600 viewer; overlay 100x50.
        var range = OverlayAnimationArea.PositionRange(new ChromeRect(.25, .5, .5, .25), 800, 600, 100, 50);
        Assert.Equal(new ChromeRect(200, 300, 300, 100), range);
    }

    [Fact]
    public void Overlay_larger_than_the_area_is_centred_on_it_and_stays_in_the_viewer()
    {
        // 400x150 area, 500x200 overlay: centred on both axes, no movement range.
        var range = OverlayAnimationArea.PositionRange(new ChromeRect(.25, .5, .5, .25), 800, 600, 500, 200);
        Assert.Equal(new ChromeRect(150, 275, 0, 0), range);
        // An area hugging the right edge never pushes the overlay out of the viewer.
        var edge = OverlayAnimationArea.PositionRange(new ChromeRect(.9, 0, .1, .1), 800, 600, 300, 100);
        Assert.True(edge.X + 300 <= 800 && edge.X >= 0);
    }

    [Fact]
    public void Custom_area_round_trips_through_saved_layouts()
    {
        var state = new OverlayState(Guid.NewGuid(), @"C:\i\a.png", 10, 10, 200, 100,
            AnimationConfigured: true, AnimationRegion: "custom AREA",
            AnimationAreaX: .1, AnimationAreaY: .2, AnimationAreaWidth: .3, AnimationAreaHeight: .4);
        var json = JsonSerializer.Serialize(new[] { state });
        var restored = JsonSerializer.Deserialize<OverlayState[]>(json)![0].Normalize();
        Assert.Equal(OverlayAnimationArea.CustomRegionName, restored.AnimationRegion);
        AssertClose(new ChromeRect(.1, .2, .3, .4), restored.AnimationArea);
        Assert.DoesNotContain("\"AnimationArea\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Layouts_saved_before_custom_areas_still_load()
    {
        var restored = JsonSerializer.Deserialize<OverlayState[]>(
            """[{"Id":"11111111-1111-1111-1111-111111111111","Path":"a.png","X":0,"Y":0,"Width":200,"Height":100,"AnimationRegion":"Top half"}]""")![0].Normalize();
        Assert.Null(restored.AnimationArea);
        Assert.Equal("Top half", restored.AnimationRegion);
    }

    [Fact]
    public void Overlay_manager_offers_drawing_and_confines_motion_to_the_area()
    {
        var manager = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs");
        Assert.Contains("Draw custom area…", manager, StringComparison.Ordinal);
        Assert.Contains("OverlayAnimationArea.PositionRange", manager, StringComparison.Ordinal);
        // Every bounds query passes the overlay's own area, so motion, clamping and collisions agree.
        var calls = manager.Split('\n').Where(line => line.Contains("AnimationBounds(", StringComparison.Ordinal) && !line.Contains("private Rect AnimationBounds", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(calls);
        Assert.All(calls, line => Assert.Contains("CustomArea", line, StringComparison.Ordinal));
    }

    private static void AssertClose(ChromeRect expected, ChromeRect? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.X, actual!.Value.X, 9);
        Assert.Equal(expected.Y, actual.Value.Y, 9);
        Assert.Equal(expected.Width, actual.Value.Width, 9);
        Assert.Equal(expected.Height, actual.Value.Height, 9);
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
        throw new FileNotFoundException(Path.Combine(segments));
    }
}
