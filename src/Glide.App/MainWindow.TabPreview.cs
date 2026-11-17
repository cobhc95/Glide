using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.App.Explorer;
using Glide.App.Services;
using Glide.Core.Workspace;
using Glide.Imaging;

namespace Glide.App;

/// <summary>
/// 5.0.6 tab hover tooltip (approved mockup docs/mockups/tab-hover-preview-5.0.6.md). "Show tab previews on hover": a
/// background tab's tooltip card starts with its picture, or the first thumbnails of an Explorer tab's folder, edge to
/// edge (<see cref="TabHoverCard"/>), from <see cref="TabPreviewCache"/> (cached Windows thumbnails or a bounded
/// background decode, never the UI thread). Under it: the name, the full folder path, the picture's pixel and file size
/// (an Explorer tab's item count), and the mouse hint. "Animate GIF previews on hover" plays an animated GIF's preview
/// while the tooltip is open (<see cref="TabGifPreview"/>); its frames are freed when the tooltip closes.
/// </summary>
public partial class MainWindow
{
    private TabPreviewCache? _tabPreviewCache;
    private readonly List<Bitmap> _tabPreviewShown = new();

    /// <summary>The parts of one tab's tooltip that are refreshed every time it opens.</summary>
    private sealed record TabToolTipParts(ToolTip ToolTip, TabHoverCard Card, TextBlock Details, TextBlock Hint);

    private const double TabToolTipCornerRadius = 8;

    private TabPreviewCache TabPreviews
    {
        get
        {
            if (_tabPreviewCache is null)
            {
                _tabPreviewCache = new TabPreviewCache();
                Closed += (_, _) =>
                {
                    StopTabGifPreview();
                    _tabPreviewCache?.Dispose();
                };
            }
            return _tabPreviewCache;
        }
    }

    /// <summary>The empty preview slot at the top of a tab tooltip card: its top corners follow the card's.</summary>
    private static Border CreateTabPreviewHost() => new()
    {
        IsVisible = false,
        ClipToBounds = true,
        CornerRadius = new CornerRadius(TabToolTipCornerRadius - 1, TabToolTipCornerRadius - 1, 0, 0),
    };

    /// <summary>Tooltip opening: the details line, and the preview of a background tab (none for the active tab, Home, or with the setting off).</summary>
    private void FillTabPreview(TabState tab, TabToolTipParts parts)
    {
        StopTabGifPreview();
        var card = parts.Card;
        var host = card.PictureHost;
        // Each fill gets a fresh token; background results for an older fill are dropped.
        var fill = new object();
        host.Tag = fill;
        host.Child = null;
        host.IsVisible = false;
        card.PictureAspect = 0;
        _tabPreviewShown.Clear();
        _ = FillTabDetailsAsync(tab, parts.Details, host, fill);
        if (!_settings.TabHoverPreviews || _workspace.Active?.Id == tab.Id) return;
        switch (tab)
        {
            case ImageTabState image:
                _ = ShowPicturePreviewAsync(image.Path, card, fill);
                break;
            case BrowserTabState browser:
                _ = ShowFolderPreviewAsync(browser.Folder, card, fill);
                break;
        }
    }

    private async Task ShowPicturePreviewAsync(string path, TabHoverCard card, object fill)
    {
        const int width = TabPreviewCache.PictureWidth, height = TabPreviewCache.PictureHeight;
        var bitmap = TabPreviews.TryGet(path, width, height) ?? await TabPreviews.GetAsync(path, width, height);
        var host = card.PictureHost;
        if (bitmap is null || !ReferenceEquals(host.Tag, fill)) return; // no preview, or the tooltip was refilled meanwhile
        var image = PreviewImage();
        image.Source = bitmap;
        host.Child = image;
        host.IsVisible = true;
        card.PictureAspect = (double)bitmap.PixelSize.Width / Math.Max(1, bitmap.PixelSize.Height);
        _tabPreviewShown.Add(bitmap);
        TabPreviews.SetShown(_tabPreviewShown);
        if (_settings.AnimateGifPreviews && GifAnimationPlayer.IsGif(path)) StartTabGifPreview(path, image, bitmap, host, fill);
    }

    private async Task ShowFolderPreviewAsync(string folder, TabHoverCard card, object fill)
    {
        var pictures = await TabPreviewCache.FirstPicturesAsync(folder);
        var host = card.PictureHost;
        if (pictures.Count == 0 || !ReferenceEquals(host.Tag, fill)) return;
        // Square cells side by side across the card.
        var row = new UniformGrid { Rows = 1, Columns = pictures.Count };
        host.Child = row;
        host.IsVisible = true;
        card.PictureAspect = pictures.Count;
        foreach (var picture in pictures)
        {
            var image = PreviewImage();
            row.Children.Add(image);
            _ = SetFolderThumbnailAsync(image, host, fill, picture);
        }
    }

