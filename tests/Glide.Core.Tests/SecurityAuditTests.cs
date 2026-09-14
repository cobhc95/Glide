using System.Buffers.Binary;
using System.Net;
using Glide.Core.Security;
using Glide.Imaging;

namespace Glide.Core.Tests;

public sealed class SecurityAuditTests : IDisposable
{
    private readonly string _tempCacheDir;

    public SecurityAuditTests()
    {
        _tempCacheDir = Path.Combine(Path.GetTempPath(), "GlideSecurityAuditTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempCacheDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempCacheDir))
                Directory.Delete(_tempCacheDir, recursive: true);
        }
        catch { }
    }

    #region Decompression Bomb / Dimension Defenses

    [Fact]
    public void ImageHeaderProbe_Rejects_Extreme_Dimensions_Decompression_Bomb()
    {
        // Craft a mock PNG header declaring a 100,000 x 100,000 dimension bomb
        var pngBomb = new byte[32];
        // PNG Signature
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(pngBomb, 0);
        // IHDR chunk length (13 bytes)
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(8, 4), 13);
        // IHDR chunk type "IHDR"
        "IHDR"u8.CopyTo(pngBomb.AsSpan(12, 4));
        // Width: 100,000 px, Height: 100,000 px (10 Billion pixels = 40 GB uncompressed)
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(16, 4), 100_000);
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(20, 4), 100_000);

        var probed = ImageHeaderProbe.TryProbe(pngBomb, out var dimensions);

        Assert.False(probed, "Decompression bomb declaring 100,000x100,000 pixels must be rejected by ImageHeaderProbe.");
    }

    [Fact]
    public void ImageHeaderProbe_Rejects_Dimensions_Exceeding_MaxDimension()
    {
        // Craft a PNG declaring 70,000 x 1,000 (exceeds MaxDimension of 65,535)
        var pngBomb = new byte[32];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(pngBomb, 0);
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(pngBomb.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(16, 4), 70_000);
        BinaryPrimitives.WriteInt32BigEndian(pngBomb.AsSpan(20, 4), 1_000);

        var probed = ImageHeaderProbe.TryProbe(pngBomb, out var dimensions);

        Assert.False(probed, "Dimensions exceeding MaxDimension (65,535) must be rejected.");
    }

    [Fact]
    public void ImageHeaderProbe_Accepts_Legitimate_HighResolution_Image()
    {
        // Craft a PNG declaring 8K (7680 x 4320 = ~33 MP, well within 256 MP cap)
        var png8K = new byte[32];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png8K, 0);
        BinaryPrimitives.WriteInt32BigEndian(png8K.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(png8K.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(png8K.AsSpan(16, 4), 7680);
        BinaryPrimitives.WriteInt32BigEndian(png8K.AsSpan(20, 4), 4320);

        var probed = ImageHeaderProbe.TryProbe(png8K, out var dimensions);

        Assert.True(probed, "Valid 8K image must be accepted.");
        Assert.Equal(7680, dimensions.Width);
        Assert.Equal(4320, dimensions.Height);
    }

    [Fact]
    public void ImageLoadCoordinator_IsDecompressionBomb_Identifies_Hostile_Dimensions()
    {
        Assert.True(ImageLoadCoordinator.IsDecompressionBomb(new ImageDimensions(70_000, 100)));
        Assert.True(ImageLoadCoordinator.IsDecompressionBomb(new ImageDimensions(20_000, 20_000))); // 400 MP > 256 MP
        Assert.False(ImageLoadCoordinator.IsDecompressionBomb(new ImageDimensions(16_000, 9_000))); // 144 MP <= 256 MP
        Assert.False(ImageLoadCoordinator.IsDecompressionBomb(new ImageDimensions(3840, 2160))); // 4K UHD
    }

    [Fact]
    public void NativeCompressedBuffer_Rejects_Negative_Or_Excessive_Lengths()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeCompressedBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeCompressedBuffer(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeCompressedBuffer(NativeCompressedBuffer.MaxAllowedBufferBytes + 1));
    }

    #endregion

    #region Network Fetching & SSRF Defenses

    [Theory]
    [InlineData("http://example.com/photo.jpg")]
    [InlineData("https://cdn.example.com/images/gallery/sample.png")]
    [InlineData("https://sub.domain.org:8443/image.webp?token=abc")]
    public void SecureImageFetcher_ValidateUrl_Accepts_Valid_Http_Urls(string url)
    {
        Assert.True(SecureImageFetcher.ValidateUrl(url, out var uri, out var reason), $"URL '{url}' should be valid: {reason}");
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ftp://ftp.example.com/image.jpg")]
    [InlineData("content://media/external/images/media/1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("gopher://gopher.example.com/")]
    [InlineData("ldap://ldap.example.com/cn=users")]
    [InlineData("http://user:password@example.com/image.jpg")] // Rejects userinfo
    [InlineData("/relative/path/image.jpg")] // Rejects relative
    public void SecureImageFetcher_ValidateUrl_Rejects_Disallowed_Schemes_And_Inputs(string url)
    {
        Assert.False(SecureImageFetcher.ValidateUrl(url, out _, out var reason), $"URL '{url}' should be rejected.");
        Assert.NotEmpty(reason);
    }

    [Theory]
    // Loopback
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.254", true)]
    [InlineData("::1", true)]
    // RFC 1918 Private
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.168.254.254", true)]
    // Link-Local / Cloud Metadata (169.254.169.254)
    [InlineData("169.254.169.254", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("fe80::1", true)]
    // CGNAT
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    // Multicast & Broadcast
    [InlineData("224.0.0.1", true)]
    [InlineData("239.255.255.255", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("0.0.0.0", true)]
    // IPv4-mapped IPv6
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    // Public IPs (Allowed)
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("93.184.216.34", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void SecureImageFetcher_IsBlockedIpAddress_Accurately_Filters_Private_And_Public_Ips(string ipString, bool shouldBlock)
    {
        var ip = IPAddress.Parse(ipString);
        var blocked = SecureImageFetcher.IsBlockedIpAddress(ip);
        Assert.Equal(shouldBlock, blocked);
    }

    #endregion

    #region Cache Handling & Path Traversal Defenses

    [Theory]
    [InlineData("../../windows/system32/cmd.exe")]
    [InlineData("..\\..\\secret.txt")]
    [InlineData("sub/../../etc/passwd")]
    [InlineData("https://example.com/../../../../autoexec.bat")]
    [InlineData("C:\\boot.ini")]
    [InlineData("https://example.com/image.png?v=1&foo=bar")]
    public void SecureDiskCache_Neutralizes_Path_Traversal_Keys(string hostileKey)
    {
        var cache = new SecureDiskCache(_tempCacheDir);
        var safePath = cache.GetSafeCachePath(hostileKey, ".png");

        // The safe path must strictly reside within _tempCacheDir
        var canonicalTemp = Path.GetFullPath(_tempCacheDir) + Path.DirectorySeparatorChar;
        Assert.StartsWith(canonicalTemp, safePath, StringComparison.OrdinalIgnoreCase);

        // Filename must be SHA-256 hash plus extension (64 chars hash + 4 chars .png = 68 chars)
        var fileName = Path.GetFileName(safePath);
        Assert.Equal(68, fileName.Length);
        Assert.EndsWith(".png", fileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", fileName);
    }

    [Fact]
    public async Task SecureDiskCache_Write_Read_Delete_Works_Safely()
    {
        var cache = new SecureDiskCache(_tempCacheDir);
        var key = "https://example.com/test_image.jpg";
        var payload = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03, 0x04 };

        // Write
        await cache.WriteAsync(key, payload, ".jpg");

        // Read
        var exists = cache.TryRead(key, out var readBytes, ".jpg");
        Assert.True(exists);
        Assert.NotNull(readBytes);
        Assert.Equal(payload, readBytes);

        // Delete
        var deleted = cache.TryDelete(key, ".jpg");
        Assert.True(deleted);

        // Verify gone
        var existsAfterDelete = cache.TryRead(key, out _, ".jpg");
        Assert.False(existsAfterDelete);
    }

    [Fact]
    public async Task SecureDiskCache_Purge_Removes_Entries()
    {
        var cache = new SecureDiskCache(_tempCacheDir);
        await cache.WriteAsync("k1", new byte[] { 1, 2, 3 });
        await cache.WriteAsync("k2", new byte[] { 4, 5, 6 });

        var purged = cache.Purge();
        Assert.Equal(2, purged);
    }

    #endregion
}
