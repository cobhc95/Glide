using System.Globalization;
using Glide.Core.Settings;
using Glide.Core.Status;

namespace Glide.Core.Tests;

/// <summary>Round 4 B5-B7, C1: the shared {token} template engine, separators, date formats and per-position looks.</summary>
[Trait("Area", "Overlay")]
public sealed class TextTemplateTests
{
    private static readonly TemplateValues Photo = new(@"C:\Pics\Trip\photo.jpg", Index: 4, Count: 50, PixelWidth: 4000, PixelHeight: 3000,
        FileSize: 2_516_582, ZoomPercent: 50, Modified: new DateTime(2024, 3, 9, 14, 5, 0), Taken: new DateTime(2023, 12, 25, 8, 30, 0));

    private static string R(string template, string? sep = TextTemplate.DefaultSeparator)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return TextTemplate.Render(template, Photo, sep); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("{ext}", "jpg")]
    [InlineData("{extension}", "jpg")]
    [InlineData("{w}x{h}", "4000x3000")]
    [InlineData("{width}x{height}", "4000x3000")]
    [InlineData("{res}", "4000\u00d73000")]
    [InlineData("{dimensions}", "4000\u00d73000")]
    [InlineData("{MP}", "12")]
    [InlineData("{megapixels}", "12")]
    [InlineData("{filesizeb}", "2516582")]
    [InlineData("{filesizebytes}", "2516582")]
    [InlineData("{file}|{filename}", "photo.jpg|photo.jpg")]
    [InlineData("{folder} {folderpath}", @"Trip C:\Pics\Trip")]
    [InlineData("[{index}/{total}] {zoom}", "[5/50] 50%")]
    [InlineData("{unknown} stays", "{unknown} stays")]
    public void Short_tokens_and_old_aliases_render_the_same(string template, string expected) => Assert.Equal(expected, R(template));

    [Fact]
    public void Help_lists_only_the_short_tokens()
    {
        var shown = TextTemplate.Tokens.Select(t => t.Token).ToList();
        foreach (var token in new[] { "{ext}", "{MP}", "{filesizeb}", "{w}", "{h}", "{folderpath}", "{modified}", "{taken}", "{sep}" })
            Assert.Contains(token, shown);
        foreach (var old in new[] { "{extension}", "{megapixels}", "{filesizebytes}", "{width}", "{height}", "{filename}", "{dimensions}" })
            Assert.DoesNotContain(old, shown);
    }

    [Theory]
    [InlineData("{modified}", "2024-03-09 14:05")]
    [InlineData("{modified:yyyy/MM/dd HH:mm}", "2024/03/09 14:05")]
    [InlineData("{modified:YYYY/MM/DD}", "2024/03/09")]
    [InlineData("{taken:ddd}", "Mon")]
    [InlineData("{taken:DDD}", "Mon")]
    [InlineData("{taken:h:mm tt}", "8:30 AM")]
    [InlineData("{taken:d}", "25")]
    [InlineData("{datetaken:yyyy}", "2023")]
    public void Dates_take_dotnet_or_excel_style_formats(string template, string expected) => Assert.Equal(expected, R(template));

    [Fact]
    public void Missing_dates_are_empty()
    {
        Assert.Equal("[]", TextTemplate.Render("[{taken}]", Photo with { Taken = null }));
    }

    [Theory]
    [InlineData("\u2022", "a  \u2022  b")]
    [InlineData("|", "a  |  b")]
    [InlineData("None", "a  b")]
    public void Sep_is_the_chosen_separator(string sep, string expected) => Assert.Equal(expected, R("a{sep}b", sep));

    [Fact]
    public void Separators_are_their_own_segments_for_the_separator_colour()
    {
        var segments = TextTemplate.Segments("{stem}{sep}{ext}", Photo);
        Assert.Equal(new[] { false, true, false }, segments.Select(s => s.IsSeparator));
        Assert.Equal(TextTemplate.Separators, SettingActionCatalog.Choices["overlay.pictureSeparator"]);
        Assert.Equal(TextOverlayStyle.ColorChoices, SettingActionCatalog.Choices["overlay.titleBarColor"]);
        Assert.Equal(TextOverlayStyle.BorderChoices, SettingActionCatalog.Choices["overlay.tabBarBorder"]);
        Assert.Equal(TextOverlayStyle.SeparatorColorChoices, SettingActionCatalog.Choices["overlay.separatorColor"]);
    }

    [Fact]
    public void Each_position_has_its_own_look_or_the_picture_one()
    {
        var s = new GlideSettingsState { PictureCounterTemplate = "{file}", PictureCounterFontSize = 20, PictureCounterColor = "Amber", PictureCounterBorder = true };
        var picture = TextOverlayStyle.For(s, "Top right");
        Assert.Equal(new TextOverlayStyle("{file}", 20, "Amber", true, false), picture);
        // Defaults: the bars look like the picture; the title bar keeps to one line.
        Assert.Equal(picture with { SingleLine = true }, TextOverlayStyle.For(s, TextOverlayPlacements.TitleBarLeft));
        Assert.Equal(picture, TextOverlayStyle.For(s, TextOverlayPlacements.TabBarTop));
        s.TextOverlayTitleBarTemplate = "{stem}";
        s.TextOverlayTitleBarFontSize = 11;
        s.TextOverlayTitleBarColor = "Accent";
        s.TextOverlayTitleBarBorder = "Off";
        s.TextOverlayTabBarBorder = "Off";
        Assert.Equal(new TextOverlayStyle("{stem}", 11, "Accent", false, true), TextOverlayStyle.For(s, TextOverlayPlacements.TitleBarRight));
        Assert.Equal(picture with { Border = false }, TextOverlayStyle.For(s, TextOverlayPlacements.TabBarBottom));
        Assert.Equal(picture, TextOverlayStyle.For(s, TextOverlayPlacements.Custom));
    }

    [Fact]
    public void Counter_text_uses_the_engine()
    {
        var model = new ViewerStatusModel(@"C:\a\b.png", 0, 3, 10, 20, 100, 100);
        Assert.Equal("b  \u2022  10\u00d720", ViewerStatusFormatter.CounterText(model, "{stem}{sep}{res}"));
        Assert.Equal("[1/3]", ViewerStatusFormatter.CounterText(model, ""));
    }
    /// <summary>5.1.7 UI test bug 11: an empty part (a folder has no size or pixels) takes its separator and literal text with it.</summary>
    [Theory]
    [InlineData("{res}{sep}{filesize}", "")]
    [InlineData("{w}x{h}{sep}{ext}", "")]
    [InlineData("{stem}{sep}{res}{sep}{filesize}", "Gamma")]
    [InlineData("{res}{sep}{stem}", "Gamma")]
    [InlineData("{stem}{sep}{filesize}{sep}{folder}", "Gamma  /  Pics")]
    [InlineData("[{ext}]{sep}{stem}", "Gamma")]
    [InlineData("{sep}{stem}{sep}", "Gamma")]
    public void Empty_parts_and_their_separators_collapse_for_a_folder(string template, string expected)
    {
        var folder = new TemplateValues(@"C:\Pics\Gamma", IsFolder: true, Modified: new DateTime(2024, 1, 1));
        Assert.Equal(expected, TextTemplate.Render(template, folder, "/"));
    }

    [Fact]
    public void A_picture_with_all_values_keeps_every_separator_and_a_missing_zoom_drops_only_its_own()
    {
        Assert.Equal("photo  •  4000×3000  •  2.4 MB", R("{stem}{sep}{res}{sep}{filesize}"));
        var noZoom = new TemplateValues(@"C:\Pics\photo.jpg", PixelWidth: 10, PixelHeight: 20);
        Assert.Equal("photo  •  jpg", TextTemplate.Render("{stem}{sep}{zoom}{sep}{ext}", noZoom));
        // The overlay's segments agree (separators are still marked for their own colour).
        var segments = TextTemplate.Segments("{stem}{sep}{zoom}{sep}{ext}", noZoom);
        Assert.Equal(new[] { false, true, false }, segments.Select(s => s.IsSeparator).ToArray());
    }
}
