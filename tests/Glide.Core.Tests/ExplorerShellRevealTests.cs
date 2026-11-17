using Glide.App.Explorer;
using Xunit;

namespace Glide.Core.Tests;

/// <summary>
/// "Show in Windows File Explorer" from the Explorer tab opened the Documents folder instead of the
/// file's folder: the whole "/select,path" token was quoted by ArgumentList, which explorer.exe cannot
/// parse. The command line must be /select,"full path" (quotes only around the path).
/// </summary>
public sealed class ExplorerShellRevealTests
{
    [Fact]
    public void Selecting_a_file_uses_the_select_switch_with_only_the_path_quoted()
    {
        var args = ExplorerShell.RevealArguments(@"D:\Pictures\Wallpaper 4k\Dark\5sh7n01d9b311.jpg", directory: false);

        Assert.Equal("/select,\"D:\\Pictures\\Wallpaper 4k\\Dark\\5sh7n01d9b311.jpg\"", args);
        Assert.StartsWith("/select,\"", args);
        Assert.False(args.StartsWith("\"/select", StringComparison.Ordinal));
    }

    [Fact]
    public void Opening_a_folder_quotes_just_the_path()
    {
        Assert.Equal("\"D:\\Pictures\\Wallpaper 4k\"", ExplorerShell.RevealArguments(@"D:\Pictures\Wallpaper 4k", directory: true));
    }

    [Fact]
    public void Paths_with_spaces_commas_and_brackets_stay_inside_one_quoted_token()
    {
        var args = ExplorerShell.RevealArguments(@"C:\Pics, Old\4K Wallpapers 1 (2).jpg", directory: false);

        Assert.Equal("/select,\"C:\\Pics, Old\\4K Wallpapers 1 (2).jpg\"", args);
    }
}
