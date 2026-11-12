using System.Text.Json;
using Glide.Imaging;

namespace Glide.Core.Tests;

public sealed class OverlayVideoTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "overlay-video.mp4");

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.MOV")]
    [InlineData("clip.mkv")]
    [InlineData("clip.webm")]
    [InlineData("clip.avi")]
    [InlineData("clip.wmv")]
    [InlineData("clip.m2ts")]
    public void Major_video_containers_route_to_overlays(string name)
    {
        Assert.True(VideoFormatRegistry.IsVideo(name));
        Assert.False(ImageFormatRegistry.IsSupported(name));
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("layers.psd")]
    [InlineData("noext")]
    [InlineData("")]
    public void Images_are_not_videos(string name) => Assert.False(VideoFormatRegistry.IsVideo(name));

    [Fact]
    public void IsVideo_is_derived_and_never_persisted()
    {
        var state = new OverlayState(Guid.NewGuid(), @"C:\v\clip.mp4", 0, 0, 320, 180);
        Assert.True(state.IsVideo);
        Assert.DoesNotContain("IsVideo", JsonSerializer.Serialize(state), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(180, 120)]
    [InlineData(320, 180)]
    [InlineData(640, 360)]
    [InlineData(1280, 720)]
    public void Video_navigation_controls_never_overlap_other_chrome(double w, double h)
    {
        var bar = OverlayVideoChromeLayout.NavigationBar(w, h);
        var play = OverlayVideoChromeLayout.PlayPauseButton(w, h);
        var mute = OverlayVideoChromeLayout.MuteButton(w, h);
        var seek = OverlayVideoChromeLayout.SeekTrack(w, h);

        // Inside the overlay, clear of the lower-right resize corner (40x40 hit area).
        Assert.True(bar.X >= 0 && bar.Bottom <= h && bar.Right <= w - 36);
        foreach (var control in new[] { play, mute, seek })
            Assert.True(control.X >= bar.X && control.Right <= bar.Right && control.Y >= bar.Y && control.Bottom <= bar.Bottom);
        Assert.False(play.Intersects(seek));
        Assert.False(seek.Intersects(mute));
        Assert.True(seek.Width > 0);

        // Existing image controls move out of the bar's way for video overlays.
        Assert.False(OverlayVideoChromeLayout.ZoomOut(w, h, video: true).Intersects(bar));
        Assert.False(OverlayVideoChromeLayout.ZoomIn(w, h, video: true).Intersects(bar));
        Assert.False(OverlayVideoChromeLayout.OpacitySlider(w, h, video: true).Intersects(bar));
        Assert.False(OverlayVideoChromeLayout.OpacitySlider(w, h, video: true).Intersects(play));
    }

    [Fact]
    public void Image_overlay_chrome_geometry_is_unchanged()
    {
        Assert.Equal(new ChromeRect(8, 25, 22, 70), OverlayVideoChromeLayout.OpacitySlider(200, 120, video: false));
        Assert.Equal(new ChromeRect(64, 82, 30, 30), OverlayVideoChromeLayout.ZoomOut(200, 120, video: false));
        Assert.Equal(new ChromeRect(106, 82, 30, 30), OverlayVideoChromeLayout.ZoomIn(200, 120, video: false));
    }

    [Fact]
    public void Seek_fraction_clamps_to_the_track()
    {
        var track = OverlayVideoChromeLayout.SeekTrack(640, 360);
        Assert.Equal(0, OverlayVideoChromeLayout.SeekFraction(640, 360, track.X - 50));
        Assert.Equal(1, OverlayVideoChromeLayout.SeekFraction(640, 360, track.Right + 50));
        Assert.Equal(.5, OverlayVideoChromeLayout.SeekFraction(640, 360, track.CenterX), 3);
    }

    [Fact]
    public void All_three_overlay_renderers_and_hit_testing_share_the_video_layout()
    {
        foreach (var file in new[] { "WindowInWindowOverlayManager.cs", "CompositorOverlayRenderer.cs", "NativeOverlayWindowRenderer.cs", "OverlayVideoChromePainter.cs" })
            Assert.Contains("OverlayVideoChromeLayout.", ReadSource("src", "Glide.App", "Controls", file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_player_plays_seeks_pauses_and_delivers_frames()
    {
        using var player = NativeVideoPlayer.TryOpen(FixturePath, out var hr);
        Assert.True(player is not null, $"open failed 0x{hr:X8}");
        player!.SetMuted(true);
        var info = await player.WaitForReadyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(VideoPlaybackState.Ready, info.State);
        Assert.Equal(160, info.Width);
        Assert.Equal(90, info.Height);
        Assert.InRange(info.Duration, 1.8, 2.2);
        Assert.True(info.Loop);

        var frame = new byte[80 * 45 * 4];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var got = false;
        while (!got && DateTime.UtcNow < deadline) { got = player.TryReadFrame(frame, 80, 45); await Task.Delay(10); }
        Assert.True(got);
        Assert.Contains(frame.Where((_, i) => i % 4 != 3), b => b > 32); // not a black frame
        Assert.All(Enumerable.Range(0, 80 * 45), i => Assert.Equal(255, frame[i * 4 + 3])); // opaque

        player.Seek(1.5);
        await Task.Delay(300);
        Assert.True(player.GetInfo().Position >= 1.4);

        player.SetPlaying(false);
        await Task.Delay(200);
        Assert.False(player.GetInfo().Playing);
        player.SetMuted(false);
        Assert.False(player.GetInfo().Muted);
        player.SetMuted(true);
    }

    [Fact]
    public async Task A_playing_video_never_blocks_deleting_the_file()
    {
        var copy = Path.Combine(Path.GetTempPath(), $"glide-video-{Guid.NewGuid():N}.mp4");
        File.Copy(FixturePath, copy);
        using var player = NativeVideoPlayer.TryOpen(copy, out _);
        Assert.NotNull(player);
        player!.SetMuted(true);
        Assert.Equal(VideoPlaybackState.Ready, (await player.WaitForReadyAsync(TimeSpan.FromSeconds(10), CancellationToken.None)).State);
        File.Delete(copy);
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void Missing_or_non_video_file_fails_cleanly()
    {
        Assert.Null(NativeVideoPlayer.TryOpen(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".mp4"), out _));
        var garbage = Path.Combine(Path.GetTempPath(), $"glide-garbage-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(garbage, Enumerable.Repeat((byte)0x5A, 4096).ToArray());
        try
        {
            using var player = NativeVideoPlayer.TryOpen(garbage, out _);
            if (player is null) return;
            var info = player.WaitForReadyAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(VideoPlaybackState.Failed, info.State);
        }
        finally { File.Delete(garbage); }
    }

    private static string Vp9FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "overlay-video-vp9.webm");

    [Theory]
    [InlineData("overlay-video-vp9.webm")]
    [InlineData("overlay-video.mp4")]
    public async Task Bundled_ffmpeg_backend_plays_seeks_and_pauses(string fixture)
    {
        using var player = NativeVideoPlayer.TryOpen(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), out var hr, VideoBackend.Ffmpeg);
        Assert.True(player is not null, $"FFmpeg open failed 0x{hr:X8}");
        player!.SetMuted(true);
        var info = await player.WaitForReadyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(VideoPlaybackState.Ready, info.State);
        Assert.Equal(VideoBackend.Ffmpeg, info.Backend);
        Assert.Equal((160, 90), (info.Width, info.Height));
        Assert.InRange(info.Duration, 1.8, 2.3);

        var frame = new byte[80 * 45 * 4];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var frames = 0;
        while (frames < 5 && DateTime.UtcNow < deadline) { if (player.TryReadFrame(frame, 80, 45)) frames++; await Task.Delay(10); }
        Assert.True(frames >= 5, $"only {frames} frames");
        Assert.Contains(frame.Where((_, i) => i % 4 != 3), b => b > 32);
        Assert.All(Enumerable.Range(0, 80 * 45), i => Assert.Equal(255, frame[i * 4 + 3]));

        player.Seek(1.2);
        await Task.Delay(250);
        Assert.InRange(player.GetInfo().Position, 1.15, 1.9);

        player.SetPlaying(false);
        var paused = player.GetInfo().Position;
        await Task.Delay(200);
        Assert.False(player.GetInfo().Playing);
        Assert.Equal(paused, player.GetInfo().Position, 3);
    }

    [Fact]
    public async Task Ffmpeg_backend_never_blocks_deleting_the_file()
    {
        var copy = Path.Combine(Path.GetTempPath(), $"glide-video-{Guid.NewGuid():N}.webm");
        File.Copy(Vp9FixturePath, copy);
        using var player = NativeVideoPlayer.TryOpen(copy, out _, VideoBackend.Ffmpeg);
        Assert.NotNull(player);
        player!.SetMuted(true);
        Assert.Equal(VideoPlaybackState.Ready, (await player.WaitForReadyAsync(TimeSpan.FromSeconds(10), CancellationToken.None)).State);
        File.Delete(copy);
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void Ffmpeg_backend_rejects_garbage_without_crashing()
    {
        var garbage = Path.Combine(Path.GetTempPath(), $"glide-garbage-{Guid.NewGuid():N}.webm");
        File.WriteAllBytes(garbage, Enumerable.Repeat((byte)0x5A, 4096).ToArray());
        try { Assert.Null(NativeVideoPlayer.TryOpen(garbage, out _, VideoBackend.Ffmpeg)); }
        finally { File.Delete(garbage); }
    }

    [Fact]
    public void Overlay_sessions_try_windows_codecs_first_then_the_bundled_ffmpeg_fallback()
    {
        var session = ReadSource("src", "Glide.App", "Controls", "VideoOverlaySession.cs");
        var windows = session.IndexOf("VideoBackend.MediaFoundation, options", StringComparison.Ordinal);
        var fallback = session.IndexOf("VideoBackend.Ffmpeg, options", StringComparison.Ordinal);
        Assert.True(windows > 0 && fallback > windows);
    }

    [Fact]
    public void Ffmpeg_is_delay_loaded_and_its_download_pack_carries_the_license()
    {
        var cmake = ReadSource("native", "Glide.Native", "CMakeLists.txt");
        var build = ReadSource("build.cmd");
        Assert.Contains("/DELAYLOAD:", cmake, StringComparison.Ordinal);
        Assert.Contains(@"tools\fetch-ffmpeg.ps1", build, StringComparison.Ordinal);
        var pack = ReadSource("tools", "package-ffmpeg-pack.ps1");
        Assert.Contains("LICENSE.txt", pack, StringComparison.Ordinal);
        Assert.Contains("NOTICE.txt", pack, StringComparison.Ordinal);
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
