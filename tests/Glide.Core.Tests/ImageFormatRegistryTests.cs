using Glide.Core;
using Xunit;

namespace Glide.Core.Tests;

public sealed class ImageFormatRegistryTests
{
    [Fact]
    public void Glide22Registry_Has196UniqueRoutedExtensions_AndTenProtectedCoreExtensions()
    {
        Assert.Equal(196, ImageFormatRegistry.Extensions.Count);
        Assert.Equal(196, ImageFormatRegistry.Extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(10, ImageFormatRegistry.CoreFastPathExtensions.Count);
        Assert.Equal(10, ImageFormatRegistry.CoreFastPathExtensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("test.jpg")] [InlineData("test.jpeg")] [InlineData("test.jpe")] [InlineData("test.png")]
    [InlineData("test.bmp")] [InlineData("test.gif")] [InlineData("test.tif")] [InlineData("test.tiff")]
    [InlineData("test.webp")] [InlineData("test.ico")]
    public void NavigatorAndRegistry_AgreeOnProtectedCoreExtensions(string path)
    {
        Assert.True(ImageFormatRegistry.IsSupported(path));
        Assert.True(ImageFormatRegistry.IsCoreFastPath(path));
        Assert.True(ImageNavigator.IsSupported(path));
    }

    [Theory]
    [InlineData("scan.nii.gz")]
    [InlineData("slide.ome.tiff")]
    [InlineData("camera.cr3")]
    [InlineData("drawing.dwg")]
    [InlineData("document.pdf")]
    public void AllProductContractSuffixes_AreRoutedWithoutPretendingTheyAreCore(string path)
    {
        Assert.True(ImageFormatRegistry.IsSupported(path));
        Assert.False(ImageFormatRegistry.IsCoreFastPath(path));
        Assert.True(ImageNavigator.IsSupported(path));
    }
}