    private static Image PreviewImage()
    {
        // Fills its slot and crops the overflow (no letterboxing).
        var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.MediumQuality);
        return image;
    }

    private async Task SetFolderThumbnailAsync(Image image, Border host, object fill, string path)
    {
        const int size = TabPreviewCache.FolderThumbSize;
        var bitmap = TabPreviews.TryGet(path, size, size) ?? await TabPreviews.GetAsync(path, size, size);
        if (bitmap is null || !ReferenceEquals(host.Tag, fill)) return; // an unreadable picture leaves its cell empty
        image.Source = bitmap;
        _tabPreviewShown.Add(bitmap);
        TabPreviews.SetShown(_tabPreviewShown);
    }

    // ---- details line -------------------------------------------------------------------------------------------

    private async Task FillTabDetailsAsync(TabState tab, TextBlock details, Border host, object fill)
    {
        details.Text = null;
        details.IsVisible = false;
        var showHidden = _settings.ExplorerShowHiddenFiles;
        var onlyImages = _settings.ExplorerShowOnlyImages;
        string? text;
        try { text = await Task.Run(() => TabDetails(tab, showHidden, onlyImages)); }
        catch { text = null; /* unreadable: no details line */ }
        if (string.IsNullOrEmpty(text) || !ReferenceEquals(host.Tag, fill)) return;
        details.Text = text;
        details.IsVisible = true;
    }

    /// <summary>
    /// Background thread. A picture: "3840×2160 · 4.2 MB" (each part only when known). An Explorer tab: its item count,
    /// counted as the Explorer tab lists the folder. Null when nothing is known.
    /// </summary>
    internal static string? TabDetails(TabState tab, bool showHidden, bool onlyImages)
    {
        switch (tab)
        {
            case ImageTabState image when File.Exists(image.Path):
                var parts = new List<string>(2);
                if (ProbePixelSize(image.Path) is { } size) parts.Add($"{size.Width}×{size.Height}");
                parts.Add(ExplorerModel.FormatSize(new FileInfo(image.Path).Length));
                return string.Join(" · ", parts);
            case BrowserTabState browser when Directory.Exists(browser.Folder):
                var count = ExplorerModel.Enumerate(browser.Folder, showHidden, onlyImages, CancellationToken.None).Count;
                return count == 1 ? "1 item" : $"{count:N0} items";
            default:
                return null;
        }
    }

    private static ImageDimensions? ProbePixelSize(string path)
    {
        if (BuiltInRasterDecoder.TryProbeDimensions(path, out var raster) && raster.IsValid) return raster;
        if (ImageHeaderProbe.TryProbe(path, out var header) && header.IsValid) return header;
        if (NativeImageProbe.TryProbe(path, out var native) && native.Width > 0 && native.Height > 0)
            return new ImageDimensions((int)Math.Min(native.Width, (uint)int.MaxValue), (int)Math.Min(native.Height, (uint)int.MaxValue));
        return null;
    }

    // ---- animated GIF previews ----------------------------------------------------------------------------------

    private CancellationTokenSource? _tabGifCts;
    private DispatcherTimer? _tabGifTimer;
    private List<(WriteableBitmap Bitmap, int DelayMs)>? _tabGifFrames;
    private Image? _tabGifImage;
    private Bitmap? _tabGifStill;
    private Border? _tabGifHost;

    /// <summary>Decodes the GIF's small frames in the background, then plays them in the preview while this fill is current.</summary>
    private void StartTabGifPreview(string path, Image image, Bitmap still, Border host, object fill)
    {
        var cts = _tabGifCts = new CancellationTokenSource();
        _tabGifHost = host;
        _ = Task.Run(() => TabGifPreview.Decode(path, TabPreviewCache.PictureWidth, TabPreviewCache.PictureHeight, cts.Token), cts.Token)
            .ContinueWith(task =>
            {
                if (task.Status != TaskStatus.RanToCompletion || task.Result is not { } frames) return; // still first frame
                Dispatcher.UIThread.Post(() =>
                {
                    // Never shown yet, so they can be freed at once when the tooltip moved on.
                    if (cts.IsCancellationRequested || !ReferenceEquals(host.Tag, fill)) { TabGifPreview.Dispose(frames); return; }
                    _tabGifFrames = frames;
                    _tabGifImage = image;
                    _tabGifStill = still;
                    var index = 0;
                    image.Source = frames[0].Bitmap;
                    var timer = _tabGifTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameDelay(frames[0].DelayMs) };
                    timer.Tick += (_, _) =>
                    {
                        index = (index + 1) % frames.Count;
                        image.Source = frames[index].Bitmap;
                        timer.Interval = FrameDelay(frames[index].DelayMs);
                    };
                    timer.Start();
                });
            }, TaskScheduler.Default);
    }

    private static TimeSpan FrameDelay(int delayMs) => TimeSpan.FromMilliseconds(Math.Max(20, delayMs));

    /// <summary>
    /// Tooltip closed or refilled, or the window closed: stops the animation and frees its frames. With
    /// <paramref name="onlyFor"/>, only when the animation plays in that preview slot (another tab's tooltip may
    /// already have opened when this one reports closing).
    /// </summary>
    private void StopTabGifPreview(Border? onlyFor = null)
    {
        if (onlyFor is not null && !ReferenceEquals(onlyFor, _tabGifHost)) return;
        _tabGifHost = null;
        _tabGifCts?.Cancel();
        _tabGifCts = null;
        _tabGifTimer?.Stop();
        _tabGifTimer = null;
        if (_tabGifFrames is not { } frames) return;
        if (_tabGifImage is not null) _tabGifImage.Source = _tabGifStill;
        _tabGifFrames = null;
        _tabGifImage = null;
        _tabGifStill = null;
        // The last shown frame may still be in the render pipeline: free them once it has moved on (as GifAnimationPlayer does).
        DispatcherTimer.RunOnce(() => TabGifPreview.Dispose(frames), TimeSpan.FromSeconds(2));
    }

    /// <summary>Test hook: true while a tab tooltip's GIF preview is playing.</summary>
    internal bool TabGifPreviewPlayingForTests => _tabGifTimer is not null;
}
