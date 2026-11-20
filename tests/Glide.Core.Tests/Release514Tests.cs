using Glide.Core.Commands;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

[Trait("Area", "Release514")]
public sealed class Release514Tests
{
    [Fact]
    public void Captured_shortcut_is_applied_only_when_its_key_is_released()
    {
        var gate = new HotkeyCaptureRelease<int>();
        gate.Press(32, "Space");
        Assert.True(gate.IsPending);
        Assert.Null(gate.Release(17));          // another key's key-up does not apply it
        Assert.Equal("Space", gate.Release(32));
        Assert.False(gate.IsPending);
        Assert.Null(gate.Release(32));          // a second key-up applies nothing
        gate.Press(13, "Enter");
        Assert.Equal("Enter", gate.Flush());
        Assert.Null(gate.Flush());
    }

    [Fact]
    public void Bottom_text_overlay_sits_above_the_status_bar_when_it_is_shown()
    {
        Assert.Equal(16, TextOverlayLayout.BottomInset(800, statusBarShown: false, statusBarTop: 740, gap: 16));
        Assert.Equal(76, TextOverlayLayout.BottomInset(800, statusBarShown: true, statusBarTop: 740, gap: 16));
        Assert.Equal(16, TextOverlayLayout.BottomInset(800, statusBarShown: true, statusBarTop: 0, gap: 16)); // not laid out yet
    }
}
