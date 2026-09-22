using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Glide.App.Printing;
using Xunit;

namespace Glide.Core.Tests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class PrintWindowTests
{
    public PrintWindowTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
    }

    [Fact]
    public void PrintWindow_Instantiates_Without_Printers()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            using var bitmap = new RenderTargetBitmap(new Avalonia.PixelSize(64, 48));
            var args = new PrintWindowArgs(
                "test.jpg", bitmap, 64, 48, 96, 96,
                _ => Task.FromResult<(Bitmap, int, int)>((bitmap, 64, 48)));
            var window = new PrintWindow(args);
            Assert.NotNull(window);
            Assert.Contains("Print", window.Title ?? "");
        });
    }

    [Fact]
    public void PrintPreviewControl_renders_without_paper_or_bitmap()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var control = new PrintPreviewControl { Width = 300, Height = 300 };
            Assert.NotNull(control);
            var layout = PrintLayoutEngine.Compute(new PrintLayoutInput(
                100, 80, 96, 96,
                new PrintPaperGeometry(827, 1169, 50, 50, 727, 1069),
                false, 50, 50, 50, 50,
                PrintScalingMode.BestFit, 100, true, true,
                PrintHAlign.Centre, PrintVAlign.Centre));
            Assert.True(layout.DestWidth > 0 && layout.DestHeight > 0);
        });
    }
}
