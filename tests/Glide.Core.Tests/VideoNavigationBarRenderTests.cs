using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.App.Controls;

namespace Glide.Core.Tests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class VideoNavigationBarRenderTests
{
    public VideoNavigationBarRenderTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    [Theory]
    [InlineData(180, 120)]
    [InlineData(300, 170)]
    [InlineData(420, 240)]
    public void Every_navigation_control_is_drawn(int width, int height) =>
        AvaloniaHeadlessFixture.RunOnUIThread(() => RenderAndCheck(width, height));

    private static void RenderAndCheck(int width, int height)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        using (var context = bitmap.CreateDrawingContext())
            OverlayVideoChromePainter.Draw(context, new Rect(0, 0, width, height),
                new OverlayVideoChrome(Progress: .4, Playing: true, Muted: false, Volume: .6), Color.FromRgb(0x36, 0xB8, 0xF4));

        var saveDirectory = Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");
        if (!string.IsNullOrWhiteSpace(saveDirectory)) bitmap.Save(Path.Combine(saveDirectory, $"navbar-{width}x{height}.png"));

        var pixels = ReadPixels(bitmap, width, height);
        bool Drawn(Glide.Core.ChromeRect r) => Enumerable.Range((int)r.X, Math.Max(1, (int)r.Width))
            .Any(x => Enumerable.Range((int)r.Y, Math.Max(1, (int)r.Height)).Any(y => pixels[(y * width + x) * 4 + 3] > 0 && pixels[(y * width + x) * 4] + pixels[(y * width + x) * 4 + 1] + pixels[(y * width + x) * 4 + 2] > 300));

        Assert.True(Drawn(OverlayVideoChromeLayout.PlayPauseButton(width, height)));
        Assert.True(Drawn(OverlayVideoChromeLayout.MuteButton(width, height)));
        Assert.True(Drawn(OverlayVideoChromeLayout.SeekTrack(width, height)));
        if (OverlayVideoChromeLayout.PreviousButton(width, height) is { } previous) Assert.True(Drawn(previous));
        if (OverlayVideoChromeLayout.NextButton(width, height) is { } next) Assert.True(Drawn(next));
        if (OverlayVideoChromeLayout.VolumeTrack(width, height) is { } volume) Assert.True(Drawn(volume));
    }

    private static byte[] ReadPixels(Bitmap bitmap, int width, int height)
    {
        var buffer = new byte[width * height * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, width * 4); }
        finally { handle.Free(); }
        return buffer;
    }
}
