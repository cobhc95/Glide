using Glide.Core.Renaming;

namespace Glide.Core.Tests;

/// <summary>Issue #4: the batch rename expression language, the checked preview and the remembered inputs.</summary>
[Trait("Area", "Explorer")]
public sealed class BatchRenameCoreTests
{
    private static string One(string expression, string name, bool regex = false, bool matchCase = false, bool isDirectory = false, int index = 0, int count = 1)
    {
        var parsed = BatchRenameExpression.Parse(expression, regex, matchCase);
        Assert.Null(parsed.Error);
        var (stem, extension) = BatchRenamePlanner.Split(name, isDirectory);
        Assert.True(parsed.TryApply(stem, index, count, out var result, out var error), error);
        return result + extension;
    }

    // ------------------------------------------------------------------ templates

    [Theory]
    [InlineData("foo*bar", "Filename.jpg", "fooFilenamebar.jpg")]   // the example of the issue
    [InlineData("foo*", "Filename.jpg", "fooFilename.jpg")]
    [InlineData("*bar", "Filename.jpg", "Filenamebar.jpg")]
    [InlineData("*_*", "a.png", "a_a.png")]
    [InlineData("*", "a.png", "a.png")]
    [InlineData("new", "Filename.jpg", "new.jpg")]
    [InlineData("v2 * final", "my photo.tar.gz", "v2 my photo.tar final.gz")]   // only the last extension is the extension
    public void Templates_wrap_or_replace_the_name_and_keep_the_extension(string expression, string name, string expected) =>
        Assert.Equal(expected, One(expression, name));

    [Fact]
    public void A_name_without_star_numbers_several_items_and_leaves_a_single_item_alone()
    {
        Assert.Equal("holiday (1).jpg", One("holiday", "a.jpg", index: 0, count: 3));
        Assert.Equal("holiday (3).png", One("holiday", "c.png", index: 2, count: 3));
        Assert.Equal("holiday.jpg", One("holiday", "a.jpg", index: 0, count: 1));
        // With a star every name is already different: no counter.
        Assert.Equal("holiday-a.jpg", One("holiday-*", "a.jpg", index: 0, count: 3));
    }

    [Fact]
    public void Folders_have_no_extension_so_the_whole_name_is_the_stem()
    {
        Assert.Equal("old.folder_x", One("*_x", "old.folder", isDirectory: true));
        Assert.Equal(("Pics.2024", ""), BatchRenamePlanner.Split("Pics.2024", isDirectory: true));
    }

    [Theory]
    [InlineData(".gitignore", ".gitignore", "")]
    [InlineData("name.", "name.", "")]
    [InlineData("a.b.c", "a.b", ".c")]
    [InlineData("noext", "noext", "")]
    public void Split_finds_the_extension_like_Explorer(string name, string stem, string extension) =>
        Assert.Equal((stem, extension), BatchRenamePlanner.Split(name, false));

    // ------------------------------------------------------------------ replace

    [Theory]
    [InlineData("IMG/Paris", "IMG_0001.jpg", "Paris_0001.jpg")]
    [InlineData("img/Paris", "IMG_0001.jpg", "Paris_0001.jpg")]   // ignores case by default
    [InlineData("_/", "a_b_c.txt", "abc.txt")]                    // empty replacement deletes
    [InlineData("a/b|b/c", "ab.txt", "cc.txt")]                   // several, in order, one after the other
    [InlineData("jpg/png", "jpg.jpg", "png.jpg")]                 // never touches the extension
    [InlineData("a/b|", "aa.txt", "bb.txt")]                      // a trailing | is ignored
    public void Replace_rules_change_the_name_but_not_the_extension(string expression, string name, string expected) =>
        Assert.Equal(expected, One(expression, name));

    [Fact]
    public void Match_case_makes_replace_and_regex_case_sensitive()
    {
        Assert.Equal("img_1.jpg", One("IMG/x", "img_1.jpg", matchCase: true));
        Assert.Equal("x_1.jpg", One("IMG/x", "img_1.jpg", matchCase: false));
        Assert.Equal("img_1.jpg", One("IMG/x", "img_1.jpg", regex: true, matchCase: true));
        Assert.Equal("x_1.jpg", One("IMG/x", "img_1.jpg", regex: true, matchCase: false));
    }

    [Theory]
    [InlineData("/x", "Type the text to find")]
    [InlineData("a/b/c", "Only one /")]
    [InlineData("a/b|c", "has no /")]
    public void Malformed_replace_rules_explain_themselves(string expression, string message)
    {
        var parsed = BatchRenameExpression.Parse(expression);
        Assert.Contains(message, parsed.Error);
        Assert.False(parsed.TryApply("x", 0, 1, out _, out _));
    }

    // ------------------------------------------------------------------ regex

