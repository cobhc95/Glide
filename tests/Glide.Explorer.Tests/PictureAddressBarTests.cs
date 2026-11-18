using Avalonia.Controls;
using Glide.App.Controls;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>5.1.0 "Show address bar while viewing pictures": the Explorer navigation row above a picture, in its own row.</summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class PictureAddressBarTests
{
    private static Border? Row(ExplorerHarness h) => h.Get<Border?>("_pictureAddressRow");

    [Fact]
    public async Task Off_by_default_and_shown_above_the_picture_with_its_folder_when_on()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.False(h.Settings.ShowAddressBarOverPictures);
            h.DoubleClick(ExplorerHarness.ImageA);
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "picture tab");
            h.Pump();
            Assert.True(Row(h) is null || !Row(h)!.IsVisible);

            h.Call("SetAddressBarOverPictures", true);
            h.Pump();
            var row = Row(h);
            Assert.NotNull(row);
            Assert.True(row!.IsVisible);
            Assert.Equal(0, Grid.GetRow(row));
            var image = h.Window.FindControl<Grid>("ImageView")!;
            Assert.All(image.Children.Where(c => !ReferenceEquals(c, row)), c => Assert.Equal(1, Grid.GetRow(c))); // the picture is not covered
            var bar = h.Get<ExplorerAddressBar>("_pictureAddressBar");
            Assert.Equal(h.Root.TrimEnd('\\'), bar.Path.TrimEnd('\\'), ignoreCase: true);

            // Up opens the folder in the Explorer view of this tab, with the picture selected; the row hides there.
            var tabId = h.Workspace.Active!.Id;
            await (Task)h.Call("OpenPictureFolderInThisTabAsync", true)!;
            await h.SettleAsync();
            Assert.Equal(tabId, h.Workspace.Active!.Id);
            Assert.IsType<BrowserTabState>(h.Workspace.Active);
            Assert.Contains(ExplorerHarness.ImageA, h.SelectedNames);
            Assert.False(row.IsVisible);

            h.Call("SetAddressBarOverPictures", false);
            Assert.False(h.Settings.ShowAddressBarOverPictures);
        });
    }

    [Fact]
    public async Task Row_right_click_menu_toggles_the_setting()
    {
        await ExplorerHarness.Run(async h =>
        {
            var menu = (ContextMenu)h.Call("BuildAddressRowContextMenu")!;
            var item = Assert.Single(menu.Items.OfType<MenuItem>());
            Assert.Equal("Show address bar while viewing pictures", item.Header);
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(h.Settings.ShowAddressBarOverPictures);
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(h.Settings.ShowAddressBarOverPictures);
            await Task.CompletedTask;
        });
    }
}
