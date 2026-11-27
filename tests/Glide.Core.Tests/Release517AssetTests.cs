using Glide.App.Platform;
using Glide.Core;

namespace Glide.Core.Tests;

/// <summary>
/// 5.1.7 icon assets (item 20 / 21 of docs/audits/5.1.7-test-coverage.md): the regenerated app icon and the 39 per-extension document
/// icons that ship next to Glide.exe, and the ProgID naming the registration derives from them. Files only: the registry is never
/// touched (RegisterInHive needs a real hive; the naming it uses is the pure functions below).
/// </summary>
[Trait("Area", "Shell")]
public sealed class Release517AssetTests
{

    private sealed record IconFrame(int Width, int Height, int BitCount, int Bytes, int Offset);

    private static (IReadOnlyList<IconFrame> Frames, byte[] Data) ReadIco(string path)
    {
        var data = File.ReadAllBytes(path);
        Assert.True(data.Length > 22, path + " is too small");
        Assert.Equal(0, BitConverter.ToUInt16(data, 0));
        Assert.Equal(1, BitConverter.ToUInt16(data, 2));        // type: icon
        var count = BitConverter.ToUInt16(data, 4);
        var frames = new List<IconFrame>();
        for (var i = 0; i < count; i++)
        {
            var at = 6 + 16 * i;
            var w = data[at] == 0 ? 256 : data[at];
            var h = data[at + 1] == 0 ? 256 : data[at + 1];
            frames.Add(new IconFrame(w, h, BitConverter.ToUInt16(data, at + 6), (int)BitConverter.ToUInt32(data, at + 8), (int)BitConverter.ToUInt32(data, at + 12)));
        }
        return (frames, data);
    }

    private static void AssertFramesAreRealImages(string path, IReadOnlyList<IconFrame> frames, byte[] data)
    {
        foreach (var frame in frames)
        {
            Assert.True(frame.Offset >= 6 + 16 * frames.Count && frame.Offset + frame.Bytes <= data.Length, $"{path} {frame.Width}px frame points outside the file");
            var png = data[frame.Offset] == 0x89 && data[frame.Offset + 1] == 0x50 && data[frame.Offset + 2] == 0x4E && data[frame.Offset + 3] == 0x47;
            var dib = BitConverter.ToUInt32(data, frame.Offset) == 40;   // BITMAPINFOHEADER
            Assert.True(png || dib, $"{path} {frame.Width}px frame is neither PNG nor a DIB");
            Assert.Equal(frame.Width, frame.Height);
        }
    }

    private static string RepositoryFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(segments).ToArray());
    }

    [Fact]
    public void The_app_icon_has_every_size_from_16_to_256_and_each_frame_is_a_real_image()
    {
        var (frames, data) = ReadIco(RepositoryFile("src", "Glide.App", "Assets", "Glide.ico"));
        var sizes = frames.Select(f => f.Width).Distinct().OrderBy(x => x).ToList();
        foreach (var required in new[] { 16, 24, 32, 48, 64, 128, 256 })
            Assert.Contains(required, sizes);
        Assert.Equal(256, sizes.Max());
        Assert.Equal(16, sizes.Min());
        AssertFramesAreRealImages("Glide.ico", frames, data);
    }

    [Fact]
    public void Every_document_icon_ships_with_its_tier_of_sizes_and_each_names_a_format_Glide_registers()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, WindowsFileAssociationRegistration.FileTypeIconFolder);
        var icons = Directory.GetFiles(folder, "*.ico");
        var singleDot = ImageFormatRegistry.Extensions.Count(e => e.LastIndexOf('.') == 0);
        Assert.Equal(singleDot + 1, icons.Length); // one per single-dot extension + generic.ico
        foreach (var icon in icons)
        {
            if (Path.GetFileName(icon) == WindowsFileAssociationRegistration.GenericIconFile) continue;
            var extension = "." + Path.GetFileNameWithoutExtension(icon);
            Assert.Contains(extension, ImageFormatRegistry.Extensions);
            var (frames, data) = ReadIco(icon);
            var sizes = frames.Select(f => f.Width).OrderBy(x => x).ToArray();
            Assert.True(sizes.SequenceEqual(new[] { 16, 32, 48 }) || sizes.SequenceEqual(new[] { 16, 24, 32, 48, 256 }), string.Join(",", sizes));
            AssertFramesAreRealImages(icon, frames, data);
        }
        // The icons the changelog names.
        foreach (var name in new[] { "jpg", "png", "webp" })
            Assert.Contains(icons, f => Path.GetFileName(f) == name + ".ico");
    }

    [Fact]
    public void Registration_names_a_typed_ProgID_only_for_the_extensions_that_have_an_icon()
    {
        var withIcon = new List<string>();
        var without = new List<string>();
        foreach (var extension in ImageFormatRegistry.Extensions)
        {
            var path = WindowsFileAssociationRegistration.FileTypeIconPath(AppContext.BaseDirectory, extension);
            if (path is not null)
            {
                withIcon.Add(extension);
                Assert.Equal(Path.Combine(AppContext.BaseDirectory, "FileTypes", extension.TrimStart('.') + ".ico"), path, ignoreCase: true);
                Assert.Equal("Glide.Image" + extension.ToLowerInvariant(), WindowsFileAssociationRegistration.ProgIdFor(extension, hasTypeIcon: true));
            }
            else
            {
                without.Add(extension);
                Assert.Equal("Glide.Image", WindowsFileAssociationRegistration.ProgIdFor(extension, hasTypeIcon: false));
            }
        }
        Assert.Equal(ImageFormatRegistry.Extensions.Count(e => e.LastIndexOf('.') == 0), withIcon.Count);
        Assert.NotEmpty(without);                                              // other formats keep the generic Glide ProgID and icon
        Assert.All(withIcon, e => Assert.True(WindowsFileAssociationRegistration.IsGlideProgId(WindowsFileAssociationRegistration.ProgIdFor(e, true))));
        Assert.All(without, e => Assert.True(WindowsFileAssociationRegistration.IsGlideProgId(WindowsFileAssociationRegistration.ProgIdFor(e, false))));
    }
}
