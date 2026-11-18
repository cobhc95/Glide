using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Settings;
using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>
/// Renders the gesture action dropdowns with the real app styles (Fluent + GlideTheme, dark) on the
/// headless Skia backend and samples pixels, so the "field" look (fill, border, radius, chevron
/// pointing down, accent states) can never silently stop rendering again. Set GLIDE_SAVE_RENDERS to a
/// folder to also write the frames as PNG files for a visual check.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Gestures")]
public sealed class GestureDropdownRenderTests
{
    public GestureDropdownRenderTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private const double Scale = 2;
    private static readonly Color Accent = Color.Parse("#38A9F5");

    private static readonly string? SaveDirectory = Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");

    private static void UseAppStyles(Window window)
    {
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Styles.Insert(0, new FluentTheme());
        window.Styles.Insert(1, new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
    }

    private sealed class Frame
    {
        private readonly byte[] _pixels;
        public Frame(Bitmap bitmap)
        {
            Bitmap = bitmap;
            Width = bitmap.PixelSize.Width;
            Height = bitmap.PixelSize.Height;
            _pixels = new byte[Width * Height * 4];
            var handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
            try { bitmap.CopyPixels(new PixelRect(0, 0, Width, Height), handle.AddrOfPinnedObject(), _pixels.Length, Width * 4); }
            finally { handle.Free(); }
        }
        public Bitmap Bitmap { get; }
        public int Width { get; }
        public int Height { get; }
        public Color this[int x, int y]
        {
            get
            {
                x = Math.Clamp(x, 0, Width - 1);
                y = Math.Clamp(y, 0, Height - 1);
                var i = (y * Width + x) * 4;
                return Color.FromArgb(_pixels[i + 3], _pixels[i + 2], _pixels[i + 1], _pixels[i]);
            }
        }

        public void SaveCrop(PixelRect rect, string name)
        {
            if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
            Directory.CreateDirectory(SaveDirectory);
            rect = rect.Intersect(new PixelRect(0, 0, Width, Height));
            if (rect.Width < 1 || rect.Height < 1) return;
            using var crop = new WriteableBitmap(rect.Size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = crop.Lock())
                Bitmap.CopyPixels(rect, fb.Address, fb.RowBytes * rect.Height, fb.RowBytes);
            crop.Save(Path.Combine(SaveDirectory, name));
        }
    }

    private static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    private static int Luma(Color c) => c.R + c.G + c.B;
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Frame Capture(Window window)
    {
        // Lay out, then draw the window's visual tree (styles, templates, transforms) with Skia.
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
        var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(window);
        return new Frame(bitmap);
    }

    private static PixelRect PixelBounds(Visual visual, Visual root)
    {
        var m = visual.TransformToVisual(root) ?? throw new InvalidOperationException("Not in the same tree.");
        var r = new Rect(visual.Bounds.Size).TransformToAABB(m);
        return new PixelRect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
    }

    private static PixelRect Inflate(PixelRect r, int by) => new(r.X - by, r.Y - by, r.Width + by * 2, r.Height + by * 2);

    /// <summary>Pixel facts about one rendered dropdown (s = device pixels per DIP).</summary>
    private sealed record Field(Color Outside, Color Fill, Color Border, Color Corner, Color DotSpot, int LeadingAccentPixels, bool ChevronDown, bool ChevronUp, double Height, string Chevron);

    private static Field Measure(Frame frame, PixelRect r, double s)
    {
        var cy = r.Y + r.Height / 2;
        var outside = frame[r.X - (int)(3 * s), cy];
        var fill = frame[r.X + (int)(4 * s), cy];
        var border = frame[r.X + r.Width / 2, r.Y + (int)Math.Max(0, s / 2 - 0.5)];
        var corner = frame[r.X + (int)Math.Max(0, s / 2 - 0.5), r.Y + (int)Math.Max(0, s / 2 - 0.5)];
        var dotSpot = frame[r.X + (int)(14 * s), cy];
        // Accent-blue pixels where a changed row draws its leading 6 DIP dot (default rows: none).
        var leadingAccent = 0;
        for (var x = r.X + (int)(9 * s); x < r.X + (int)(19 * s); x++)
            for (var y = cy - (int)(4 * s); y <= cy + (int)(4 * s); y++)
                if (Distance(frame[x, y], Accent) < 70) leadingAccent++;

        // Chevron: the 15 DIP glyph sits right-aligned inside the 10 DIP padding. Collect its "ink" in a
        // generous box at the right end, then compare the mean y of the ink in the middle third of its
        // width (the apex) with the outer thirds (the arm ends): a down chevron has its apex lowest.
        var box = new PixelRect(r.Right - (int)(30 * s), cy - (int)(9 * s), (int)(26 * s), (int)(18 * s));
        var ink = new List<(int X, int Y)>();
        for (var x = box.X; x < box.Right; x++)
            for (var y = box.Y; y < box.Bottom; y++)
                if (Distance(frame[x, y], fill) > 90) ink.Add((x, y));
        bool down = false, up = false;
        var info = "no chevron ink found";
        if (ink.Count > 0)
        {
            double minX = ink.Min(p => p.X), maxX = ink.Max(p => p.X), third = (maxX - minX) / 3;
            var middle = ink.Where(p => p.X >= minX + third && p.X <= maxX - third).Select(p => (double)p.Y).DefaultIfEmpty(double.NaN).Average();
            var outer = ink.Where(p => p.X < minX + third || p.X > maxX - third).Select(p => (double)p.Y).DefaultIfEmpty(double.NaN).Average();
            down = middle > outer + s;
            up = middle < outer - s;
            info = $"apex y {middle:0.0}, arm-ends y {outer:0.0}, ink x {minX}..{maxX}";
        }
        return new Field(outside, fill, border, corner, dotSpot, leadingAccent, down, up, r.Height / s, info);
    }

    private static void AssertFieldLook(Field f, string what)
    {
        Assert.True(Math.Abs(f.Height - 30) < 1.01, $"{what}: field height {f.Height} DIP, expected 30.");
        Assert.True(Distance(f.Fill, f.Outside) >= 10, $"{what}: field fill {Hex(f.Fill)} is not distinguishable from the panel {Hex(f.Outside)}.");
        Assert.True(Luma(f.Border) - Luma(f.Fill) >= 30, $"{what}: border {Hex(f.Border)} is not visible against the fill {Hex(f.Fill)}.");
        Assert.True(Distance(f.Corner, f.Border) > Distance(f.Corner, f.Outside), $"{what}: corner {Hex(f.Corner)} is not rounded (border {Hex(f.Border)}, panel {Hex(f.Outside)}).");
    }

    private static GestureActionPicker NewPicker(string? actionId = null)
    {
        var slot = GestureCatalog.Slots[0].Id;
        var picker = new GestureActionPicker(slot, "Default (nothing)", () => GestureCatalog.ActionsForSlot(slot), () => GestureCatalog.SuggestedForSlot(slot), () => []);
        if (actionId is not null) picker.SetAction(actionId);
        return picker;
    }

    [Fact]
    public async Task Gesture_dropdowns_render_as_fields_in_every_state()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var normal = NewPicker();
            var changed = NewPicker(GestureCatalog.Fullscreen);
            var hover = NewPicker();
            ((IPseudoClasses)hover.Classes).Set(":pointerover", true);
            var focus = NewPicker();
            ((IPseudoClasses)focus.Classes).Set(":focus", true);
            ((IPseudoClasses)focus.Classes).Set(":focus-visible", true);
            var open = NewPicker();
            var disabled = NewPicker();
            disabled.IsEnabled = false;
            var states = new (string Name, GestureActionPicker Picker)[] { ("normal", normal), ("changed", changed), ("hover", hover), ("focus", focus), ("open", open), ("disabled", disabled) };

            var list = new StackPanel { Spacing = 12, Width = 300 };
            foreach (var (name, picker) in states)
            {
                list.Children.Add(new TextBlock { Text = name, FontSize = 11, Margin = new Thickness(0, 0, 0, -8) });
                list.Children.Add(picker);
            }
            var surface = new Border { Padding = new Thickness(16), Child = list };
            var window = new Window { Width = 340 * Scale, Height = 470 * Scale, Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(Scale, Scale), Child = surface, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top } };
            UseAppStyles(window);
            surface.Bind(Border.BackgroundProperty, window.GetResourceObservable("BrushSurface"));
            window.Show();
            try
            {
                var frame = Capture(window);
                var m = new Dictionary<string, Field>();
                void Take(string name, GestureActionPicker picker)
                {
                    var r = PixelBounds(picker, window);
                    frame.SaveCrop(Inflate(r, (int)(8 * Scale)), $"gesture-dropdown-{name}.png");
                    m[name] = Measure(frame, r, Scale);
                }
                frame.SaveCrop(PixelBounds(surface, window), "gesture-dropdown-states.png");
                foreach (var (name, picker) in states.Where(s => s.Name != "open")) Take(name, picker);

                // Open the real popup (the picker's own Click path), then capture the open field.
                Assert.DoesNotContain("open", open.Classes);
                typeof(GestureActionPicker).GetMethod("OpenPopup", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(open, null);
                Assert.Contains("open", open.Classes);
                frame = Capture(window);
                frame.SaveCrop(PixelBounds(surface, window), "gesture-dropdown-states-with-popup.png");
                Take("open", open);

                AssertFieldLook(m["normal"], "normal");
                AssertFieldLook(m["changed"], "changed");
                AssertFieldLook(m["hover"], "hover");
                AssertFieldLook(m["focus"], "focus");
                AssertFieldLook(m["open"], "open");

                // Chevron points down while closed, up while the popup is open.
                Assert.True(m["normal"].ChevronDown, "normal: chevron does not point down: " + m["normal"].Chevron);
                Assert.True(m["changed"].ChevronDown, "changed: chevron does not point down.");
                Assert.True(m["hover"].ChevronDown, "hover: chevron does not point down.");
                Assert.True(m["open"].ChevronUp, "open: chevron does not point up.");

                // Dot only on the changed row; changed row has an accent tint and accent border.
                Assert.True(m["changed"].DotSpot.B > 200 && m["changed"].DotSpot.B > m["changed"].DotSpot.R + 80, $"changed: no accent dot ({Hex(m["changed"].DotSpot)}).");
                Assert.True(m["changed"].LeadingAccentPixels >= 10, $"changed: dot too small ({m["changed"].LeadingAccentPixels} accent pixels).");
                foreach (var name in new[] { "normal", "hover", "focus", "open", "disabled" })
                    Assert.True(m[name].LeadingAccentPixels == 0, $"{name}: an accent dot is drawn on a default row ({m[name].LeadingAccentPixels} pixels).");
                Assert.False(normal.ShowsChangedDot);
                Assert.True(changed.ShowsChangedDot);
                Assert.True(m["changed"].Fill.B > m["changed"].Fill.R + 8 && Distance(m["changed"].Fill, m["normal"].Fill) >= 10, $"changed: no accent tint ({Hex(m["changed"].Fill)} vs {Hex(m["normal"].Fill)}).");
                foreach (var name in new[] { "changed", "open", "focus" })
                    Assert.True(m[name].Border.B > 200 && m[name].Border.B > m[name].Border.R + 100, $"{name}: border {Hex(m[name].Border)} is not the accent.");
                Assert.True(m["normal"].Border.B < m["normal"].Border.R + 40, $"normal: border {Hex(m["normal"].Border)} should not be the accent.");

                // Hover is lighter (fill and border) than normal.
                Assert.True(Luma(m["hover"].Fill) > Luma(m["normal"].Fill) + 6, $"hover fill {Hex(m["hover"].Fill)} is not lighter than {Hex(m["normal"].Fill)}.");
                Assert.True(Luma(m["hover"].Border) > Luma(m["normal"].Border) + 30, $"hover border {Hex(m["hover"].Border)} is not brighter than {Hex(m["normal"].Border)}.");

                // Disabled is faded (~45 % opacity): its border is much closer to the panel.
                Assert.True(Distance(m["disabled"].Border, m["disabled"].Outside) < Distance(m["normal"].Border, m["normal"].Outside) * 0.7,
                    $"disabled border {Hex(m["disabled"].Border)} is not faded.");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Gesture_panel_rows_custom_rows_and_search_cards_render_field_dropdowns()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var state = new GlideSettingsState();
            state.Gestures["windowed.middleClickImage"] = GestureCatalog.Fullscreen;
            state.CustomGestures.Add(new CustomGesture { Area = GestureAreas.Windowed, Input = GestureInputs.MiddleClick, Modifier = GestureModifiers.Alt, Action = GestureCatalog.Fullscreen });
            var window = new SettingsWindow(state, (_, _) => { });
            UseAppStyles(window);
            window.Height = 1500; // tall enough that the whole Windowed-image pane, custom row included, is on screen
            window.Show();
            try
            {
                // Settings > Mouse & keyboard > Mouse gestures card expanded, "Windowed image" area.
                typeof(SettingsWindow).GetMethod("OpenGestureArea", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [GestureAreas.Windowed, null]);
                var pane = (StackPanel)typeof(SettingsWindow).GetField("_gesturePane", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var extras = (StackPanel)typeof(SettingsWindow).GetField("_gestureExtrasPane", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var cards = (System.Collections.IDictionary)typeof(SettingsWindow).GetField("_cardById", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var gestureCard = (Control)cards["mouse-gestures"]!;
                var frame = Capture(window);
                frame.SaveCrop(PixelBounds(pane, window), "gesture-panel.png");
                frame.SaveCrop(PixelBounds(gestureCard, window), "gesture-card-windowed.png");
                frame.SaveCrop(new PixelRect(0, 0, frame.Width, frame.Height), "gesture-settings-window.png");
                // Windowed image options (under the table, full width): every label fits on one line.
                foreach (var label in extras.Children.OfType<Grid>().Where(g => g.Tag is string tag && tag.StartsWith("gestureArea:", StringComparison.Ordinal))
                             .SelectMany(g => g.Children.OfType<TextBlock>()))
                    Assert.True(label.Bounds.Height < label.FontSize * 2, $"option label '{label.Text}' wraps ({label.Bounds.Height} DIP high).");
                foreach (var check in extras.Children.OfType<CheckBox>())
                    Assert.True(check.Bounds.Height < 40, $"option '{check.Content}' wraps ({check.Bounds.Height} DIP high).");

                var pickers = pane.GetVisualDescendants().OfType<GestureActionPicker>().Where(p => p.IsEffectivelyVisible).ToList();
                Assert.NotEmpty(pickers);
                var first = pickers[0];
                var rf = PixelBounds(first, window);
                Assert.True(rf.Bottom < frame.Height, "first gesture row is not on screen.");
                var f = Measure(frame, rf, window.RenderScaling);
                AssertFieldLook(f, "panel row");
                Assert.True(f.ChevronDown, "panel row: chevron does not point down.");
                foreach (var p in pickers.Where(p => !p.Id.StartsWith("custom:", StringComparison.Ordinal)))
                    Assert.Equal(p.ActionId != GestureCatalog.Default, p.ShowsChangedDot);

                // Custom-gesture row: Input / Hold combos and the action dropdown share the field look.
                var custom = extras.Children.OfType<Grid>().FirstOrDefault(g => g.Tag is string tag && tag.StartsWith("custom:", StringComparison.Ordinal));
                Assert.NotNull(custom);
                Assert.True(PixelBounds(custom!, window).Bottom < frame.Height, "custom row is not on screen.");
                frame.SaveCrop(Inflate(PixelBounds(custom!, window), 6), "gesture-custom-row.png");
                foreach (var combo in custom!.Children.OfType<ComboBox>())
                {
                    var rc = PixelBounds(combo, window);
                    var c = frame[rc.X + 4, rc.Y + rc.Height / 2];
                    Assert.True(Math.Abs(rc.Height - 30) <= 1, $"custom combo height {rc.Height}.");
                    Assert.True(Distance(c, frame[rc.X - 3, rc.Y + rc.Height / 2]) >= 10, $"custom combo fill {Hex(c)} not visible.");
                    Assert.True(Luma(frame[rc.X + rc.Width / 2, rc.Y]) - Luma(c) >= 30, "custom combo border not visible.");
                }
                var customPicker = custom.Children.OfType<GestureActionPicker>().Single();
                var fc = Measure(frame, PixelBounds(customPicker, window), window.RenderScaling);
                AssertFieldLook(fc, "custom action");
                Assert.True(fc.ChevronDown, "custom action: chevron does not point down.");

                // Selection area: its options under the table, rendered for a visual check.
                typeof(SettingsWindow).GetMethod("SelectGestureArea", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [GestureAreas.Selection]);
                frame = Capture(window);
                frame.SaveCrop(PixelBounds(gestureCard, window), "gesture-card-selection.png");
                foreach (var label in extras.Children.OfType<Grid>().Where(g => g.Tag is string tag && tag.StartsWith("gestureArea:", StringComparison.Ordinal))
                             .SelectMany(g => g.Children.OfType<TextBlock>()))
                    Assert.True(label.Bounds.Height < label.FontSize * 2, $"option label '{label.Text}' wraps ({label.Bounds.Height} DIP high).");

                // Settings search result card: same field.
                typeof(SettingsWindow).GetMethod("RunSearchRebuild", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["scroll window"]);
                var searchPanel = window.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "SearchPanel");
                var cardPicker = searchPanel.GetLogicalDescendants().OfType<GestureActionPicker>().First(p => p.Id == "wheel.windowed");
                frame = Capture(window);
                var rs = PixelBounds(cardPicker, window);
                frame.SaveCrop(new PixelRect(rs.X - 420, rs.Y - 90, rs.Width + 520, rs.Height + 140), "gesture-search-card.png");
                var fs = Measure(frame, rs, window.RenderScaling);
                AssertFieldLook(fs, "search card");
                Assert.True(fs.ChevronDown, "search card: chevron does not point down.");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
