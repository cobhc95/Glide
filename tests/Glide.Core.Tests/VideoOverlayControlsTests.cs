using System.Text.Json;
using Glide.Imaging;

namespace Glide.Core.Tests;

public sealed class VideoOverlayControlsTests
{
    [Theory]
    [InlineData(180, 120, false, false)]   // narrow: play · seek · mute
    [InlineData(300, 170, true, false)]    // medium: + previous / next
    [InlineData(420, 240, true, true)]     // wide: + volume slider
    [InlineData(1280, 720, true, true)]
    public void Navigation_bar_adapts_to_overlay_width_without_overlaps(double w, double h, bool skip, bool volume)
    {
        Assert.Equal(skip, OverlayVideoChromeLayout.PreviousButton(w, h) is not null);
        Assert.Equal(skip, OverlayVideoChromeLayout.NextButton(w, h) is not null);
        Assert.Equal(volume, OverlayVideoChromeLayout.VolumeTrack(w, h) is not null);

        var bar = OverlayVideoChromeLayout.NavigationBar(w, h);
        var controls = new List<ChromeRect>
        {
            OverlayVideoChromeLayout.PlayPauseButton(w, h),
            OverlayVideoChromeLayout.SeekTrack(w, h),
            OverlayVideoChromeLayout.MuteButton(w, h)
        };
        if (OverlayVideoChromeLayout.PreviousButton(w, h) is { } previous) controls.Add(previous);
        if (OverlayVideoChromeLayout.NextButton(w, h) is { } next) controls.Add(next);
        if (OverlayVideoChromeLayout.VolumeTrack(w, h) is { } track) controls.Add(track);

        foreach (var control in controls)
            Assert.True(control.X >= bar.X && control.Right <= bar.Right, $"{control} outside {bar}");
        for (var a = 0; a < controls.Count; a++)
            for (var b = a + 1; b < controls.Count; b++)
                Assert.False(controls[a].Intersects(controls[b]), $"{controls[a]} overlaps {controls[b]}");
        Assert.True(OverlayVideoChromeLayout.SeekTrack(w, h).Width >= 20);
        if (skip)
        {
            // Order: previous, play, next.
            Assert.True(OverlayVideoChromeLayout.PreviousButton(w, h)!.Value.Right <= OverlayVideoChromeLayout.PlayPauseButton(w, h).X);
            Assert.True(OverlayVideoChromeLayout.PlayPauseButton(w, h).Right <= OverlayVideoChromeLayout.NextButton(w, h)!.Value.X);
        }
    }

    [Fact]
    public void Volume_fraction_maps_across_the_slider()
    {
        var track = OverlayVideoChromeLayout.VolumeTrack(640, 360)!.Value;
        Assert.Equal(0, OverlayVideoChromeLayout.VolumeFraction(640, 360, track.X - 10));
        Assert.Equal(1, OverlayVideoChromeLayout.VolumeFraction(640, 360, track.Right + 10));
        Assert.Equal(.5, OverlayVideoChromeLayout.VolumeFraction(640, 360, track.CenterX), 3);
    }

    private static readonly IComparer<string> Logical =
        Glide.App.Services.SiblingFolderNavigationService.WindowsLogicalStringComparer.Instance;

    private static readonly string[] Folder =
    {
        @"C:\v\clip 10.mp4", @"C:\v\clip 2.webm", @"C:\v\notes.txt", @"C:\v\clip 1.mkv", @"C:\v\photo.jpg"
    };

    [Theory]
    [InlineData(@"C:\v\clip 1.mkv", 1, @"C:\v\clip 2.webm")]
    [InlineData(@"C:\v\clip 2.webm", 1, @"C:\v\clip 10.mp4")]   // natural order: 2 before 10
    [InlineData(@"C:\v\clip 10.mp4", 1, @"C:\v\clip 1.mkv")]    // wraps to the first
    [InlineData(@"C:\v\clip 1.mkv", -1, @"C:\v\clip 10.mp4")]   // wraps to the last
    [InlineData(@"C:\v\clip 10.mp4", -1, @"C:\v\clip 2.webm")]
    public void Next_and_previous_follow_explorer_order_and_wrap(string current, int direction, string expected) =>
        Assert.Equal(expected, VideoFormatRegistry.Sibling(current, Folder, direction, Logical));