    [Theory]
    [InlineData(@"(\d+)/day_$1", "IMG_0042.jpg", "IMG_day_0042.jpg")]
    [InlineData(@"^IMG_(\d+)$/photo-$1", "IMG_0042.jpg", "photo-0042.jpg")]
    [InlineData(@"(?<n>\d+)/[${n}]", "a12b.png", "a[12]b.png")]
    [InlineData(@"\s+/_", "a  b   c.txt", "a_b_c.txt")]
    [InlineData(@"\d+", "a12b3.txt", "ab.txt")]                  // no slash: delete what matches
    [InlineData(@"(.*)-(.*)/$2-$1", "left-right.txt", "right-left.txt")]
    public void Regex_replaces_with_groups(string expression, string name, string expected) =>
        Assert.Equal(expected, One(expression, name, regex: true));

    [Fact]
    public void An_invalid_regex_is_reported_not_thrown()
    {
        var parsed = BatchRenameExpression.Parse("(unclosed/x", regex: true);
        Assert.StartsWith("Not a valid regular expression", parsed.Error);
        Assert.Contains("regular expression to find", BatchRenameExpression.Parse("/x", regex: true).Error);
    }

    [Fact]
    public void An_empty_expression_changes_nothing()
    {
        Assert.True(BatchRenameExpression.Parse("").IsEmpty);
        Assert.True(BatchRenameExpression.Parse(null).IsEmpty);
        Assert.Equal("a.jpg", One("", "a.jpg"));
    }

    // ------------------------------------------------------------------ planner

    private static BatchRenameSource F(string name, string folder = @"C:\Pics") => new(Path.Combine(folder, name), false);

    private static BatchRenamePlan Plan(string expression, string[] names, bool regex = false, Func<string, bool>? exists = null) =>
        BatchRenamePlanner.Plan(names.Select(n => F(n)).ToList(), expression, regex, false, exists ?? (_ => false));

    [Fact]
    public void The_preview_lists_every_item_with_its_new_name_in_order()
    {
        var plan = Plan("holiday-*", ["a.jpg", "b.jpg", "c.png"]);
        Assert.True(plan.CanApply);
        Assert.Equal(["holiday-a.jpg", "holiday-b.jpg", "holiday-c.png"], plan.Items.Select(i => i.NewName));
        Assert.Equal([(@"C:\Pics\a.jpg", @"C:\Pics\holiday-a.jpg"), (@"C:\Pics\b.jpg", @"C:\Pics\holiday-b.jpg"), (@"C:\Pics\c.png", @"C:\Pics\holiday-c.png")], plan.Moves);
        Assert.Equal("3 items will be renamed.", plan.Summary);
    }

    [Fact]
    public void Items_whose_name_does_not_change_are_skipped_and_never_conflict()
    {
        var plan = Plan("IMG/Pic", ["IMG_1.jpg", "other.jpg"]);
        Assert.True(plan.CanApply);
        Assert.Equal(1, plan.ChangedCount);
        Assert.Equal([(@"C:\Pics\IMG_1.jpg", @"C:\Pics\Pic_1.jpg")], plan.Moves);
        Assert.False(Plan("zzz/y", ["a.jpg"]).CanApply);   // nothing changes: Rename stays disabled, but nothing is wrong
        Assert.Equal(0, Plan("zzz/y", ["a.jpg"]).ProblemCount);
    }

    [Fact]
    public void Two_items_getting_the_same_name_are_both_flagged_and_disable_Rename()
    {
        var plan = Plan("x/z", ["x1.jpg", "z1.jpg"]);   // x1 -> z1 collides with the unchanged z1
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Items, i => i.Problem == BatchRenameProblem.DuplicateInBatch && i.OldName == "x1.jpg");

        var same = Plan("same", ["a.jpg", "b.png", "c.jpg"]);   // numbered, so no clash
        Assert.True(same.CanApply);
        Assert.Equal(["same (1).jpg", "same (2).png", "same (3).jpg"], same.Items.Select(i => i.NewName));

