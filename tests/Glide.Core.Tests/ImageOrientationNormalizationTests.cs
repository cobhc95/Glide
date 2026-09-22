using Glide.Imaging;
using Xunit;

namespace Glide.Core.Tests;

/// <summary>
/// Regression coverage for the "some photos are stretched" bug. The lightweight header probe reports
/// the raw stored JPEG dimensions while the native WIC decode honours the EXIF orientation tag, so
/// an orientation-5..8 photo arrives as a rotated bitmap. The loader must align the reported source
/// size with the bitmap's actual orientation or the viewport stretches it to the wrong aspect ratio.
/// </summary>
public sealed class ImageOrientationNormalizationTests
{
    [Fact]
    public void Transposed_source_is_swapped_to_match_a_rotated_bitmap()
    {
        // 0089.jpg: stored 6016x4016 landscape with EXIF Orientation 8, decoded 4016x6016 portrait.
        var result = ImageLoadCoordinator.NormalizeSourceOrientation(new ImageDimensions(6016, 4016), 4016, 6016);
        Assert.Equal(new ImageDimensions(4016, 6016), result);
    }

    [Fact]
    public void Upright_source_is_left_untouched()
    {
        var result = ImageLoadCoordinator.NormalizeSourceOrientation(new ImageDimensions(6016, 4016), 3000, 2003);
        Assert.Equal(new ImageDimensions(6016, 4016), result);
    }

    [Fact]
    public void Portrait_source_with_portrait_bitmap_is_left_untouched()
    {
        var result = ImageLoadCoordinator.NormalizeSourceOrientation(new ImageDimensions(4016, 6016), 2000, 2994);
        Assert.Equal(new ImageDimensions(4016, 6016), result);
    }

    [Fact]
    public void Square_and_invalid_sources_are_never_swapped()
    {
        Assert.Equal(new ImageDimensions(5000, 5000),
            ImageLoadCoordinator.NormalizeSourceOrientation(new ImageDimensions(5000, 5000), 4016, 6016));
        Assert.Equal(new ImageDimensions(0, 0),
            ImageLoadCoordinator.NormalizeSourceOrientation(new ImageDimensions(0, 0), 4016, 6016));
        Assert.Equal(new ImageDimensions(6016, 4016),
            ImageLoadCoordinator.NormalizeSourceOrientation(new ImageDimensions(6016, 4016), 0, 0));
    }
}
