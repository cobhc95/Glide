using Glide.Core.Workspace;
using Xunit;

namespace Glide.Core.Tests;

[Trait("Area", "Shell")]
public sealed class CustomLaunchTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Glide.CustomLaunch", Guid.NewGuid().ToString("N"));

    public CustomLaunchTargetTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Pics"));
        File.WriteAllBytes(Path.Combine(_root, "photo.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
    }

    public void Dispose() { TempDir.TryDelete(_root); }

    [Fact]
    public void Folder_resolves_to_Folder()
    {
        var t = CustomLaunchTarget.Resolve(Path.Combine(_root, "Pics"));
        Assert.Equal(CustomLaunchKind.Folder, t.Kind);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "Pics")), t.Path);
        Assert.Null(t.Notice);
        Assert.True(t.IsUsable);
    }

    [Fact]
    public void Image_file_resolves_to_Image()
    {
        var t = CustomLaunchTarget.Resolve(Path.Combine(_root, "photo.png"));
        Assert.Equal(CustomLaunchKind.Image, t.Kind);
        Assert.Null(t.Notice);
    }

    [Fact]
    public void Missing_path_is_reported_with_a_notice()
    {
        var t = CustomLaunchTarget.Resolve(Path.Combine(_root, "nope"), "Startup custom");
        Assert.Equal(CustomLaunchKind.Missing, t.Kind);
        Assert.False(t.IsUsable);
        Assert.Contains("not found", t.Notice, StringComparison.Ordinal);
        Assert.StartsWith("Startup custom", t.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_image_file_is_unsupported()
    {
        var t = CustomLaunchTarget.Resolve(Path.Combine(_root, "notes.txt"));
        Assert.Equal(CustomLaunchKind.UnsupportedFile, t.Kind);
        Assert.NotNull(t.Notice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_is_NotSet(string? value)
    {
        var t = CustomLaunchTarget.Resolve(value);
        Assert.Equal(CustomLaunchKind.NotSet, t.Kind);
        Assert.NotNull(t.Notice);
    }

    [Fact]
    public void Quoted_and_padded_paths_are_normalised()
    {
        var t = CustomLaunchTarget.Resolve("  \"" + Path.Combine(_root, "Pics") + "\"  ");
        Assert.Equal(CustomLaunchKind.Folder, t.Kind);
    }

    [Fact]
    public void Invalid_characters_do_not_throw()
    {
        var t = CustomLaunchTarget.Resolve("C:\\bad|path?*");
        Assert.False(t.IsUsable);
        Assert.NotNull(t.Notice);
    }
}
