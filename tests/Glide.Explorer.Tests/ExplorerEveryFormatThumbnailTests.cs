using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Glide.App.Controls;

namespace Glide.Explorer.Tests;

/// <summary>
/// 5.2.0: the Explorer tab shows real thumbnails for formats beyond the original 11 suffixes (the managed decoders and SVG here;
/// the per-format matrix is in Glide.Core.Tests), and a file that is not what its suffix says keeps the file-type glyph.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerEveryFormatThumbnailTests
{
    private static byte[] Ppm(int w, int h)
    {
        var header = Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        var data = new byte[header.Length + w * h * 3];
        header.CopyTo(data, 0);
        for (var i = header.Length; i < data.Length; i += 3) { data[i] = 220; data[i + 1] = 40; data[i + 2] = 40; }
        return data;
    }

    private static byte[] Tga(int w, int h)
    {
        var data = new byte[18 + w * h * 3];
        data[2] = 2;
        data[12] = (byte)w; data[14] = (byte)h;
        data[16] = 24; data[17] = 0x20;
        for (var i = 18; i < data.Length; i += 3) { data[i] = 40; data[i + 1] = 220; data[i + 2] = 40; }
        return data;
    }

    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg" width="60" height="40" viewBox="0 0 60 40"><rect width="60" height="40" fill="#2a60dc"/><path d="M5 35 L30 5 L55 35 Z" fill="#fff"/></svg>""";

    private static void Populate(string root)
    {
        File.WriteAllBytes(Path.Combine(root, "photo.ppm"), Ppm(48, 32));
        File.WriteAllBytes(Path.Combine(root, "texture.tga"), Tga(48, 32));
        File.WriteAllText(Path.Combine(root, "logo.svg"), Svg);
        File.WriteAllBytes(Path.Combine(root, "broken.psd"), [.. "8BPS not a real document"u8, .. new byte[40]]);
        File.WriteAllBytes(Path.Combine(root, "fake.heic"), new byte[64]);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "hello");
    }

    [Fact]
    public async Task Formats_beyond_the_old_eleven_get_thumbnails_and_undecodable_files_keep_the_glyph()
    {
        await ExplorerHarness.Run(async h =>
        {
            foreach (var name in new[] { "photo.ppm", "texture.tga", "logo.svg" })
            {
                var tile = h.Tile(name)!;
                await h.WaitUntil(() => tile.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null), name + " thumbnail decoded");
                var picture = tile.GetVisualDescendants().OfType<Image>().First(i => i.Source is not null);
                Assert.True(picture.Source!.Size.Width > 0 && picture.Source.Size.Width <= 152 + 1, $"{name}: width {picture.Source.Size.Width}");
                Assert.False(tile.GetVisualDescendants().OfType<GlideIconView>().First().IsVisible, name + ": the glyph is replaced by the picture");
            }

            // A file whose content is not what its suffix says, and a corrupt plug-in file, are tried once and keep the glyph.
            foreach (var name in new[] { "broken.psd", "fake.heic" })
            {
                var tile = h.Tile(name)!;
                await h.SettleAsync();
                Assert.All(tile.GetVisualDescendants().OfType<Image>(), i => Assert.Null(i.Source));
                Assert.True(tile.GetVisualDescendants().OfType<GlideIconView>().First().IsVisible, name + ": still the glyph");
            }
        }, Populate);
    }
}
