namespace Glide.Core.Tests;

[Trait("Area", "Explorer")]
public sealed class ListingDiffTests
{
    private sealed record Item(string Key, int Version = 0);

    private static List<Item> L(params string[] keys) => keys.Select(k => new Item(k)).ToList();

    private static List<Item> Apply(List<Item> current, List<Item> target, Func<Item, Item, bool>? same = null)
    {
        var list = current.ToList();
        ListingDiff.Compute(current, target, i => i.Key, same).ApplyTo(list);
        return list;
    }

    [Fact]
    public void Removal_drops_only_that_item_and_keeps_the_other_instances()
    {
        var current = L("a", "b", "c", "d");
        var diff = ListingDiff.Compute(current, L("a", "c", "d"), i => i.Key);
        Assert.Equal(new[] { 1 }, diff.RemoveAt);
        Assert.Empty(diff.Inserts);
        var list = current.ToList();
        diff.ApplyTo(list);
        Assert.Same(current[0], list[0]);
        Assert.Same(current[2], list[1]);
        Assert.Same(current[3], list[2]);
    }

    [Fact]
    public void Additions_are_inserted_at_their_sorted_position()
    {
        var current = L("a", "c", "e");
        var target = L("a", "b", "c", "d", "e", "f");
        var diff = ListingDiff.Compute(current, target, i => i.Key);
        Assert.Empty(diff.RemoveAt);
        Assert.Equal(new[] { 1, 3, 5 }, diff.Inserts.Select(x => x.Index));
        var list = Apply(current, target);
        Assert.Equal(target.Select(i => i.Key), list.Select(i => i.Key));
        Assert.Same(current[1], list[2]);
    }

    [Fact]
    public void Rename_is_a_removal_plus_an_insertion()
    {
        var current = L("a", "b", "c");
        var target = L("a", "c", "z");
        var diff = ListingDiff.Compute(current, target, i => i.Key);
        Assert.Equal(new[] { 1 }, diff.RemoveAt);
        Assert.Equal(new[] { 2 }, diff.Inserts.Select(x => x.Index));
        Assert.Equal(new[] { "a", "c", "z" }, Apply(current, target).Select(i => i.Key));
    }

    [Fact]
    public void Keys_match_case_insensitively_like_Windows_paths()
    {
        Assert.True(ListingDiff.Compute(L("A.png", "b.png"), L("a.PNG", "b.png"), i => i.Key).IsEmpty);
    }

    [Fact]
    public void Changed_content_replaces_the_item()
    {
        var current = new List<Item> { new("a"), new("b", 1), new("c") };
        var target = new List<Item> { new("a"), new("b", 2), new("c") };
        var list = Apply(current, target, (x, y) => x.Version == y.Version);
        Assert.Equal(2, list[1].Version);
        Assert.Same(current[0], list[0]);
    }

    [Fact]
    public void A_moved_item_is_moved_with_the_fewest_steps()
    {
        var current = L("a", "b", "c", "d", "e");
        var target = L("a", "c", "d", "e", "b");
        var diff = ListingDiff.Compute(current, target, i => i.Key);
        Assert.Equal(2, diff.OperationCount);
        Assert.Equal(target.Select(i => i.Key), Apply(current, target).Select(i => i.Key));
    }

    [Fact]
    public void Empty_and_identical_lists_are_handled()
    {
        Assert.True(ListingDiff.Compute(L("a", "b"), L("a", "b"), i => i.Key).IsEmpty);
        Assert.Equal(new[] { "x" }, Apply(L(), L("x")).Select(i => i.Key));
        Assert.Empty(Apply(L("x", "y"), L()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Random_changes_always_produce_the_target(int seed)
    {
        var rng = new Random(seed);
        for (var round = 0; round < 200; round++)
        {
            var current = Enumerable.Range(0, rng.Next(0, 30)).Select(i => new Item($"k{i}", rng.Next(2))).OrderBy(_ => rng.Next()).ToList();
            var target = current.Where(_ => rng.Next(4) != 0).Select(i => rng.Next(5) == 0 ? i with { Version = i.Version + 1 } : i)
                .Concat(Enumerable.Range(0, rng.Next(0, 5)).Select(i => new Item($"n{round}_{i}")))
                .OrderBy(_ => rng.Next()).ToList();
            var list = Apply(current, target, (x, y) => x.Version == y.Version);
            Assert.Equal(target.Select(i => (i.Key, i.Version)), list.Select(i => (i.Key, i.Version)));
        }
    }
}
