using Glide.Imaging;
using Xunit;

namespace Glide.Core.Tests;

public sealed class ImageHeaderProbeTests
{
    [Theory]
    [InlineData((byte)2)] // uncompressed true-colour TGA
    [InlineData((byte)1)] // colour-mapped TGA
    [InlineData((byte)10)] // RLE true-colour TGA
    public void Headerless_tga_is_not_misdetected_as_ico_or_cur(byte imageType)
    {
        // A TGA header begins idLength, colourMapType, imageType: 00 00 <type> 00. Types 1 and 2
        // previously matched the ICO/CUR signature and produced a bogus 256x256 size.
        var header = new byte[64];
        header[2] = imageType;
        header[12] = 96;   // width
        header[14] = 64;   // height
        header[16] = 24;   // bpp

        Assert.False(ImageHeaderProbe.TryProbe(header, out _));
    }

    [Fact]
    public void Valid_ico_directory_is_still_probed()
    {
        var ico = new byte[64];
        ico[2] = 1;        // type = icon
        ico[4] = 1;        // image count = 1
        ico[6] = 16;       // first image width
        ico[7] = 32;       // first image height

        Assert.True(ImageHeaderProbe.TryProbe(ico, out var dimensions));
        Assert.Equal(new ImageDimensions(16, 32), dimensions);
    }

    [Fact]
    public void Zero_count_ico_directory_is_rejected()
    {
        var ico = new byte[64];
        ico[2] = 2;        // type = cursor
        ico[4] = 0;        // image count = 0 (invalid)

        Assert.False(ImageHeaderProbe.TryProbe(ico, out _));
    }
}
