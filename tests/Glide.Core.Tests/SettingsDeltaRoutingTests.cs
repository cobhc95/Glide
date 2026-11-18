using Glide.App;
using Glide.App.Settings;

namespace Glide.Core.Tests;

/// <summary>Stage C (C2/C3): SettingsDelta ids, SettingsService notifications and the owner routing table.</summary>
[Collection(AvaloniaHeadlessCollection.Name)] // serialised with the window tests: the service state is process-wide
[Trait("Area", "Settings")]
public sealed class SettingsDeltaRoutingTests
{
    [Fact]
    public void Delta_lists_changed_ids_and_flags_changes_without_an_accessor()
    {
        var before = new GlideSettingsState();
        Assert.True(SettingsDelta.Compute(before, before.CloneState()).IsEmpty);

        var after = before.CloneState();
        after.StatusStatZoom = !after.StatusStatZoom;
        after.SlideshowIntervalMs += 500;
        var delta = SettingsDelta.Compute(before, after);
        Assert.Equal(new[] { "status.statZoom", "slideshow.interval" }.OrderBy(x => x), delta.ChangedIds.OrderBy(x => x));
        Assert.False(delta.HasUnmappedChanges);

        var derived = before.CloneState();
        derived.ThemeChoice = "Light";
        derived.LightTheme = true; // derived field, no accessor of its own
        var themed = SettingsDelta.Compute(before, derived);
        Assert.Contains("appearance.theme", themed.ChangedIds);
        Assert.True(themed.HasUnmappedChanges);

        var hotkeys = before.CloneState();
        hotkeys.Hotkeys["nav.next"] = new List<string> { "F12" };
        var unmapped = SettingsDelta.Compute(before, hotkeys);
        Assert.Empty(unmapped.ChangedIds);
        Assert.True(unmapped.HasUnmappedChanges);
    }

    [Fact]
    public void Text_overlay_style_detection_is_unchanged()
    {
        var current = new GlideSettingsState();
        var next = current.CloneState();
        Assert.False(MainWindow.OnlyTextOverlayStyleDiffers(current, next));
        next.PictureCounterFontSize += 2;
        next.PictureCounterColor = "Amber";
        Assert.True(MainWindow.OnlyTextOverlayStyleDiffers(current, next));
        next.ShowScrollbars = !next.ShowScrollbars;
        Assert.False(MainWindow.OnlyTextOverlayStyleDiffers(current, next));
    }

    [Fact]
    public void Every_routed_id_belongs_to_the_owner_whose_handler_lists_it()
    {
        foreach (var (owner, ids) in MainWindow.SettingEffectRoutesForTests)
            foreach (var id in ids)
            {
                Assert.True(SettingAccessors.Values.ContainsKey(id), $"{id}: routed but has no accessor, so it can never be in a delta");
                Assert.Equal(owner, SettingEffectRegistry.For(id).EffectOwner);
            }
    }

    [Fact]
    public void Service_publish_raises_changed_with_the_changed_ids()
    {
        var original = SettingsService.Current;
        var first = new GlideSettingsState();
        SettingsService.Publish(first);
        SettingsChangedEventArgs? seen = null;
        void Handler(object? _, SettingsChangedEventArgs e) => seen = e;
        SettingsService.Changed += Handler;
        try
        {
            var second = first.CloneState();
            second.EscExitsFullscreen = !second.EscExitsFullscreen;
            SettingsService.Publish(second);
            Assert.NotNull(seen);
            Assert.Equal(new[] { "escape.exitFullscreen" }, seen!.Delta.ChangedIds);
            Assert.Equal(second.EscExitsFullscreen, SettingsService.Current!.EscExitsFullscreen);
        }
        finally
        {
            SettingsService.Changed -= Handler;
            if (original is not null) SettingsService.Publish(original); else SettingsService.ResetForTests();
        }
    }
}