    [Fact]
    public void Skipping_from_a_deleted_video_continues_from_its_place()
    {
        Assert.Equal(@"C:\v\clip 2.webm", VideoFormatRegistry.Sibling(@"C:\v\clip 1b.mp4", Folder, 1, Logical));
        Assert.Equal(@"C:\v\clip 1.mkv", VideoFormatRegistry.Sibling(@"C:\v\clip 1b.mp4", Folder, -1, Logical));
    }

    [Fact]
    public void Only_video_in_folder_returns_itself_and_empty_folder_returns_null()
    {
        Assert.Equal(@"C:\v\a.mp4", VideoFormatRegistry.Sibling(@"C:\v\a.mp4", new[] { @"C:\v\a.mp4", @"C:\v\b.png" }, 1, Logical));
        Assert.Null(VideoFormatRegistry.Sibling(@"C:\v\a.mp4", new[] { @"C:\v\b.png" }, 1, Logical));
    }

    [Fact]
    public void Video_preferences_default_to_auto_play_next_and_round_trip()
    {
        var fresh = new OverlayState(Guid.NewGuid(), @"C:\v\a.mp4", 0, 0, 320, 180);
        Assert.True(fresh.VideoAutoPlayNext);
        Assert.False(fresh.VideoLoop);
        Assert.Equal(1, fresh.VideoVolume);

        var saved = fresh with { VideoMuted = true, VideoVolume = .35, VideoLoop = true, VideoAutoPlayNext = false };
        var restored = JsonSerializer.Deserialize<OverlayState>(JsonSerializer.Serialize(saved))!.Normalize();
        Assert.Equal((true, .35, true, false), (restored.VideoMuted, restored.VideoVolume, restored.VideoLoop, restored.VideoAutoPlayNext));
        Assert.Equal(1, (fresh with { VideoVolume = 7 }).Normalize().VideoVolume);
        Assert.True(JsonSerializer.Deserialize<OverlayState>(
            """{"Id":"11111111-1111-1111-1111-111111111111","Path":"a.mp4","X":0,"Y":0,"Width":200,"Height":100}""")!.VideoAutoPlayNext);
    }

    [Theory]
    [InlineData(VideoBackend.MediaFoundation, "overlay-video.mp4")]
    [InlineData(VideoBackend.Ffmpeg, "overlay-video-vp9.webm")]
    public async Task Without_loop_both_backends_report_the_end_that_triggers_auto_play_next(VideoBackend backend, string fixture)
    {
        using var player = NativeVideoPlayer.TryOpen(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), out _, backend);
        Assert.NotNull(player);
        player!.SetMuted(true);
        player.SetLoop(false);
        Assert.Equal(VideoPlaybackState.Ready, (await player.WaitForReadyAsync(TimeSpan.FromSeconds(10), CancellationToken.None)).State);
        var frame = new byte[16 * 9 * 4];
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!player.GetInfo().Ended && DateTime.UtcNow < deadline) { player.TryReadFrame(frame, 16, 9); await Task.Delay(20); }
        var info = player.GetInfo();
        Assert.True(info.Ended, $"position {info.Position} of {info.Duration}");
        Assert.False(info.Loop);
    }

    [Theory]
    [InlineData(VideoBackend.MediaFoundation, "overlay-video.mp4")]
    [InlineData(VideoBackend.Ffmpeg, "overlay-video-vp9.webm")]
    public async Task Volume_is_applied_and_reported_by_both_backends(VideoBackend backend, string fixture)
    {
        using var player = NativeVideoPlayer.TryOpen(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), out _, backend);
        Assert.NotNull(player);
        player!.SetMuted(true);
        await player.WaitForReadyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        player.SetVolume(.4);
        Assert.Equal(.4, player.GetInfo().Volume, 2);
    }

    [Fact]
    public void Manager_wires_skip_buttons_volume_and_auto_advance()
    {
        var manager = File.ReadAllText(FindSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs"));
        Assert.Contains("SwitchVideoAsync(item.State.Id, -1)", manager, StringComparison.Ordinal);
        Assert.Contains("SwitchVideoAsync(item.State.Id, +1)", manager, StringComparison.Ordinal);
        Assert.Contains("info.Ended && !info.Loop && video.AutoPlayNext", manager, StringComparison.Ordinal);
        Assert.Contains("OverlayVideoChromeLayout.VolumeFraction", manager, StringComparison.Ordinal);
        Assert.Contains("Auto-play next video in folder", manager, StringComparison.Ordinal);
    }

    private static string FindSource(params string[] segments)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(new[] { d.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(Path.Combine(segments));
    }
}
