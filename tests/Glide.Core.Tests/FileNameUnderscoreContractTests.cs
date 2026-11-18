namespace Glide.Core.Tests;

// Source-text contract kept: a string Button/MenuItem content becomes AccessText, which treats "_" as a mnemonic and
// drops it from file names ("eTj_0007.JPG" showed as "eTj0007" with an underlined 0). These UI sites must use a
// TextBlock or double the underscore.
[Trait("Area", "Explorer")]
public sealed class FileNameUnderscoreContractTests
{
    [Fact]
    public void Home_recent_rows_use_a_TextBlock_and_middle_click_opens_with_the_middle_click_policy()
    {
        var tabs = ReadSource("src", "Glide.App", "MainWindow.Tabs.cs");
        Assert.Contains("Content = new TextBlock { Text = $\"{(string.Equals(entry.Kind, \"folder\"", tabs);
        Assert.Contains("RecentHistoryItemMiddleReleased", tabs);
        Assert.Contains("ResolveMiddleClickOpening(e.KeyModifiers)", tabs);
    }

    [Fact]
    public void Breadcrumb_and_explorer_menu_names_keep_underscores()
    {
        var crumbs = ReadSource("src", "Glide.App", "Controls", "ExplorerAddressBar.cs");
        Assert.Contains("Content = new TextBlock { Text = text }", crumbs);
        var menus = ReadSource("src", "Glide.App", "MainWindow.ExplorerMenus.cs");
        Assert.Contains(".Replace(\"_\", \"__\")", menus);
        Assert.Contains("Properties of {ExplorerMenuName(", menus);
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return SourceText.Read(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
