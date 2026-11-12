using Glide.App.Controls;
using Xunit;

namespace Glide.Core.Tests;

/// <summary>Scroll down must go to the next image and scroll up to the previous one, unless the user inverts it.</summary>
public sealed class WheelDirectionTests
{
    [Theory]
    [InlineData(120, false, -1)]  // scroll up: previous
    [InlineData(-120, false, 1)]  // scroll down: next
    [InlineData(120, true, 1)]    // inverted: scroll up goes next
    [InlineData(-120, true, -1)]  // inverted: scroll down goes previous
    public void Wheel_step_maps_scroll_direction_to_previous_or_next(double deltaY, bool invert, int expected)
    {
        Assert.Equal(expected, ImageViewport.WheelNavigationStep(deltaY, invert));
    }

    [Fact]
    public void Wheel_step_is_never_zero_for_a_real_notch()
    {
        Assert.NotEqual(0, ImageViewport.WheelNavigationStep(0.5, false));
        Assert.NotEqual(0, ImageViewport.WheelNavigationStep(-0.5, true));
    }
}
