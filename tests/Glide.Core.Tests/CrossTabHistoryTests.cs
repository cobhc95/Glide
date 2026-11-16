using Glide.Core.Navigation;

namespace Glide.Core.Tests;

/// <summary>The pure policy behind Back / Forward across a jump from an image tab to an Explorer tab (Ctrl+E).</summary>
public sealed class CrossTabHistoryTests
{
    private static readonly CrossTabLink Image = new(Guid.NewGuid(), NavigationEntryKind.Image, @"C:\Pics\4K Wallpapers 1 (1).jpg");

    private static NavigationHistory Arrived(params string[] browsedAfter)
    {
        var history = new NavigationHistory();
        history.Record(NavigationEntry.Explorer(@"C:\Older"));
        history.Record(NavigationEntry.Explorer(@"C:\Pics"));
        CrossTabHistory.MarkArrival(history, Image);
        foreach (var folder in browsedAfter) history.Record(NavigationEntry.Explorer(folder));
        return history;
    }

    [Fact]
    public void Default_mode_returns_to_the_image_only_at_the_arrival_folder()
    {
        var history = Arrived(@"C:\Pics\Sub");
        Assert.Equal(CrossTabStepKind.InTab, CrossTabHistory.Decide(history, -1, ExplorerBackReturnModes.Default).Kind);
        history.Back();
        var step = CrossTabHistory.Decide(history, -1, ExplorerBackReturnModes.Default);
        Assert.Equal(CrossTabStepKind.ToOtherTab, step.Kind);
        Assert.Same(Image, step.Link);
        Assert.Equal("Back to 4K Wallpapers 1 (1).jpg", CrossTabHistory.Describe(-1, step.Link!));
    }

    [Fact]
    public void Straight_mode_returns_to_the_image_from_any_folder_browsed_since()
    {
        var history = Arrived(@"C:\Pics\Sub", @"C:\Pics\Sub\Deeper");
        var step = CrossTabHistory.Decide(history, -1, ExplorerBackReturnModes.StraightToPicture);
        Assert.Equal(CrossTabStepKind.ToOtherTab, step.Kind);
        Assert.Equal(1, CrossTabHistory.ArrivalIndex(history, step.Link!));
    }

    [Fact]
    public void Folders_only_mode_is_the_plain_history()
    {
        var history = Arrived();
        Assert.Equal(CrossTabStepKind.InTab, CrossTabHistory.Decide(history, -1, ExplorerBackReturnModes.FoldersOnly).Kind);
        history.Back();
        Assert.Equal(CrossTabStepKind.None, CrossTabHistory.Decide(history, -1, ExplorerBackReturnModes.FoldersOnly).Kind);
    }

    [Fact]
    public void Forward_from_the_returned_image_goes_to_the_Explorer_tab_and_a_new_image_drops_it()
    {
        var image = new NavigationHistory();
        image.Record(NavigationEntry.Image(Image.Path));
        var explorer = new CrossTabLink(Guid.NewGuid(), NavigationEntryKind.Explorer, @"C:\Pics\", Image.Path);
        CrossTabHistory.MarkReturned(image, explorer);
        Assert.True(CrossTabHistory.CanGo(image, 1, ExplorerBackReturnModes.Default));
        Assert.Equal("Forward to Pics", CrossTabHistory.Describe(1, explorer));
        Assert.False(CrossTabHistory.CanGo(image, 1, ExplorerBackReturnModes.FoldersOnly));
        image.Record(NavigationEntry.Image(@"C:\Pics\other.jpg"));            // arrow to the next image: link gone
        Assert.False(CrossTabHistory.CanGo(image, 1, ExplorerBackReturnModes.Default));
    }

    [Fact]
    public void Plain_Explorer_history_has_no_cross_tab_step_and_modes_normalize()
    {
        var history = new NavigationHistory();
        history.Record(NavigationEntry.Explorer(@"C:\A"));
        Assert.False(CrossTabHistory.CanGo(history, -1, null));
        history.Record(NavigationEntry.Explorer(@"C:\B"));
        Assert.Equal(CrossTabStepKind.InTab, CrossTabHistory.Decide(history, -1, "nonsense").Kind);
        Assert.Equal(ExplorerBackReturnModes.Default, ExplorerBackReturnModes.Normalize("nonsense"));
        Assert.Equal(ExplorerBackReturnModes.FoldersOnly, ExplorerBackReturnModes.Normalize("folders ONLY"));
    }
}
