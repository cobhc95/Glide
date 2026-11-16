using Glide.App.Explorer;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>Explorer wheel scrolling by the scroll amount mode, the speed multiplier and the alternative file manager command line.</summary>
public sealed class ExplorerWheelScrollTests
{
    private const double Spacing = 6;

    private const string PerView = ExplorerScrollModes.PerView;

    [Theory]
    [InlineData(ExplorerViewModes.ExtraLarge, 1)]
    [InlineData(ExplorerViewModes.Large, 1)]
    [InlineData(ExplorerViewModes.Medium, 2)]
    [InlineData(ExplorerViewModes.Small, 2)]
    [InlineData(ExplorerViewModes.List, 3)]
    [InlineData(ExplorerViewModes.Details, 3)]
    public void Per_view_mode_scrolls_1_2_3_rows_by_view(int mode, int rows)
    {
        Assert.Equal(rows, ExplorerWheelScroll.RowsPerNotch(mode));
        var pitch = ExplorerWheelScroll.RowPitch(mode, Spacing);
        // Wheel away from the user (Delta.Y = +1, WHEEL_DELTA 120) scrolls up by exactly that many rows.
        Assert.Equal(-rows * pitch, ExplorerWheelScroll.OffsetChange(mode, 1, 1, Spacing, PerView), 6);
        Assert.Equal(rows * pitch, ExplorerWheelScroll.OffsetChange(mode, -1, 1, Spacing, PerView), 6);
    }

    [Theory]
    [InlineData(ExplorerViewModes.ExtraLarge)]
    [InlineData(ExplorerViewModes.Large)]
    [InlineData(ExplorerViewModes.Medium)]
    [InlineData(ExplorerViewModes.Small)]
    [InlineData(ExplorerViewModes.List)]
    [InlineData(ExplorerViewModes.Details)]
    public void Default_mode_scrolls_one_row_of_the_view_per_notch(int mode)
    {
        var pitch = ExplorerWheelScroll.RowPitch(mode, Spacing);
        Assert.Equal(-pitch, ExplorerWheelScroll.OffsetChange(mode, 1, 1, Spacing), 6);
        Assert.Equal(-pitch, ExplorerWheelScroll.OffsetChange(mode, 1, 1, Spacing, ExplorerScrollModes.OneRow), 6);
        Assert.Equal(-pitch, ExplorerWheelScroll.OffsetChange(mode, 1, 1, Spacing, "unknown mode"), 6);
    }

    [Theory]
    [InlineData(ExplorerViewModes.Large, 5, 5)]
    [InlineData(ExplorerViewModes.Details, 3, 3)]
    [InlineData(ExplorerViewModes.List, 0, 1)]
    [InlineData(ExplorerViewModes.Small, 99, 20)]
    public void Fixed_rows_mode_uses_the_clamped_row_count_in_every_view(int mode, int rows, int effective)
    {
        var pitch = ExplorerWheelScroll.RowPitch(mode, Spacing);
        Assert.Equal(-effective * pitch, ExplorerWheelScroll.OffsetChange(mode, 1, 1, Spacing, ExplorerScrollModes.FixedRows, rows), 6);
    }

    [Theory]
    [InlineData(ExplorerViewModes.Large, 100, 100)]
    [InlineData(ExplorerViewModes.Details, 250, 250)]
    [InlineData(ExplorerViewModes.Medium, 1, 10)]
    [InlineData(ExplorerViewModes.List, 5000, 1000)]
    public void Pixels_mode_ignores_row_size_and_is_clamped(int mode, int pixels, int effective)
    {
        Assert.Equal(-effective, ExplorerWheelScroll.OffsetChange(mode, 1, 1, Spacing, ExplorerScrollModes.Pixels, pixels: pixels), 6);
    }

    [Fact]
    public void Row_pitch_is_tile_height_plus_row_spacing_and_details_has_no_spacing()
    {
        Assert.Equal(188 + Spacing, ExplorerWheelScroll.RowPitch(ExplorerViewModes.Large, Spacing));
        Assert.Equal(28, ExplorerWheelScroll.RowPitch(ExplorerViewModes.Details, Spacing));
    }

