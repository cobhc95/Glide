using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// 5.0.6 tester item 11: hovering a background tab shows a small preview of its picture (or the first thumbnails of an
/// Explorer tab's folder) in the tab tooltip, made off the UI thread, and the "Show tab previews on hover" setting.
/// The tooltip is the approved hover card (docs/mockups/tab-hover-preview-5.0.6.md): the name, the full folder path and
/// the details line are shown in full (never trimmed), and "Animate GIF previews on hover" plays a GIF's preview.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Tabs")]
public sealed class TabHoverPreviewTests
{
    private static Border TabBorder(ExplorerHarness h, Guid id) =>
        h.Get<StackPanel>("TabStripPanel").Children.OfType<Border>().Single(b => Equals(b.DataContext, id));

    /// <summary>Walks the real pointer onto the tab and waits past the tooltip show delay; returns the open tooltip's content.</summary>
    private static async Task<Control> HoverTabAsync(ExplorerHarness h, Guid id)
    {
        var away = new Point(600, 400);
        h.Window.MouseMove(away);
        for (var i = 0; i < 10; i++) { h.Pump(); await Task.Delay(40); }
        var tab = TabBorder(h, id);
        var target = tab.TranslatePoint(new Point(tab.Bounds.Width / 3, tab.Bounds.Height / 2), h.Window)!.Value;
        for (var i = 1; i <= 8; i++) { h.Window.MouseMove(away + (target - away) * (i / 8.0)); h.Pump(); await Task.Delay(10); }
        await h.WaitUntil(() => ToolTip.GetIsOpen(TabBorder(h, id)), "tab tooltip opened on hover", 4000);
        return (Control)ToolTip.GetTip(TabBorder(h, id))!;
    }

    private static IReadOnlyList<Image> PreviewImages(Control tip) =>
        tip.GetSelfAndLogicalDescendants().OfType<Image>().Where(i => i.Source is Bitmap).ToList();

    private static async Task<Guid> OpenInBackgroundAsync(ExplorerHarness h, string name, int expectedTabs)
    {
        h.Click(name, MouseButton.Middle);
        await h.WaitUntil(() => h.Workspace.Tabs.Count == expectedTabs, "background tab opened");
        h.Pump();
        return h.Workspace.Tabs[h.Workspace.ActiveIndex + 1].Id;
    }

