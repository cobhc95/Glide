using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Controls;

namespace Glide.Core.Tests;

/// <summary>
/// Headless renders of the round 4 chrome: picture-in-picture vs Overlay mode (shared painter, K1) and the tab-bar kind
/// icons (I3). Set GLIDE_SAVE_RENDERS to a folder to write the PNG previews (synthetic pictures only).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Overlay")]
public sealed class Round4ChromeRenderTests
{
    public Round4ChromeRenderTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static string? SaveDirectory => Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");
    private static readonly Color Accent = Color.FromRgb(0x36, 0xB8, 0xF4);

    private sealed class DrawControl(Action<DrawingContext, Size> draw) : Control
    {
        public override void Render(DrawingContext context) => draw(context, Bounds.Size);
    }

    private static void Paint(DrawingContext context, Rect rect)
    {
        var sky = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromRgb(0x5B, 0x8F, 0xD6), 0), new GradientStop(Color.FromRgb(0xF2, 0xC1, 0x8B), 1) }
        };
        context.DrawRectangle(sky, null, rect);
        context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x8A)), null, new Point(rect.X + rect.Width * .72, rect.Y + rect.Height * .3), rect.Width * .07, rect.Width * .07);
        var hill = new StreamGeometry();
        using (var g = hill.Open())
        {
            g.BeginFigure(new Point(rect.X, rect.Bottom), true);
            g.LineTo(new Point(rect.X, rect.Y + rect.Height * .7));
            g.LineTo(new Point(rect.X + rect.Width * .35, rect.Y + rect.Height * .5));
            g.LineTo(new Point(rect.X + rect.Width * .6, rect.Y + rect.Height * .68));
            g.LineTo(new Point(rect.Right, rect.Y + rect.Height * .55));
            g.LineTo(new Point(rect.Right, rect.Bottom));
            g.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x3A, 0x6B, 0x4A)), null, hill);
    }

    private static RenderTargetBitmap Render(Control control, Size size)
    {
        var window = new Window { Width = size.Width, Height = size.Height, Content = control, Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x36)) };
        window.Show();
        for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
        bitmap.Render(window);
        window.Close();
        return bitmap;
    }

    private static void Save(Bitmap bitmap, string name)
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
        Directory.CreateDirectory(SaveDirectory);
        bitmap.Save(Path.Combine(SaveDirectory, name));
    }

    [Fact]
    public void Picture_in_picture_and_overlay_mode_render_the_same_chrome() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var size = new Size(860, 380);
        var label = new Typeface("Segoe UI");
        var control = new DrawControl((context, _) =>
        {
            void Caption(string text, double x) => context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, label, 13, Brushes.White), new Point(x, 10));

            // Left: a picture-in-picture overlay inside Glide's viewer.
            Caption("Picture-in-picture overlay (inside Glide)", 20);
            var pip = new Rect(30, 50, 360, 270);
            Paint(context, pip);
            var state = new OverlayState(Guid.NewGuid(), "a.png", pip.X, pip.Y, pip.Width, pip.Height, Opacity: .8);
            OverlayFrameChromePainter.Draw(context, WindowInWindowOverlayManager.BuildChromeSpec(state, glow: true), Accent);

            // Right: Overlay mode (the Glide window itself), same painter, plus its drag strip.
            Caption("Overlay mode (Glide window) with drag strip", 450);
            var mode = new Rect(460, 50, 360, 270);
            Paint(context, mode);
            using (context.PushTransform(Matrix.CreateTranslation(mode.X, mode.Y)))
            {
                var chrome = new LegacyOverlayChrome { OpacityValue = .8 };
                OverlayFrameChromePainter.Draw(context, chrome.BuildSpec(mode.Size), Accent);
                if (OverlayChromePolicy.DragStripRect(new Rect(mode.Size)) is { } strip)
                {
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xBF, 0x14, 0x14, 0x16)), null, strip, strip.Height / 2, strip.Height / 2);
                    var c = strip.Center;
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC8, 0xE6, 0xE9, 0xEE)), null, new Rect(c.X - 22, c.Y - 2, 44, 4), 2, 2);
                }
            }
            context.DrawText(new FormattedText("Overlay mode right-click: Show drag strip, Lock position, opacity, interactions, Always start, Exit",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, label, 12, Brushes.Gainsboro), new Point(20, 345));
        }) { Width = size.Width, Height = size.Height };

        using var bitmap = Render(control, size);
        Save(bitmap, "overlay-shared-chrome.png");
        Assert.Equal(new PixelSize(860, 380), bitmap.PixelSize);
    });

    [Fact]
    public void Tab_kind_icons_are_larger_and_distinct() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        Assert.Equal(18, MainWindow.TabKindIconSize);
        Assert.Equal("TabExplorer", MainWindow.TabKindIcon(Glide.Core.Workspace.TabKind.Browser));
        Assert.Equal("TabImage", MainWindow.TabKindIcon(Glide.Core.Workspace.TabKind.Image));

        var size = new Size(680, 120);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 14 };
        foreach (var (title, explorer, image, px, stroke) in new[] { ("Before (14 px outline)", "Browser", "Image", 14.0, 1.5), ("After (18 px filled)", "TabExplorer", "TabImage", 18.0, 1.6) })
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock { Text = title, Width = 150, Foreground = Brushes.Gainsboro, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            foreach (var (kind, name, active) in new[] { (explorer, "Pictures", true), (image, "sunset.jpg", false), (explorer, "Downloads", false), (image, "cat.png", true) })
            {
                var tab = new Border
                {
                    Background = new SolidColorBrush(active ? Color.FromRgb(0x3A, 0x40, 0x48) : Color.FromRgb(0x24, 0x28, 0x2E)),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8),
                    Child = new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 7,
                        Children =
                        {
                            new GlideIconView { Kind = kind, Width = px, Height = px, StrokeWidth = stroke,
                                IconBrush = new SolidColorBrush(active ? Accent : Color.Parse("#9AA6B2")), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                            new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
                        }
                    }
                };
                row.Children.Add(tab);
            }
            panel.Children.Add(row);
        }
        using var bitmap = Render(panel, size);
        Save(bitmap, "tab-icons-before-after.png");
        Assert.Equal(new PixelSize(680, 120), bitmap.PixelSize);
    });
}
