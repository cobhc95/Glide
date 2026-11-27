using System.Text;
using Glide.App.Explorer;
using Glide.Core.Settings;
using Glide.Imaging;

namespace Glide.Explorer.Tests;

/// <summary>Round 4 C1/C2: template info under thumbnails, date taken, resolution and size on one line, sort by date taken.</summary>
[Trait("Area", "Explorer")]
public sealed class ExplorerRound4InfoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Glide.Explorer.Round4", Guid.NewGuid().ToString("N"));

    public ExplorerRound4InfoTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A tiny JPEG whose only content is an EXIF block with the given DateTime (no picture data is needed to read it).</summary>
    private string JpegTaken(string name, string? exifDate, DateTime modified)
    {
        var path = Path.Combine(_root, name);
        using (var s = File.Create(path))
        {
            s.Write([0xFF, 0xD8]);
            if (exifDate is not null)
            {
                var tiff = new List<byte>();
                tiff.AddRange("II"u8.ToArray()); tiff.AddRange(BitConverter.GetBytes((ushort)42)); tiff.AddRange(BitConverter.GetBytes(8u));
                tiff.AddRange(BitConverter.GetBytes((ushort)1));
                tiff.AddRange(BitConverter.GetBytes((ushort)0x0132)); tiff.AddRange(BitConverter.GetBytes((ushort)2)); tiff.AddRange(BitConverter.GetBytes(20u)); tiff.AddRange(BitConverter.GetBytes(26u));
                tiff.AddRange(BitConverter.GetBytes(0u));
                tiff.AddRange(Encoding.ASCII.GetBytes(exifDate)); tiff.Add(0);
                var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
                var len = payload.Length + 2;
                s.Write([0xFF, 0xE1, (byte)(len >> 8), (byte)len]);
                s.Write(payload);
            }
            s.Write([0xFF, 0xD9]);
        }
        File.SetLastWriteTime(path, modified);
        return path;
    }

    private static ExplorerEntry Entry(string path) { var f = new FileInfo(path); return new ExplorerEntry(path, false, f.Length, f.LastWriteTime); }

    [Fact]
    public void Date_taken_is_read_from_exif_and_sorts_with_modified_as_fallback()
    {
        var a = Entry(JpegTaken("a.jpg", "2020:05:01 10:00:00", new DateTime(2024, 1, 1)));
        var b = Entry(JpegTaken("b.jpg", "2019:01:01 09:00:00", new DateTime(2024, 6, 1)));
        var c = Entry(JpegTaken("c.jpg", null, new DateTime(2019, 6, 1)));   // no EXIF: its date modified
        Assert.Equal(new DateTime(2020, 5, 1, 10, 0, 0), a.DateTaken);
        Assert.Null(c.DateTaken);
        var byTaken = ExplorerModel.Sort(new[] { a, b, c }, ExplorerSortKey.Taken, ascending: true, foldersFirst: true).Select(e => e.Name);
        Assert.Equal(new[] { "b.jpg", "c.jpg", "a.jpg" }, byTaken);
        var byModified = ExplorerModel.Sort(new[] { a, b, c }, ExplorerSortKey.Modified, ascending: true, foldersFirst: true).Select(e => e.Name);
        Assert.Equal(new[] { "c.jpg", "a.jpg", "b.jpg" }, byModified);
        Assert.Equal("Date taken", ExplorerSortFields.Label(ExplorerSortKey.Taken));
        Assert.True(ExplorerSortFields.NeedsProbe(ExplorerSortKey.Taken));
        Assert.DoesNotContain(ExplorerSortFields.All, f => f.Key == ExplorerSortKey.Taken);   // no Details column
    }

    [Fact]
    public void Resolution_and_size_share_one_line()
    {
        var e = Entry(JpegTaken("r.jpg", null, new DateTime(2024, 1, 2, 3, 4, 5)));
        var info = new ExplorerTileInfo(true, true, true, null, "•");
        Assert.Equal(2, info.Lines);
        var lines = info.Text(e, new ImageDimensions(48, 32));
        Assert.Equal(ExplorerModel.FormatDate(e.Modified), lines[0]);
        Assert.Equal($"48 × 32  •  {ExplorerModel.FormatSize(e.Size)}", lines[1]);
        Assert.Equal(1, new ExplorerTileInfo(false, true, true).Lines);
    }

    [Fact]
    public void Template_info_uses_the_overlay_tokens_and_date_formats()
    {
        var e = Entry(JpegTaken("t.jpg", "2021:07:04 18:30:00", new DateTime(2024, 2, 3, 4, 5, 6)));
        var info = new ExplorerTileInfo(false, false, false, "{w}x{h}{sep}{ext}<br>{modified:yyyy/MM/dd}<br>{taken:yyyy-MM-dd HH:mm}<br>ignored 4th line", "|");
        Assert.Equal(3, info.Lines);
        Assert.True(info.NeedsPixels);
        Assert.True(info.NeedsTaken);
        var lines = info.Text(e, new ImageDimensions(10, 20), e.DateTaken);
        Assert.Equal(new[] { "10x20  |  jpg", "2024/02/03", "2021-07-04 18:30" }, lines);
        // Folders leave picture values empty.
        var folder = new ExplorerEntry(_root, true, 0, new DateTime(2024, 1, 1));
        // (5.1.7 UI test bug 11) ... and leave no stray "x" or separator behind: a part whose tokens are all empty goes whole.
        Assert.Equal("", info.Text(folder, null)[0]);
        var folderLines = info.Text(folder, null);
        Assert.Equal("2024/01/01", folderLines[1]);
        var sizeLine = new ExplorerTileInfo(false, false, false, "{res}{sep}{filesize}<br>{modified:yyyy}", "/");
        Assert.Equal(new[] { "", "2024" }, sizeLine.Text(folder, null));
        Assert.Equal("48\u00d732  /  " + ExplorerModel.FormatSize(e.Size), sizeLine.Text(e, new ImageDimensions(48, 32))[0]);
    }

    [Fact]
    public void Template_settings_are_off_by_default_with_resolution_and_size_on_one_line()
    {
        var s = new GlideSettingsState();
        Assert.False(s.ExplorerInfoUseTemplate);
        Assert.Contains("{res}{sep}{filesize}<br>", s.ExplorerInfoTemplate);
        Assert.Null(ExplorerTileInfo.From(s).Template);
        s.ExplorerInfoUseTemplate = true;
        Assert.Equal(2, ExplorerTileInfo.From(s).Lines);
    }
}