    [Fact]
    public async Task Hovering_a_background_image_tab_shows_a_small_thumbnail_of_its_picture()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // these tests hover background tabs (the default is foreground since 5.0.6)
            var tab = await OpenInBackgroundAsync(h, "b.png", 3);
            var tip = await HoverTabAsync(h, tab);
            await h.WaitUntil(() => PreviewImages(tip).Count == 1, "picture preview in the tab tooltip");
            var bitmap = (Bitmap)PreviewImages(tip)[0].Source!;
            Assert.InRange(bitmap.PixelSize.Width, 1, Glide.App.Services.TabPreviewCache.PictureWidth);
            // The card: no tooltip padding (the picture runs edge to edge), the bare name, the full folder path and the
            // pixel and file size, all untrimmed.
            Assert.Equal(new Thickness(0), ((ToolTip)tip).Padding);
            var texts = TextBlocks(tip);
            Assert.Contains(texts, t => t.Text == "b.png" && t.TextWrapping == TextWrapping.Wrap);
            Assert.Contains(texts, t => t.Text == h.Root && t.TextWrapping == TextWrapping.Wrap);
            Assert.DoesNotContain(texts, t => t.Text?.StartsWith("Name:", StringComparison.Ordinal) == true || t.Text?.StartsWith("Path:", StringComparison.Ordinal) == true);
            Assert.All(texts, t => Assert.Equal(TextTrimming.None, t.TextTrimming));
            await h.WaitUntil(() => TextBlocks(tip).Any(t => t.IsVisible && t.Text is { } s && s.Contains('×') && s.Contains(" · ")), "pixel and file size line");
            // 5.1.0: the mouse hint is one line of key chips ([Drag] move · [Middle] close · [Right] more · [Ctrl+Wheel] size).
            var hint = ((Glide.App.Controls.TabHoverCard)((ToolTip)tip).Content!).Hint;
            Assert.Equal(TextWrapping.NoWrap, hint.TextWrapping);
            Assert.Equal(["Drag", "Middle", "Right", "Ctrl+Wheel"],
                hint.Inlines!.OfType<Avalonia.Controls.Documents.InlineUIContainer>().Select(c => ((TextBlock)((Border)c.Child).Child!).Text));
        });
    }

    [Fact]
    public async Task Hovering_a_background_Explorer_tab_shows_the_first_thumbnails_of_its_folder()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // these tests hover background tabs (the default is foreground since 5.0.6)
            var tab = await OpenInBackgroundAsync(h, "Alpha", 3);
            var tip = await HoverTabAsync(h, tab);
            await h.WaitUntil(() => PreviewImages(tip).Count >= 1, "folder thumbnails in the Explorer tab tooltip");
            Assert.Contains(TextBlocks(tip), t => t.Text == "Alpha");
            Assert.Contains(TextBlocks(tip), t => t.Text == Path.Combine(h.Root, "Alpha")); // the full folder path
            await h.WaitUntil(() => TextBlocks(tip).Any(t => t.IsVisible && t.Text is { } s && s.EndsWith(" items", StringComparison.Ordinal) || t.Text == "1 item"), "item count line");
        });
    }

    [Fact]
    public async Task The_preview_is_made_off_the_UI_thread_once_and_then_served_from_the_cache()
    {
        var calls = new List<bool>();
        Glide.App.Services.TabPreviewCache.ProducerOverrideForTests = (path, width, height) =>
        {
            lock (calls) calls.Add(Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        };
        try
        {
            await ExplorerHarness.Run(async h =>
            {
                h.Settings.SwitchToNewTabs = false; // these tests hover background tabs (the default is foreground since 5.0.6)
                var tab = await OpenInBackgroundAsync(h, "c.png", 3);
                var tip = await HoverTabAsync(h, tab);
                await h.WaitUntil(() => PreviewImages(tip).Count == 1, "picture preview");
                tip = await HoverTabAsync(h, tab); // hover again
                Assert.Single(PreviewImages(tip));
            });
        }
        finally { Glide.App.Services.TabPreviewCache.ProducerOverrideForTests = null; }
        lock (calls)
        {
            Assert.Single(calls);           // the second hover came from the cache
            Assert.False(calls[0], "the preview was produced on the UI thread");
        }
    }

    [Fact]
    public async Task Hovering_the_active_tab_shows_no_preview()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // these tests hover background tabs (the default is foreground since 5.0.6)
            await OpenInBackgroundAsync(h, "b.png", 3);
            var tip = await HoverTabAsync(h, h.Workspace.Active!.Id);
            for (var i = 0; i < 10; i++) { h.Pump(); await Task.Delay(30); }
            Assert.Empty(PreviewImages(tip));
        });
    }

    [Fact]
    public async Task Turning_tab_previews_off_shows_only_the_name_and_path()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // these tests hover background tabs (the default is foreground since 5.0.6)
            h.Settings.TabHoverPreviews = false;
            var tab = await OpenInBackgroundAsync(h, "b.png", 3);
            var tip = await HoverTabAsync(h, tab);
            for (var i = 0; i < 10; i++) { h.Pump(); await Task.Delay(30); }
            Assert.Empty(PreviewImages(tip));
            Assert.Contains(TextBlocks(tip), t => t.Text == "b.png");
            Assert.False(((Glide.App.Controls.TabHoverCard)((ToolTip)tip).Content!).PictureHost.IsVisible); // no empty picture area
        });
    }

    [Fact]
    public async Task A_long_folder_path_is_never_shortened_it_wraps_and_the_card_grows()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // these tests hover background tabs (the default is foreground since 5.0.6)
            var deep = Path.Combine(h.Root, string.Join(Path.DirectorySeparatorChar, Enumerable.Range(0, 6).Select(i => $"a fairly long folder name number {i}")));
            Directory.CreateDirectory(deep);
            var file = Path.Combine(deep, "a picture with a rather long file name that will not fit on one line.png");
            File.Copy(Path.Combine(h.Root, "b.png"), file);
            await (Task)h.Call("OpenImageInNewTabAsync", file, KeyModifiers.None, "test")!;
            h.Pump();
            var id = h.Workspace.Tabs.OfType<ImageTabState>().Single(t => t.Path == file).Id;
            var tip = await HoverTabAsync(h, id);
            await h.WaitUntil(() => PreviewImages(tip).Count == 1, "picture preview");
            for (var i = 0; i < 5; i++) { h.Pump(); await Task.Delay(30); }
            var path = TextBlocks(tip).Single(t => t.Text == deep);
            var name = TextBlocks(tip).Single(t => t.Text == Path.GetFileName(file));
            Assert.True(path.Bounds.Height > path.FontSize * 2, $"the path did not wrap (height {path.Bounds.Height})");
            Assert.True(name.Bounds.Height > name.FontSize * 1.9, $"the name did not wrap (height {name.Bounds.Height})");
            var card = (Glide.App.Controls.TabHoverCard)((ToolTip)tip).Content!;
            Assert.True(card.Bounds.Height >= card.Text.Bounds.Bottom - 0.5, "the card is shorter than its text");
        });
    }

    /// <summary>The share of the picture the preview slot actually shows (1 = the whole picture, smaller = cropped).</summary>
    private static double VisibleFraction(Image image, Border host)
    {
        var source = ((Bitmap)image.Source!).Size;
        double w = host.Bounds.Width, hh = host.Bounds.Height;
        if (image.Stretch != Stretch.UniformToFill) return 1; // Uniform / Fill never cut anything off
        var scale = Math.Max(w / source.Width, hh / source.Height);
        return Math.Min(1, w / (source.Width * scale)) * Math.Min(1, hh / (source.Height * scale));
    }

    [Theory]
    [InlineData(40, 200)]   // tall portrait (aspect 0.2)
    [InlineData(120, 200)]  // mild portrait (aspect 0.6)
    [InlineData(400, 100)]  // landscape (aspect 4)
    public async Task Round3_4_the_tab_preview_shows_the_whole_picture_never_a_cropped_part(int width, int height)
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false;
            var file = Path.Combine(h.Root, $"shape{width}x{height}.png");
            File.WriteAllBytes(file, ExplorerHarness.Png(width, height, 200, 40, 40));
            await (Task)h.Call("OpenImageInNewTabAsync", file, KeyModifiers.None, "test")!;
            h.Pump();
            var id = h.Workspace.Tabs.OfType<ImageTabState>().Single(t => t.Path == file).Id;
            var tip = await HoverTabAsync(h, id);
            await h.WaitUntil(() => PreviewImages(tip).Count == 1, "picture preview");
            for (var i = 0; i < 5; i++) { h.Pump(); await Task.Delay(30); }
            var card = (Glide.App.Controls.TabHoverCard)((ToolTip)tip).Content!;
            var fraction = VisibleFraction(PreviewImages(tip)[0], card.PictureHost);
            Assert.True(fraction > 0.98, $"only {fraction:P0} of a {width}x{height} picture is shown in the preview");
        });
    }

    [Fact]
    public async Task Round3_5_the_resize_key_and_wheel_over_the_tab_resize_the_open_preview_and_save_the_size()
    {
        await ExplorerHarness.Run(async h =>
        {
            // As at application start-up. Without it this test passed alone but failed after any test that had registered
            // the guard: its wheel handler closed the tab's tooltip before the resize handler saw it open.
            Glide.App.Services.ToolTipGuard.Register();
            h.Settings.SwitchToNewTabs = false;
            Assert.Equal(100, h.Settings.TabPreviewSizePercent);
            Assert.Equal("Ctrl", h.Settings.TabPreviewResizeModifier);
            var id = await OpenInBackgroundAsync(h, "b.png", 3);
            var tip = await HoverTabAsync(h, id);
            await h.WaitUntil(() => PreviewImages(tip).Count == 1, "picture preview");
            var card = (Glide.App.Controls.TabHoverCard)((ToolTip)tip).Content!;
            for (var i = 0; i < 5; i++) { h.Pump(); await Task.Delay(30); }
            var before = card.PictureHost.Bounds.Width;
            var tab = TabBorder(h, id);
            var over = tab.TranslatePoint(new Point(tab.Bounds.Width / 3, tab.Bounds.Height / 2), h.Window)!.Value;

            // Four notches: the picture then outgrows the hint line of key chips (5.1.0), which sets the card's minimum width.
            for (var notch = 0; notch < 4; notch++) h.Window.MouseWheel(over, new Vector(0, 1), RawInputModifiers.Control);
            for (var i = 0; i < 5; i++) { h.Pump(); await Task.Delay(30); }
            Assert.Equal(140, h.Settings.TabPreviewSizePercent);
            Assert.Equal(1.4, card.Scale, 3);
            Assert.True(card.PictureHost.Bounds.Width > before + 1, $"the preview did not grow ({before} -> {card.PictureHost.Bounds.Width})");

            h.Settings.TabPreviewResizeModifier = "Alt";
            h.Window.MouseWheel(over, new Vector(0, -1), RawInputModifiers.Alt);
            h.Pump();
            Assert.Equal(130, h.Settings.TabPreviewSizePercent);

            Assert.True(ToolTip.GetIsOpen(tab), "the resize ticks kept the preview open");
            h.Window.MouseWheel(over, new Vector(0, 1), RawInputModifiers.Shift); // not the chosen key: nothing changes
            h.Pump();
            Assert.Equal(130, h.Settings.TabPreviewSizePercent);
            Assert.False(ToolTip.GetIsOpen(tab), "a plain wheel tick still closes the tooltip");

            h.Settings.TabPreviewResizeModifier = "None"; // off
            h.Window.MouseWheel(over, new Vector(0, -1), RawInputModifiers.Alt);
            h.Pump();
            Assert.Equal(130, h.Settings.TabPreviewSizePercent);
        });
    }

    [Fact]
    public void Round3_5_preview_size_is_clamped_and_steps_by_ten()
    {
        Assert.Equal(250, Glide.Core.Workspace.TabPreviewSize.AfterWheel(250, 1));
        Assert.Equal(50, Glide.Core.Workspace.TabPreviewSize.AfterWheel(50, -1));
        Assert.Equal(110, Glide.Core.Workspace.TabPreviewSize.AfterWheel(100, 0.3));
        Assert.Equal(100, Glide.Core.Workspace.TabPreviewSize.Clamp(0));
        Assert.Equal("Ctrl", Glide.Core.Workspace.TabPreviewSize.NormalizeModifier("bogus"));
        Assert.False(Glide.Core.Workspace.TabPreviewSize.ModifierMatches("Ctrl", true, true, false));
        Assert.True(Glide.Core.Workspace.TabPreviewSize.ModifierMatches("shift", false, true, false));
    }

    // ---- "Animate GIF previews on hover" ---------------------------------------------------------------------------

    private static async Task<(Control Tip, Guid Id)> HoverGifTabAsync(ExplorerHarness h)
    {
        var gif = Path.Combine(h.Root, "anim.gif");
        File.WriteAllBytes(gif, AnimatedGif(4));
        await (Task)h.Call("OpenImageInNewTabAsync", gif, KeyModifiers.None, "test")!;
        h.Pump();
        var id = h.Workspace.Tabs.OfType<ImageTabState>().Single(t => t.Path == gif).Id;
        var tip = await HoverTabAsync(h, id);
        await h.WaitUntil(() => PreviewImages(tip).Count == 1, "GIF preview");
        return (tip, id);
    }

    private static async Task<HashSet<IImage>> WatchSourcesAsync(ExplorerHarness h, Control tip, int milliseconds)
    {
        var seen = new HashSet<IImage>(ReferenceEqualityComparer.Instance);
        var image = tip.GetSelfAndLogicalDescendants().OfType<Image>().Single();
        for (var waited = 0; waited < milliseconds; waited += 25)
        {
            h.Pump();
            if (image.Source is { } source) seen.Add(source);
            await Task.Delay(25);
        }
        return seen;
    }

    [Fact]
    public async Task Gif_previews_stay_still_by_default()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // these tests hover background tabs (the default is foreground since 5.0.6)
            Assert.False(h.Settings.AnimateGifPreviews);
            var (tip, _) = await HoverGifTabAsync(h);
            var seen = await WatchSourcesAsync(h, tip, 900);
            Assert.Single(seen);
            Assert.False(h.Window.TabGifPreviewPlayingForTests);
        });
    }

    [Fact]
    public async Task With_animate_on_the_GIF_preview_plays_and_stops_when_the_tooltip_closes()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // these tests hover background tabs (the default is foreground since 5.0.6)
            h.Settings.AnimateGifPreviews = true;
            var (tip, id) = await HoverGifTabAsync(h);
            await h.WaitUntil(() => h.Window.TabGifPreviewPlayingForTests, "GIF preview playing");
            var seen = await WatchSourcesAsync(h, tip, 900);
            Assert.True(seen.OfType<WriteableBitmap>().Count() >= 2, $"the preview showed {seen.Count} frame(s)");

            ToolTip.SetIsOpen(TabBorder(h, id), false);
            await h.WaitUntil(() => !h.Window.TabGifPreviewPlayingForTests, "GIF preview stopped on close");
            Assert.IsNotType<WriteableBitmap>(tip.GetSelfAndLogicalDescendants().OfType<Image>().Single().Source); // back to the still
        });
    }

    [Fact]
    public async Task A_GIF_over_the_preview_frame_cap_keeps_the_still_first_frame()
    {
        Glide.App.Services.TabGifPreview.MaxFramesBytesOverrideForTests = 1;
        try
        {
            await ExplorerHarness.Run(async h =>
            {
                h.Settings.SwitchToNewTabs = false; // these tests hover background tabs (the default is foreground since 5.0.6)
                h.Settings.AnimateGifPreviews = true;
                var (tip, _) = await HoverGifTabAsync(h);
                var seen = await WatchSourcesAsync(h, tip, 900);
                Assert.Single(seen);
                Assert.Empty(seen.OfType<WriteableBitmap>());
                Assert.False(h.Window.TabGifPreviewPlayingForTests);
            });
        }
        finally { Glide.App.Services.TabGifPreview.MaxFramesBytesOverrideForTests = null; }
    }

    private static IReadOnlyList<TextBlock> TextBlocks(Control tip) => tip.GetSelfAndLogicalDescendants().OfType<TextBlock>().ToList();

    /// <summary>A 1x1 GIF with <paramref name="frames"/> frames of 100 ms.</summary>
    private static byte[] AnimatedGif(int frames)
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange(new byte[] { 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF });
        for (var i = 0; i < frames; i++)
        {
            gif.AddRange(new byte[] { 0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00 });
            gif.AddRange(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0x02, 0x02, 0x44, 0x01, 0x00 });
        }
        gif.Add(0x3B);
        return gif.ToArray();
    }
}
