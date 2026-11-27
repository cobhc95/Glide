using System.Text.Json;
using Glide.Imaging;

namespace Glide.Core.Tests;

// Source-text contract kept: this asserts on production source (UI code-behind, native/installer files or a repo-wide scan) that cannot be run headlessly; behavioural coverage lives in the other test files.
[Trait("Area", "Video")]
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
        // The compositor renderer reads the layout through the shared OverlayFrameGeometry (round 4: also Overlay mode's).
        foreach (var file in new[] { "WindowInWindowOverlayManager.cs", "OverlayFrameGeometry.cs", "NativeOverlayWindowRenderer.cs", "OverlayVideoChromePainter.cs" })
            Assert.Contains("OverlayVideoChromeLayout.", ReadSource("src", "Glide.App", "Controls", file), StringComparison.Ordinal);
        Assert.Contains("OverlayFrameGeometry.BuildSpec", ReadSource("src", "Glide.App", "Controls", "CompositorOverlayRenderer.cs"), StringComparison.Ordinal);
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
        await Wait.UntilAsync(() => player.TryReadFrame(frame, 80, 45), "first video frame", 5000);
        Assert.Contains(frame.Where((_, i) => i % 4 != 3), b => b > 32); // not a black frame
        Assert.All(Enumerable.Range(0, 80 * 45), i => Assert.Equal(255, frame[i * 4 + 3])); // opaque

        player.Seek(1.5);
        await Wait.UntilAsync(() => player.GetInfo().Position >= 1.4, "seek to 1.5 s", 5000);

        player.SetPlaying(false);
        await Wait.UntilAsync(() => !player.GetInfo().Playing, "paused", 5000);
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

    /// <summary>
    /// 5.0.4: Glide no longer ships a fallback decoder. A VP9 WebM plays only when Windows has a VP9 decoder (the VP9
    /// Video Extensions); otherwise Glide names the missing codec. Which branch runs depends on this PC's codecs.
    /// </summary>
    [Fact]
    public async Task Vp9_webm_plays_with_the_windows_decoder_or_names_the_missing_codec()
    {
        var hasVp9 = NativeVideoPlayer.HasDecoder(VideoCodecCatalog.Vp9.Subtype, audio: false);
        Assert.NotNull(hasVp9); // the probe itself must work on Windows
        using var player = NativeVideoPlayer.TryOpen(Vp9FixturePath, out _);
        var info = player is null ? default : await player.WaitForReadyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        var played = info.State == VideoPlaybackState.Ready && info.Width > 0;
        var diagnosis = VideoPlaybackDiagnoser.Diagnose(Vp9FixturePath);
        if (hasVp9 == true)
        {
            // This PC has the VP9 extension: the "missing codec" branch can't be observed here, Windows plays the file.
            Assert.True(played, "Windows reports a VP9 decoder but could not play the fixture");
            Assert.NotEqual(VideoDiagnosisKind.MissingCodecs, diagnosis.Kind);
            return;
        }
        Assert.False(played);
        Assert.Equal(VideoDiagnosisKind.MissingCodecs, diagnosis.Kind);
        Assert.Equal("This video uses the VP9 codec, which isn't installed on this PC.", diagnosis.Message);
    }

    [Fact]
    public void Playable_fixture_is_diagnosed_as_having_every_decoder()
    {
        // H.264/AAC ship with Windows; the diagnosis must not invent a missing codec for them.
        var diagnosis = VideoPlaybackDiagnoser.Diagnose(FixturePath);
        Assert.Equal(VideoDiagnosisKind.DecodersPresent, diagnosis.Kind);
        Assert.Contains("H.264 (AVC) video", diagnosis.Message, StringComparison.Ordinal);
        Assert.Empty(diagnosis.MissingCodecs);
    }

    [Fact]
    public void Windows_reports_its_built_in_h264_decoder()
    {
        Assert.True(NativeVideoPlayer.HasDecoder(VideoCodecCatalog.H264.Subtype, audio: false));
        Assert.True(NativeVideoPlayer.HasDecoder(VideoCodecCatalog.Aac.Subtype, audio: true));
        // A subtype no decoder registers.
        Assert.False(NativeVideoPlayer.HasDecoder(VideoCodecCatalog.Fcc("ZZQ9"), audio: false));
    }

    [Fact]
    public void Overlay_sessions_use_only_the_codecs_installed_in_windows()
    {
        var session = ReadSource("src", "Glide.App", "Controls", "VideoOverlaySession.cs");
        Assert.Contains("NativeVideoPlayer.TryOpen(path, out _)", session, StringComparison.Ordinal);
        Assert.DoesNotContain("Backend", session, StringComparison.Ordinal);
        Assert.DoesNotContain("Download", session, StringComparison.OrdinalIgnoreCase);
        var window = ReadSource("src", "Glide.App", "MainWindow.Tabs.cs");
        Assert.Contains("VideoPlaybackDiagnoser.Diagnose(path)", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_video_bridge_has_no_bundled_decoder()
    {
        var cmake = ReadSource("native", "Glide.Native", "CMakeLists.txt");
        Assert.DoesNotContain("/DELAYLOAD:", cmake, StringComparison.Ordinal);
        Assert.DoesNotContain("GlideVideoFfmpeg", cmake, StringComparison.Ordinal);
        var video = ReadSource("native", "Glide.Native", "GlideVideo.cpp");
        Assert.Contains("GlideVideoHasDecoder", video, StringComparison.Ordinal);
        Assert.Contains("MFTEnumEx", video, StringComparison.Ordinal);
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
