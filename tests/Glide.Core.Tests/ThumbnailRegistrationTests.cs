using Glide.App.Platform;

namespace Glide.Core.Tests;

/// <summary>
/// Guards the Explorer thumbnail registration surface: the extension set is always derived from
/// Glide's authoritative format registry, and the provider CLSID matches the native shim.
/// </summary>
[Trait("Area", "Thumbnails")]
public sealed class ThumbnailRegistrationTests
{
    [Fact]
    public void All_mode_covers_every_supported_extension()
        => Assert.Equal(ImageFormatRegistry.Extensions.Count,
                        WindowsThumbnailRegistration.ExtensionsFor(ThumbnailProviderMode.All).Count);

    [Fact]
    public void Custom_mode_keeps_only_supported_extensions()
    {
        var set = WindowsThumbnailRegistration.ExtensionsFor(
            ThumbnailProviderMode.Custom, new[] { ".tga", ".svg", ".notreal" });
        Assert.Equal(2, set.Count);
        Assert.Contains(".tga", set);
        Assert.Contains(".svg", set);
    }

    [Fact]
    public void Recommended_mode_includes_the_glide_native_formats()
    {
        var set = WindowsThumbnailRegistration.ExtensionsFor(ThumbnailProviderMode.Recommended);
        Assert.Contains(".svg", set);
        Assert.Contains(".tga", set);
        Assert.Contains(".qoi", set);
        // WebP must be owned by Glide even though Windows registers its in-box, WIC-dependent
        // "Photo Thumbnail Provider" for it: without the optional Webp Image Extensions package
        // that handler fails and Explorer would otherwise show a generic icon.
        Assert.Contains(".webp", set);
    }

    [Fact]
    public void Provider_clsid_matches_the_native_shim()
        => Assert.Equal("{6E3C1B2A-9F41-4E7C-9B1E-2C7A5D8F0A31}", WindowsThumbnailRegistration.ProviderClsid);

    [Fact]
    public void Per_user_clsid_pointing_at_another_dll_is_a_stale_shadow()
    {
        Assert.True(WindowsThumbnailRegistration.IsStaleShadow(@"C:\dev\dist\Glide.ShellThumbnail.dll", @"C:\Program Files\Glide\Glide.ShellThumbnail.dll"));
        Assert.False(WindowsThumbnailRegistration.IsStaleShadow(@"c:\program files\glide\Glide.ShellThumbnail.dll", @"C:\Program Files\Glide\Glide.ShellThumbnail.dll"));
        Assert.False(WindowsThumbnailRegistration.IsStaleShadow(null, @"C:\Program Files\Glide\Glide.ShellThumbnail.dll"));
    }

    [Fact]
    public void Settings_defaults_are_safe()
    {
        var defaults = ThumbnailSettings.Default;
        Assert.True(defaults.Enabled);
        Assert.Equal(ThumbnailProviderMode.Recommended, defaults.Mode);
        Assert.Equal("Balanced", defaults.Quality);
        Assert.True(defaults.PreferEmbedded);
    }
}