        // The same new name in different folders is two different files.
        Assert.True(BatchRenamePlanner.PlanNames([F("a.jpg", @"C:\A"), F("b.jpg", @"C:\B")], (_, _) => "x.jpg", _ => false).CanApply);
        Assert.False(BatchRenamePlanner.PlanNames([F("a.jpg"), F("b.jpg")], (_, _) => "x.jpg", _ => false).CanApply);
        var twice = BatchRenamePlanner.Plan([F("a1.jpg"), F("a2.jpg")], @"a\d/b", true, false, _ => false);
        Assert.False(twice.CanApply);
        Assert.All(twice.Items, i => Assert.Equal(BatchRenameProblem.DuplicateInBatch, i.Problem));
    }

    [Fact]
    public void A_name_taken_by_a_file_outside_the_batch_is_flagged_but_one_inside_it_is_free()
    {
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Pics\a.jpg", @"C:\Pics\b.jpg", @"C:\Pics\keep.jpg" };
        var plan = BatchRenamePlanner.Plan([F("a.jpg"), F("b.jpg")], "a/keep", false, false, onDisk.Contains);
        Assert.False(plan.CanApply);
        Assert.Equal(BatchRenameProblem.ExistsOnDisk, plan.Items[0].Problem);

        // A swap: a becomes b and b becomes a. Both names belong to the batch, so both are free (the executor moves them via temporary names).
        var swap = BatchRenamePlanner.PlanNames([F("a.jpg"), F("b.jpg")], (s, _) => s.Path.EndsWith("a.jpg") ? "b.jpg" : "a.jpg", onDisk.Contains);
        Assert.True(swap.CanApply);
        Assert.Equal(["b.jpg", "a.jpg"], swap.Items.Select(i => i.NewName));
    }

    [Fact]
    public void A_case_only_change_is_allowed_even_though_the_name_exists_in_another_case()
    {
        var plan = BatchRenamePlanner.Plan([F("photo.JPG")], "photo/Photo", false, false, p => p.Equals(@"C:\Pics\photo.JPG", StringComparison.OrdinalIgnoreCase));
        Assert.True(plan.CanApply);
        Assert.Equal("Photo.JPG", plan.Items[0].NewName);
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("a|b")]
    [InlineData("what?")]
    public void Illegal_names_are_flagged_in_the_preview(string expression)
    {
        var plan = Plan(expression, ["a.jpg", "b.jpg"]);
        Assert.False(plan.CanApply);
        Assert.All(plan.Items, i => Assert.Equal(BatchRenameProblem.InvalidName, i.Problem));
    }

    [Fact]
    public void Reserved_and_trailing_dot_names_are_flagged()
    {
        Assert.Equal(BatchRenameProblem.InvalidName, Plan("CON", ["a.jpg"]).Items[0].Problem);   // CON.jpg
        Assert.Null(FileNameRules.Validate("fine name.jpg"));
        Assert.NotNull(FileNameRules.Validate(new string('a', 256)));
        Assert.NotNull(FileNameRules.Validate("ends with space "));
        Assert.Equal("ab", FileNameRules.StripInvalid("a:b?"));
    }

    [Fact]
    public void A_broken_expression_is_reported_once_and_disables_Rename()
    {
        var plan = Plan("(x/y", ["a.jpg"], regex: true);
        Assert.False(plan.CanApply);
        Assert.StartsWith("Not a valid regular expression", plan.ExpressionError);
        Assert.Equal(plan.ExpressionError, plan.Summary);
    }

    [Fact]
    public void An_empty_field_gives_a_disabled_Rename_without_complaints()
    {
        var plan = Plan("", ["a.jpg", "b.jpg"]);
        Assert.False(plan.CanApply);
        Assert.Null(plan.ExpressionError);
        Assert.Equal(0, plan.ProblemCount);
        Assert.Null(plan.Summary);
    }

    // ------------------------------------------------------------------ history

    [Fact]
    public void History_keeps_the_newest_first_without_duplicates_and_at_most_thirty()
    {
        IReadOnlyList<RenameHistoryEntry> history = [];
        for (var i = 0; i < 40; i++) history = RenameInputHistory.Add(history, "p" + i, regex: false);
        Assert.Equal(RenameInputHistory.Capacity, history.Count);
        Assert.Equal("p39", history[0].Text);
        Assert.Equal("p10", history[^1].Text);

        history = RenameInputHistory.Add(history, "p20", false);   // used again: moves to the top
        Assert.Equal("p20", history[0].Text);
        Assert.Single(history, e => e.Text == "p20");
        Assert.Equal(RenameInputHistory.Capacity, history.Count);

        history = RenameInputHistory.Add(history, "p20", true);   // the same text as a regex is its own entry
        Assert.Equal(2, history.Count(e => e.Text == "p20"));
        Assert.Same(history, RenameInputHistory.Add(history, "  ", false));
        Assert.Same(history, RenameInputHistory.Add(history, null, false));
    }

    [Fact]
    public void History_round_trips_through_one_settings_string()
    {
        var history = RenameInputHistory.Add(RenameInputHistory.Add([], "IMG/Paris", false), @"(\d+)/day_$1", true);
        var stored = RenameInputHistory.Serialize(history);
        var back = RenameInputHistory.Parse(stored);
        Assert.Equal(history, back);
        Assert.Equal(@"(\d+)/day_$1   (regex)", back[0].Display);
        Assert.Empty(RenameInputHistory.Parse(null));
        Assert.Empty(RenameInputHistory.Parse("garbage\nX\tnope\nT\t"));
        Assert.Single(RenameInputHistory.Parse("T\tkeep\r\nbroken line\r\n"), e => e.Text == "keep");
    }

    [Fact]
    public void Typing_suggests_matching_history_with_prefix_matches_first()
    {
        var history = new[] { "my IMG/Pic", "IMG/Paris", "photo*", "img/x" }.Select(t => new RenameHistoryEntry(t, false)).ToArray();
        var suggestions = RenameInputHistory.Suggest(history, "img").Select(e => e.Text).ToArray();
        Assert.Equal(["IMG/Paris", "img/x", "my IMG/Pic"], suggestions);   // starts with first (in history order), then contains
        Assert.Empty(RenameInputHistory.Suggest(history, ""));
        Assert.Empty(RenameInputHistory.Suggest(history, "zzz"));
        Assert.DoesNotContain("photo*", RenameInputHistory.Suggest(history, "photo*").Select(e => e.Text));   // the exact entry is not offered again
        Assert.Equal(2, RenameInputHistory.Suggest(history, "img", max: 2).Count);
    }
}
