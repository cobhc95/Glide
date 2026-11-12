using Glide.Core;
using Xunit;

namespace Glide.Core.Tests;

/// <summary>Navigation order must match Windows Explorer by default, and honour the user's chosen order.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ImageSortOrderCollection
{
    public const string Name = "Image sort order (global setting)";
}

[Collection(ImageSortOrderCollection.Name)]
public sealed class ImageSortOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glide-sort-" + Guid.NewGuid().ToString("N"));
    private readonly ImageSortSpec _original = ImageSortOrder.Current;

    public ImageSortOrderTests()
    {
        Directory.CreateDirectory(_dir);
        ImageSortOrder.Current = ImageSortSpec.Default;
    }

    public void Dispose()
    {
        ImageSortOrder.Current = _original;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Make(string name, int size = 1, DateTime? modified = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[size]);
        if (modified is { } when) File.SetLastWriteTimeUtc(path, when);
        return path;
    }

    private static string[] Names(IEnumerable<string> paths) => paths.Select(Path.GetFileName).ToArray()!;

    [Fact]
    public void Default_order_is_the_natural_name_order_windows_explorer_uses()
    {
        // The real folder from the bug report: letters-after-digits names must follow Explorer, where
        // 3m90 comes before 03325 (numbers compare by value) instead of plain character order.
        var names = new[]
        {
            "3m90fxiwwfo61.png", "5sh7n01d9b311.jpg", "8ptxnpbe1g221.jpg", "03325_valleyofthestars.jpg", "04001_thepierofh.jpg",
            "106481.jpg", "677357.png", "1245403.png", "1347287.jpeg", "aerial-view-green.jpg", "JbXSLOt.jpg"
        };
        foreach (var name in names.Reverse()) Make(name);

        var expected = new[]
        {
            "3m90fxiwwfo61.png", "5sh7n01d9b311.jpg", "8ptxnpbe1g221.jpg", "03325_valleyofthestars.jpg", "04001_thepierofh.jpg",
            "106481.jpg", "677357.png", "1245403.png", "1347287.jpeg", "aerial-view-green.jpg", "JbXSLOt.jpg"
        };
        Assert.Equal(expected, Names(ImageNavigator.EnumerateSupportedFolder(Path.Combine(_dir, names[0]))));

        var navigator = new ImageNavigator();
        navigator.OpenSingle(Path.Combine(_dir, "3m90fxiwwfo61.png"));
        Assert.Equal(expected, Names(navigator.Paths));
        Assert.Equal(0, navigator.Index);
        navigator.Move(1);
        Assert.Equal("5sh7n01d9b311.jpg", Path.GetFileName(navigator.Current));
    }

    [Fact]
    public void Windowed_enumeration_agrees_with_the_full_order()
    {
        for (var i = 1; i <= 40; i++) Make($"img{i}.jpg");
        var requested = Path.Combine(_dir, "img20.jpg");
        var window = Names(ImageNavigator.EnumerateSupportedFolder(requested, maxCount: 9));
        var full = Names(ImageNavigator.EnumerateSupportedFolder(requested));
        Assert.Equal(new[] { "img16.jpg", "img17.jpg", "img18.jpg", "img19.jpg", "img20.jpg", "img21.jpg", "img22.jpg", "img23.jpg", "img24.jpg" }, window);
        Assert.Equal("img2.jpg", full[1]);
        Assert.Equal("img10.jpg", full[9]);
    }

    [Fact]
    public void Plain_name_order_is_available_and_differs_from_natural_order()
    {
        Make("2.jpg");
        Make("10.jpg");
        ImageSortOrder.Current = new ImageSortSpec(ImageSortKey.NameOrdinal, false);
        Assert.Equal(new[] { "10.jpg", "2.jpg" }, Names(ImageNavigator.EnumerateSupportedFolder(Path.Combine(_dir, "2.jpg"))));
        ImageSortOrder.Current = ImageSortSpec.Default;
        Assert.Equal(new[] { "2.jpg", "10.jpg" }, Names(ImageNavigator.EnumerateSupportedFolder(Path.Combine(_dir, "2.jpg"))));
    }

    [Fact]
    public void Size_type_and_date_orders_work_in_both_directions()
    {
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Make("b.png", size: 300, modified: old.AddDays(2));
        Make("a.jpg", size: 100, modified: old.AddDays(3));
        Make("c.gif", size: 200, modified: old.AddDays(1));
        var probe = Path.Combine(_dir, "a.jpg");

        ImageSortOrder.Current = new ImageSortSpec(ImageSortKey.Size, false);
        Assert.Equal(new[] { "a.jpg", "c.gif", "b.png" }, Names(ImageNavigator.EnumerateSupportedFolder(probe)));
        ImageSortOrder.Current = new ImageSortSpec(ImageSortKey.Size, true);
        Assert.Equal(new[] { "b.png", "c.gif", "a.jpg" }, Names(ImageNavigator.EnumerateSupportedFolder(probe)));

        ImageSortOrder.Current = new ImageSortSpec(ImageSortKey.DateModified, false);
        Assert.Equal(new[] { "c.gif", "b.png", "a.jpg" }, Names(ImageNavigator.EnumerateSupportedFolder(probe)));

        ImageSortOrder.Current = new ImageSortSpec(ImageSortKey.Type, false);
        Assert.Equal(new[] { "c.gif", "a.jpg", "b.png" }, Names(ImageNavigator.EnumerateSupportedFolder(probe)));
    }

    [Fact]
    public void Resort_keeps_the_current_image_and_watcher_inserts_follow_the_chosen_order()
    {
        var small = Make("small.jpg", size: 10);
        Make("medium.jpg", size: 20);
        Make("large.jpg", size: 30);

        var navigator = new ImageNavigator();
        navigator.OpenSingle(small);
        Assert.Equal(new[] { "large.jpg", "medium.jpg", "small.jpg" }, Names(navigator.Paths));

        ImageSortOrder.Current = new ImageSortSpec(ImageSortKey.Size, false);
        navigator.Resort();
        Assert.Equal(new[] { "small.jpg", "medium.jpg", "large.jpg" }, Names(navigator.Paths));
        Assert.Equal("small.jpg", Path.GetFileName(navigator.Current));

        var huge = Make("huge.jpg", size: 99);
        Assert.True(navigator.ApplyChanges(new[] { huge }, Array.Empty<string>()));
        Assert.Equal(new[] { "small.jpg", "medium.jpg", "large.jpg", "huge.jpg" }, Names(navigator.Paths));
        Assert.Equal("small.jpg", Path.GetFileName(navigator.Current));
    }

    [Fact]
    public void Settings_display_names_round_trip_and_unknown_values_fall_back_to_the_default()
    {
        foreach (var name in ImageSortOrder.DisplayNames)
            Assert.Equal(name, ImageSortOrder.ToDisplayName(ImageSortOrder.ParseKey(name)));
        Assert.Equal(ImageSortKey.Name, ImageSortOrder.ParseKey("nonsense"));
        Assert.Equal(ImageSortKey.Name, ImageSortOrder.ParseKey(null));
        Assert.Equal(ImageSortOrder.NameDisplay, ImageSortOrder.NormalizeDisplayName(""));
    }
}