    [Fact]
    public void A_notch_is_far_less_than_a_page()
    {
        // The old behaviour scrolled 2/3 of the viewport; a 600 px viewport must now move about one Large row.
        Assert.True(Math.Abs(ExplorerWheelScroll.OffsetChange(ExplorerViewModes.Large, 1, 1, Spacing)) < 600 / 2.5);
    }

    public static TheoryData<string, double> ModesAndFractions()
    {
        var data = new TheoryData<string, double>();
        foreach (var m in ExplorerScrollModes.All)
            foreach (var f in new[] { 0.25, 1.0 / 3, 0.1 })
                data.Add(m, f);
        return data;
    }

    [Theory]
    [MemberData(nameof(ModesAndFractions))]
    public void High_resolution_wheels_scroll_proportionally_in_every_mode(string scrollMode, double fraction)
    {
        var full = ExplorerWheelScroll.OffsetChange(ExplorerViewModes.List, -1, 1, Spacing, scrollMode, 4, 150);
        Assert.Equal(full * fraction, ExplorerWheelScroll.OffsetChange(ExplorerViewModes.List, -fraction, 1, Spacing, scrollMode, 4, 150), 6);
    }

    [Theory]
    [InlineData(2.0, 2.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(10.0, 4.0)]
    [InlineData(0.01, 0.25)]
    [InlineData(0.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    public void Speed_multiplies_every_mode_and_is_clamped(double speed, double effective)
    {
        foreach (var m in ExplorerScrollModes.All)
        {
            var baseline = ExplorerWheelScroll.OffsetChange(ExplorerViewModes.Medium, 1, 1, Spacing, m, 4, 150);
            Assert.Equal(baseline * effective, ExplorerWheelScroll.OffsetChange(ExplorerViewModes.Medium, 1, speed, Spacing, m, 4, 150), 6);
        }
    }

    [Fact]
    public void Scroll_settings_are_in_the_catalog_with_their_defaults()
    {
        var s = new Glide.App.Settings.GlideSettingsState();
        Assert.Equal(1.0, s.ExplorerScrollSpeed);
        Assert.Equal(ExplorerScrollModes.OneRow, s.ExplorerScrollMode);
        Assert.Equal(ExplorerScrollModes.Default, s.ExplorerScrollMode);
        Assert.Equal(3, s.ExplorerScrollRows);
        Assert.Equal(100, s.ExplorerScrollPixels);
        var speed = SettingsCatalog.All.Single(d => d.Id == "explorer.scrollSpeed");
        foreach (var id in new[] { "explorer.scrollMode", "explorer.scrollRows", "explorer.scrollPixels" })
            Assert.Equal(speed.Category, SettingsCatalog.All.Single(d => d.Id == id).Category);
        Assert.NotNull(Glide.App.Settings.SettingAccessors.Find("explorer.scrollMode"));
        Assert.Contains(SettingsCatalog.All, d => d.Id == "explorer.altFileManagerPath");
    }

    [Theory]
    [InlineData("\"{path}\"", @"C:\a b\x.jpg", "\"C:\\a b\\x.jpg\"")]
    [InlineData("/O {PATH}", @"C:\x", "/O C:\\x")]
    [InlineData("", @"C:\x", "\"C:\\x\"")]
    [InlineData("-new", @"C:\x", "-new \"C:\\x\"")]
    public void Alternative_file_manager_arguments_fill_the_path_placeholder(string template, string path, string expected) =>
        Assert.Equal(expected, ExplorerShell.AlternativeArguments(template, path));

    [Fact]
    public void Alternative_file_manager_is_not_started_when_none_is_set()
    {
        var calls = 0;
        var previous = ExplorerHost.LaunchAlternativeFileManager;
        ExplorerHost.LaunchAlternativeFileManager = (_, _) => { calls++; return true; };
        try
        {
            Assert.False(ExplorerShell.OpenInAlternative("", "{path}", @"C:\x"));
            Assert.True(ExplorerShell.OpenInAlternative("\"C:\\fm.exe\"", "{path}", @"C:\x"));
            Assert.Equal(1, calls);
        }
        finally { ExplorerHost.LaunchAlternativeFileManager = previous; }
    }
}
