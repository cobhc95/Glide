using Glide.App.Explorer;
using Glide.App.Renaming;
using Glide.Core.Renaming;
using Glide.Tests.Support;

namespace Glide.Core.Tests;

/// <summary>
/// The rebuilt batch rename dialog's engine (5.2.0, mockup batch-rename-v3): the "New name" template, numbering, capitals, date
/// formats, replace, remove, conflicts, two-phase renames, undo and the saved presets. Pure Core / temp-folder tests.
/// </summary>
[Trait("Area", "Explorer")]
[Collection("BatchRenameExecutor")]
public sealed class RenameTemplateEngineTests : IDisposable
{
    private readonly TempDir _dir = new("glide-renameengine");

    public void Dispose()
    {
        BatchRenameExecutor.MoveEntry = (from, to, isDirectory) => { if (isDirectory) Directory.Move(from, to); else File.Move(from, to); };
        _dir.Dispose();
    }

    private static readonly DateTime Taken = new(2026, 5, 14, 15, 30, 0);
    private static readonly DateTime Modified = new(2025, 1, 2, 3, 4, 5);

    private static BatchRenameSource Src(string name, bool isDirectory = false) => new(Path.Combine(@"C:\Pics", name), isDirectory);

    private static RenameFileFacts Full => new(Taken, Modified, 4000, 3000);

    private static BatchRenamePlan Plan(string[] names, RenameRecipe recipe, Func<BatchRenameSource, RenameFileFacts?>? facts = null, Func<string, bool>? exists = null) =>
        RenamePlanner.Plan(names.Select(n => Src(n)).ToList(), recipe, facts ?? (_ => Full), exists ?? (_ => false));

    private static string[] After(BatchRenamePlan plan) => plan.Items.Select(i => i.NewName).ToArray();

    // ------------------------------------------------------------------ the template string

    [Fact]
    public void A_template_round_trips_text_tokens_formats_and_braces()
    {
        const string template = "{taken:yyyyMMdd} Paris {{trip}} {n}{folder}";
        var pieces = RenameTemplate.Parse(template);
        Assert.Equal([RenameToken.Taken, RenameToken.Number, RenameToken.Folder], pieces.Where(p => p.IsToken).Select(p => p.Token!.Value));
        Assert.Equal("yyyyMMdd", pieces[0].Format);
        Assert.Contains(pieces, p => !p.IsToken && p.Text == " Paris {trip} ");   // a doubled brace is one typed brace
        Assert.Equal(template, RenameTemplate.Serialize(pieces));

        // Typed text that looks like a token never becomes one once it is serialised.
        var typed = RenameTemplate.Serialize([RenamePiece.Literal("{name}"), RenamePiece.Of(RenameToken.Name)]);
        Assert.Equal("{{name}}{name}", typed);
        Assert.Equal([false, true], RenameTemplate.Parse(typed).Select(p => p.IsToken));
        Assert.Equal("{name}", RenameTemplate.Parse(typed)[0].Text);
    }

    [Fact]
    public void Unknown_tokens_stay_as_typed_and_aliases_work()
    {
        Assert.Equal([false], RenameTemplate.Parse("{unknown} {Family;mom}").Select(p => p.IsToken));
        Assert.Equal(RenameToken.Name, RenameTemplate.Parse("{stem}")[0].Token);
        Assert.Equal(RenameToken.Size, RenameTemplate.Parse("{res}")[0].Token);
        Assert.Equal([RenameToken.Width, RenameToken.Height], RenameTemplate.Parse("{w}x{h}").Where(p => p.IsToken).Select(p => p.Token!.Value));
        Assert.Equal("{name}", RenameTemplate.Default);
        Assert.Equal(["Old name", "Number", "Date taken", "Date modified", "Folder name", "Size (W×H)"], RenameTemplate.Tokens.Select(t => t.ChipLabel));
    }

    [Fact]
    public void The_default_template_is_Old_name_so_nothing_changes()
    {
        var plan = Plan(["IMG_1.jpg", "b.png"], new RenameRecipe());
        Assert.Equal(["IMG_1.jpg", "b.png"], After(plan));
        Assert.Equal(0, plan.WillRenameCount);
        Assert.All(plan.Items, i => Assert.False(i.Changed));
    }

    [Fact]
    public void Old_style_patterns_are_only_a_hidden_fallback_and_read_as_before()
    {
        Assert.True(RenameTemplate.IsLegacyPattern("holiday*"));
        Assert.True(RenameTemplate.IsLegacyPattern("foo/bar"));
        Assert.False(RenameTemplate.IsLegacyPattern("holiday {n}"));
        Assert.False(RenameTemplate.IsLegacyPattern("{taken}*"));   // a token makes it an ordinary template
        Assert.Equal(["fooabar.jpg"], After(Plan(["a.jpg"], new RenameRecipe { Template = "foo*bar" })));
        Assert.Equal(["x.jpg", "y.jpg"], After(Plan(["a.jpg", "b.jpg"], new RenameRecipe { Template = "a/x|b/y" })));
        Assert.Equal(["day_1.jpg"], After(Plan(["IMG_1.jpg"], new RenameRecipe { Template = @"IMG_(\d+)/day_$1", UseRegex = true })));
    }

    // ------------------------------------------------------------------ numbers

    [Theory]
    [InlineData(1, 1, "1", "2", "3")]
    [InlineData(1, 2, "01", "02", "03")]
    [InlineData(1, 3, "001", "002", "003")]
    [InlineData(7, 4, "0007", "0008", "0009")]
    [InlineData(0, 3, "000", "001", "002")]
    public void Numbers_start_where_asked_and_use_the_chosen_look(int start, int digits, string first, string second, string third)
    {
        var plan = Plan(["a.jpg", "b.jpg", "c.jpg"], new RenameRecipe { Template = "Trip {n}", NumberStart = start, NumberDigits = digits });
        Assert.Equal([$"Trip {first}.jpg", $"Trip {second}.jpg", $"Trip {third}.jpg"], After(plan));
    }

    [Fact]
    public void A_number_past_the_look_keeps_all_its_digits()
    {
        var plan = Plan(["a.jpg"], new RenameRecipe { Template = "{n}", NumberStart = 12345, NumberDigits = 3 });
        Assert.Equal(["12345.jpg"], After(plan));
    }

    [Fact]
    public void The_extension_is_kept_and_a_folder_has_none()
    {
        var plan = RenamePlanner.Plan([Src("a.tar.gz"), Src("Pics 2024", true)], new RenameRecipe { Template = "x{n}" }, _ => Full, _ => false);
        Assert.Equal(["x1.gz", "x2"], After(plan));
    }

    // ------------------------------------------------------------------ capitals

    [Theory]
    [InlineData(RenameCase.Keep, "iMg_4471 pARIS trip")]
    [InlineData(RenameCase.Lower, "img_4471 paris trip")]
    [InlineData(RenameCase.Upper, "IMG_4471 PARIS TRIP")]
    [InlineData(RenameCase.Title, "Img_4471 Paris Trip")]
    [InlineData(RenameCase.Sentence, "Img_4471 paris trip")]
    public void Capitals_change_the_letters(RenameCase mode, string expected) =>
        Assert.Equal(expected, RenameCaseRules.Apply("iMg_4471 pARIS trip", mode));

    [Fact]
    public void Title_case_leaves_letters_inside_words_and_after_digits_alone()
    {
        Assert.Equal("Don't Stop 4k Video", RenameCaseRules.Apply("DON'T stop 4K video", RenameCase.Title));
        Assert.Equal("", RenameCaseRules.Apply("", RenameCase.Title));
    }

    [Fact]
    public void Capitals_apply_to_the_whole_name_but_not_the_extension_option()
    {
        var plan = Plan(["PARIS TRIP.JPG"], new RenameRecipe { Template = "{name}", Case = RenameCase.Title });
        Assert.Equal(["Paris Trip.JPG"], After(plan));
        var lower = Plan(["a.JPG"], new RenameRecipe { Template = "x{name}", Extension = RenameExtension.Lower });
        Assert.Equal(["xa.jpg"], After(lower));
        var upper = Plan(["a.jpg"], new RenameRecipe { Template = "x{name}", Extension = RenameExtension.Upper });
        Assert.Equal(["xa.JPG"], After(upper));
    }

    // ------------------------------------------------------------------ dates

    [Theory]
    [InlineData("yyyy-MM-dd", "2026-05-14")]
    [InlineData("dd-MM-yyyy", "14-05-2026")]
    [InlineData("yyyyMMdd", "20260514")]
    [InlineData("yyyy-MM-dd HH.mm", "2026-05-14 15.30")]
    [InlineData("YYYY.DD.MM", "2026.14.05")]   // Excel-style letters, like the text overlay templates
    [InlineData("", "2026-05-14")]             // blank: the default
    public void Date_formats_write_the_date_taken_in_the_chosen_way(string format, string expected)
    {
        var plan = Plan(["a.jpg"], new RenameRecipe { Template = "{taken}", DateFormat = format });
        Assert.Equal([expected + ".jpg"], After(plan));
    }

    [Fact]
    public void The_date_format_picker_offers_the_mockup_examples()
    {
        Assert.Equal(["2026-05-14", "14-05-2026", "20260514", "2026-05-14 15.30"], RenameDateFormats.Presets.Select(RenameDateFormats.Example));
    }

    [Fact]
    public void A_token_can_carry_its_own_format_and_the_modified_date_is_separate()
    {
        var plan = Plan(["a.jpg"], new RenameRecipe { Template = "{taken:yyyy} {modified:dd.MM.yyyy} {modified}" });
        Assert.Equal(["2026 02.01.2025 2025-01-02.jpg"], After(plan));
    }

    [Fact]
    public void A_date_is_written_the_same_in_every_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            try { System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH"); }   // Buddhist calendar
            catch (System.Globalization.CultureNotFoundException) { return; }
            Assert.Equal("2026-05-14", RenameTemplate.FormatDate(Taken, "yyyy-MM-dd"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void An_item_without_the_date_is_skipped_not_a_problem_and_numbers_go_to_the_others()
    {
        RenameFileFacts? Facts(BatchRenameSource s) => Path.GetFileName(s.Path) == "notes.png" ? new RenameFileFacts(null, Modified, 10, 10) : Full;
        var plan = Plan(["a.jpg", "notes.png", "b.jpg"], new RenameRecipe { Template = "{taken} {n}", NumberDigits = 3 }, Facts);
        Assert.Equal(["2026-05-14 001.jpg", "notes.png", "2026-05-14 002.jpg"], After(plan));
        var skipped = plan.Items[1];
        Assert.True(skipped.Skipped);
        Assert.Equal("no date taken", skipped.SkipReason);
        Assert.False(skipped.HasProblem);
        Assert.False(skipped.Changed);
        Assert.Equal(2, plan.WillRenameCount);
        Assert.Equal(0, plan.ProblemCount);
    }

    [Fact]
    public void A_size_piece_needs_the_picture_size()
    {
        var plan = Plan(["a.jpg", "b.txt"], new RenameRecipe { Template = "{name} {size}" }, s => Path.GetFileName(s.Path) == "b.txt" ? RenameFileFacts.Empty : Full);
        Assert.Equal(["a 4000x3000.jpg", "b.txt"], After(plan));
        Assert.Equal("no picture size", plan.Items[1].SkipReason);
    }

    [Fact]
    public void The_folder_piece_is_the_name_of_the_folder_the_file_is_in()
    {
        Assert.Equal(["Pics_a.jpg"], After(Plan(["a.jpg"], new RenameRecipe { Template = "{folder}_{name}" })));
    }

    [Fact]
    public void Numbers_follow_the_chosen_sort_order()
    {
        var taken = new Dictionary<string, DateTime> { ["a.jpg"] = new(2026, 3, 1), ["b.jpg"] = new(2026, 1, 1), ["c.jpg"] = new(2026, 2, 1) };
        var modified = new Dictionary<string, DateTime> { ["a.jpg"] = new(2020, 1, 1), ["b.jpg"] = new(2022, 1, 1), ["c.jpg"] = new(2021, 1, 1) };
        RenameFileFacts? Facts(BatchRenameSource s) => new(taken[Path.GetFileName(s.Path)], modified[Path.GetFileName(s.Path)], 1, 1);
        string[] names = ["a.jpg", "b.jpg", "c.jpg"];

        var current = Plan(names, new RenameRecipe { Template = "x{n}" }, Facts);
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], current.Items.Select(i => i.OldName));
        var byTaken = Plan(names, new RenameRecipe { Template = "x{n}", Sort = RenameSort.DateTaken }, Facts);
        Assert.Equal(["b.jpg", "c.jpg", "a.jpg"], byTaken.Items.Select(i => i.OldName));   // rows come in numbering order
        Assert.Equal(["x1.jpg", "x2.jpg", "x3.jpg"], After(byTaken));
        var byModified = Plan(names, new RenameRecipe { Template = "x{n}", Sort = RenameSort.DateModified }, Facts);
        Assert.Equal(["a.jpg", "c.jpg", "b.jpg"], byModified.Items.Select(i => i.OldName));
        var byName = Plan(["img10.jpg", "img2.jpg", "img1.jpg"], new RenameRecipe { Template = "x{n}", Sort = RenameSort.Name });
        Assert.Equal(["img1.jpg", "img2.jpg", "img10.jpg"], byName.Items.Select(i => i.OldName));   // natural name order
    }

    // ------------------------------------------------------------------ replace, remove

    [Fact]
    public void Replace_is_plain_text_and_ignores_case_until_Match_case_is_ticked()
    {
        Assert.Equal(["photo_4471.jpg"], After(Plan(["IMG_4471.jpg"], new RenameRecipe { Find = "img", Replace = "photo" })));
        Assert.Equal(["IMG_4471.jpg"], After(Plan(["IMG_4471.jpg"], new RenameRecipe { Find = "img", Replace = "photo", MatchCase = true })));
        Assert.Equal(["a.b-c.jpg"], After(Plan(["a.b c.jpg"], new RenameRecipe { Find = " ", Replace = "-" })));
        Assert.Equal(["ax.jpg"], After(Plan(["a(1).jpg"], new RenameRecipe { Find = "(1)", Replace = "x" })));   // no regex characters leak into plain text
        Assert.Equal(["ab.jpg"], After(Plan(["a.b.jpg"], new RenameRecipe { Find = ".", Replace = "" })));   // an empty replacement deletes
    }

    [Fact]
    public void Replace_can_be_a_regular_expression_and_a_bad_one_is_an_error_not_a_crash()
    {
        Assert.Equal(["day_0042.jpg"], After(Plan(["IMG_0042.jpg"], new RenameRecipe { Find = @"img_(\d+)", Replace = "day_$1", UseRegex = true })));
        var bad = Plan(["a.jpg"], new RenameRecipe { Find = "(broken", Replace = "x", UseRegex = true });
        Assert.StartsWith("Not a valid regular expression", bad.ExpressionError);
        Assert.Equal(0, bad.WillRenameCount);
    }

    [Fact]
    public void Remove_cuts_characters_from_the_old_name_and_tidies_spaces()
    {
        Assert.Equal(["4471.jpg"], After(Plan(["IMG_4471.jpg"], new RenameRecipe { RemoveFirst = 4 })));
        Assert.Equal(["IMG.jpg"], After(Plan(["IMG_4471.jpg"], new RenameRecipe { RemoveLast = 5 })));
        Assert.Equal(["IMG_.jpg"], After(Plan(["IMG_4471.jpg"], new RenameRecipe { RemoveDigits = true })));
        Assert.Equal(["a b.jpg"], After(Plan(["  a    b  .jpg"], new RenameRecipe { RemoveSpaces = true })));
        // Removing more than there is leaves nothing: an empty name is a problem, not an exception.
        var empty = Plan(["abc.jpg"], new RenameRecipe { RemoveFirst = 50 });
        Assert.Equal(BatchRenameProblem.InvalidName, empty.Items[0].Problem);
        // Remove acts on the old name before the template, so the number is not cut.
        Assert.Equal(["4471_1.jpg"], After(Plan(["IMG_4471.jpg"], new RenameRecipe { Template = "{name}_{n}", RemoveFirst = 4 })));
    }

    [Fact]
    public void A_whole_name_replaced_in_one_go_keeps_a_working_combination()
    {
        var plan = Plan(["IMG_4471.jpg"], new RenameRecipe { Template = "{taken} Paris trip {n}", NumberDigits = 3, Find = "paris", Replace = "Rome", Case = RenameCase.Upper });
        Assert.Equal(["2026-05-14 ROME TRIP 001.jpg"], After(plan));
    }

    // ------------------------------------------------------------------ highlighted pieces

    [Fact]
    public void The_token_parts_are_marked_in_the_new_name()
    {
        var plan = Plan(["IMG_4471.jpg"], new RenameRecipe { Template = "{taken} Paris trip {n}", NumberDigits = 3 });
        var segments = plan.Items[0].Segments!;
        Assert.Equal([("2026-05-14", true), (" Paris trip ", false), ("001", true), (".jpg", false)], segments.Select(s => (s.Text, s.IsToken)));
    }

    [Fact]
    public void The_marks_follow_the_text_through_replace_spaces_and_capitals()
    {
        var plan = Plan(["a.jpg"], new RenameRecipe { Template = "x  {taken}  y {n}", Find = "x", Replace = "LONGER", RemoveSpaces = true, Case = RenameCase.Lower });
        Assert.Equal(["longer 2026-05-14 y 1.jpg"], After(plan));
        var marked = plan.Items[0].Segments!.Where(s => s.IsToken).Select(s => s.Text);
        Assert.Equal(["2026-05-14", "1"], marked);
        // The marked text is exactly the piece, whatever happened before it.
        var shrink = Plan(["a.jpg"], new RenameRecipe { Template = "aaaa{n}aaaa", Find = "aaaa", Replace = "b" });
        Assert.Equal(["b1b.jpg"], After(shrink));
        Assert.Equal(["1"], shrink.Items[0].Segments!.Where(s => s.IsToken).Select(s => s.Text));
    }

    [Fact]
    public void Rows_with_a_problem_or_no_change_have_no_marks()
    {
        var plan = Plan(["a.jpg"], new RenameRecipe { Template = "{n}" }, exists: p => p.EndsWith("1.jpg", StringComparison.Ordinal));
        Assert.True(plan.Items[0].HasProblem);
        Assert.Null(plan.Items[0].Segments);
        Assert.Null(Plan(["a.jpg"], new RenameRecipe()).Items[0].Segments);
    }

    // ------------------------------------------------------------------ conflicts

    [Fact]
    public void A_name_taken_by_a_file_outside_the_batch_is_a_problem_and_the_others_still_go()
    {
        var plan = Plan(["a.jpg", "b.jpg", "c.jpg"], new RenameRecipe { Template = "Trip {n}" }, exists: p => p.EndsWith("Trip 2.jpg", StringComparison.Ordinal));
        Assert.Equal(["Trip 1.jpg", "Trip 2.jpg", "Trip 3.jpg"], After(plan));
        Assert.Equal([BatchRenameProblem.None, BatchRenameProblem.ExistsOnDisk, BatchRenameProblem.None], plan.Items.Select(i => i.Problem));
        Assert.Equal(2, plan.WillRenameCount);
        Assert.Equal(1, plan.ProblemCount);
        Assert.Equal([(Src("a.jpg").Path, Path.Combine(@"C:\Pics", "Trip 1.jpg")), (Src("c.jpg").Path, Path.Combine(@"C:\Pics", "Trip 3.jpg"))], plan.SafeMoves);
        Assert.Contains("already a file or folder", plan.Items[1].Message);
    }

    [Fact]
    public void Two_new_names_that_are_the_same_both_get_a_problem()
    {
        var plan = Plan(["a.jpg", "b.jpg", "c.png"], new RenameRecipe { Template = "same" });
        Assert.Equal([BatchRenameProblem.DuplicateInBatch, BatchRenameProblem.DuplicateInBatch, BatchRenameProblem.None], plan.Items.Select(i => i.Problem));   // c.png is "same.png"
        Assert.Equal(1, plan.WillRenameCount);
    }

    [Fact]
    public void An_illegal_name_is_a_problem()
    {
        var plan = Plan(["a.jpg"], new RenameRecipe { Template = "bad:name" });
        Assert.Equal(BatchRenameProblem.InvalidName, plan.Items[0].Problem);
        Assert.Equal(0, plan.WillRenameCount);
        Assert.Empty(plan.SafeMoves);
    }

    [Fact]
    public void An_item_that_keeps_its_name_because_of_a_problem_blocks_the_names_that_want_it()
    {
        // 1.jpg -> 2.jpg is blocked by a file outside the batch, so 1.jpg stays 1.jpg; then 2.jpg -> 1.jpg would collide with it.
        var plan = RenamePlanner.Plan([Src("1.jpg"), Src("2.jpg")], new RenameRecipe { Template = "{n}", NumberStart = 2 }, _ => Full,
            p => p.EndsWith("3.jpg", StringComparison.Ordinal));
        // numbering: 1.jpg -> 2.jpg, 2.jpg -> 3.jpg (exists). 2.jpg is held, so 1.jpg -> 2.jpg now clashes with 2.jpg's own name.
        Assert.Equal([BatchRenameProblem.DuplicateInBatch, BatchRenameProblem.ExistsOnDisk], plan.Items.Select(i => i.Problem));
        Assert.Equal(0, plan.WillRenameCount);
        Assert.Empty(plan.SafeMoves);
    }

    [Fact]
    public void Nothing_in_the_safe_moves_can_run_into_a_name_a_problem_item_keeps()
    {
        var names = new[] { "a.jpg", "b.jpg", "c.jpg", "d.jpg" };
        var plan = Plan(names, new RenameRecipe { Template = "{n}", NumberStart = 1 }, exists: p => p.EndsWith("2.jpg", StringComparison.Ordinal) && !names.Contains("2.jpg"));
        var finals = plan.Items.Select(i => i.WillRename ? i.NewName : i.OldName).ToList();
        Assert.Equal(finals.Count, finals.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ------------------------------------------------------------------ two-phase renames on disk

    private string Make(string name, string? content = null)
    {
        var path = _dir.Combine(name);
        File.WriteAllText(path, content ?? "content of " + name);
        return path;
    }

    private string[] Names() => Directory.GetFileSystemEntries(_dir.Path).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    [Fact]
    public void A_swap_planned_by_the_engine_runs_through_temporary_names()
    {
        Make("1.jpg"); Make("2.jpg");
        // The view lists 2.jpg first, so numbering swaps the two names.
        var sources = new[] { new BatchRenameSource(_dir.Combine("2.jpg"), false), new BatchRenameSource(_dir.Combine("1.jpg"), false) };
        var plan = RenamePlanner.Plan(sources, new RenameRecipe { Template = "{n}" }, _ => Full, p => File.Exists(p) || Directory.Exists(p));
        Assert.Equal(2, plan.WillRenameCount);
        Assert.Equal(0, plan.ProblemCount);
        var result = BatchRenameExecutor.Execute(plan.SafeMoves);
        Assert.True(result.Success, result.Error);
        Assert.Equal("content of 2.jpg", File.ReadAllText(_dir.Combine("1.jpg")));
        Assert.Equal("content of 1.jpg", File.ReadAllText(_dir.Combine("2.jpg")));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }

    [Fact]
    public void A_cycle_of_three_works_too()
    {
        var a = Make("a.jpg"); var b = Make("b.jpg"); var c = Make("c.jpg");
        var result = BatchRenameExecutor.Execute([(a, b), (b, c), (c, a)]);
        Assert.True(result.Success, result.Error);
        Assert.Equal("content of c.jpg", File.ReadAllText(a));
        Assert.Equal("content of a.jpg", File.ReadAllText(b));
        Assert.Equal("content of b.jpg", File.ReadAllText(c));
    }

    // ------------------------------------------------------------------ undo

    [Fact]
    public void Undo_turns_the_last_batch_around()
    {
        var done = new[] { ("C:\\x\\a.jpg", "C:\\x\\b.jpg"), ("C:\\x\\c.jpg", "C:\\x\\d.jpg") };
        Assert.Equal([("C:\\x\\b.jpg", "C:\\x\\a.jpg"), ("C:\\x\\d.jpg", "C:\\x\\c.jpg")], RenameUndo.Invert(done));
    }

    [Fact]
    public void Undo_puts_every_name_back_after_a_swap_and_after_a_chain()
    {
        var a = Make("a.jpg"); var b = Make("b.jpg");
        var swap = new[] { (a, b), (b, a) };
        Assert.True(BatchRenameExecutor.Execute(swap).Success);
        Assert.Equal("content of a.jpg", File.ReadAllText(b));
        Assert.Null(RenameUndo.Check(swap, p => File.Exists(p)));
        var undone = BatchRenameExecutor.Execute(RenameUndo.Invert(swap));
        Assert.True(undone.Success, undone.Error);
        Assert.Equal("content of a.jpg", File.ReadAllText(a));
        Assert.Equal("content of b.jpg", File.ReadAllText(b));

        // a -> b -> c chain (b moves on, a takes its place), then undo.
        var c = _dir.Combine("c.jpg");
        var chain = new[] { (b, c), (a, b) };
        Assert.True(BatchRenameExecutor.Execute(chain).Success);
        Assert.Equal(["b.jpg", "c.jpg"], Names());
        Assert.Null(RenameUndo.Check(chain, p => File.Exists(p)));
        Assert.True(BatchRenameExecutor.Execute(RenameUndo.Invert(chain)).Success);
        Assert.Equal("content of a.jpg", File.ReadAllText(a));
        Assert.Equal("content of b.jpg", File.ReadAllText(b));
        Assert.Equal(["a.jpg", "b.jpg"], Names());
    }

    [Fact]
    public void Undo_refuses_when_a_file_is_gone_or_its_old_name_is_taken_again_and_changes_nothing()
    {
        var a = Make("a.jpg");
        var b = _dir.Combine("b.jpg");
        Assert.True(BatchRenameExecutor.Execute([(a, b)]).Success);
        var done = new[] { (a, b) };
        Assert.Null(RenameUndo.Check(done, p => File.Exists(p)));

        Make("a.jpg", "someone else's file");   // the old name is taken again
        Assert.Contains("exists again", RenameUndo.Check(done, p => File.Exists(p)));
        var blocked = BatchRenameExecutor.Execute(RenameUndo.Invert(done));
        Assert.False(blocked.Success);
        Assert.Equal("someone else's file", File.ReadAllText(a));
        Assert.Equal("content of a.jpg", File.ReadAllText(b));

        File.Delete(a);
        File.Delete(b);
        Assert.Contains("no longer there", RenameUndo.Check(done, p => File.Exists(p)));
        Assert.Equal("There is nothing to undo.", RenameUndo.Check([], p => true));
    }

    // ------------------------------------------------------------------ presets

    [Fact]
    public void Presets_keep_every_box_when_saved_and_loaded()
    {
        var recipe = new RenameRecipe
        {
            Template = "{taken} Paris {{x}} {n}", Find = "IMG", Replace = "photo", MatchCase = true, UseRegex = true, NumberStart = 5, NumberDigits = 3,
            Case = RenameCase.Title, DateFormat = "dd-MM-yyyy", RemoveFirst = 2, RemoveLast = 3, RemoveSpaces = true, RemoveDigits = true,
            Extension = RenameExtension.Upper, Sort = RenameSort.DateTaken
        };
        var presets = RenamePresets.Save([], "Holiday photos", recipe);
        var json = RenamePresets.Serialize(presets);
        var loaded = RenamePresets.Parse(json);
        Assert.Equal("Holiday photos", Assert.Single(loaded).Name);
        Assert.Equal(recipe, loaded[0].Recipe);
    }

    [Fact]
    public void Saving_under_a_name_that_exists_replaces_it_in_place_and_new_ones_go_first()
    {
        var p = RenamePresets.Save([], "One", new RenameRecipe { Template = "1" });
        p = RenamePresets.Save(p, "Two", new RenameRecipe { Template = "2" });
        Assert.Equal(["Two", "One"], p.Select(x => x.Name));
        p = RenamePresets.Save(p, "  one ", new RenameRecipe { Template = "1b" });
        Assert.Equal(["Two", "one"], p.Select(x => x.Name));
        Assert.Equal("1b", RenamePresets.Find(p, "ONE")!.Recipe.Template);
        p = RenamePresets.Delete(p, "two");
        Assert.Equal(["one"], p.Select(x => x.Name));
        Assert.Equal(p, RenamePresets.Save(p, "   ", new RenameRecipe()));   // a blank name saves nothing
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"presets\": 5}")]
    [InlineData("{\"presets\": [1, \"x\", {}]}")]
    [InlineData("[]")]
    public void Damaged_preset_text_gives_no_presets_and_no_error(string json) => Assert.Empty(RenamePresets.Parse(json));

    [Fact]
    public void A_preset_with_missing_or_odd_values_is_read_with_defaults_and_clamped()
    {
        var loaded = RenamePresets.Parse("{\"presets\":[{\"name\":\"x\",\"digits\":99,\"start\":-4,\"case\":\"Nonsense\",\"removeFirst\":100000}]}");
        var r = Assert.Single(loaded).Recipe;
        Assert.Equal(RenameTemplate.Default, r.Template);
        Assert.Equal(9, r.NumberDigits);
        Assert.Equal(0, r.NumberStart);
        Assert.Equal(RenameCase.Keep, r.Case);
        Assert.Equal(RenameRecipe.MaxRemove, r.RemoveFirst);
    }

    [Fact]
    public void Presets_survive_the_settings_file()
    {
        var state = new Glide.Core.Settings.GlideSettingsState();
        Assert.Equal("", state.BatchRenamePresets);
        state.BatchRenamePresets = RenamePresets.Serialize(RenamePresets.Save([], "Holiday photos", new RenameRecipe { Template = "{taken} {n}" }));
        var json = System.Text.Json.JsonSerializer.Serialize(state, Glide.App.AppJsonContext.Default.GlideSettingsState);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, Glide.App.AppJsonContext.Default.GlideSettingsState)!;
        Assert.Equal("{taken} {n}", Assert.Single(RenamePresets.Parse(back.BatchRenamePresets)).Recipe.Template);
    }

    // ------------------------------------------------------------------ facts: read once per file

    [Fact]
    public void Dates_and_sizes_are_read_once_per_file_and_only_when_needed()
    {
        var cache = new RenameFactsCache { TakenProbe = _ => Taken, ModifiedProbe = _ => Modified, SizeProbe = _ => (40, 30) };
        var sources = new[] { Src("a.jpg"), Src("b.jpg"), Src("n.txt") };
        Assert.True(cache.IsReady(sources, taken: false, modified: false, size: false));   // nothing wanted: nothing to read
        Assert.False(cache.IsReady(sources, taken: true, modified: false, size: false));

        cache.Load(sources, taken: true, modified: false, size: false);
        Assert.Equal(2, cache.ProbeCount);   // the .txt is not a picture: no EXIF read
        Assert.True(cache.IsReady(sources, true, false, false));
        Assert.False(cache.IsReady(sources, true, true, false));
        cache.Load(sources, taken: true, modified: false, size: false);
        Assert.Equal(2, cache.ProbeCount);   // already known

        cache.Load(sources, taken: true, modified: true, size: true);
        Assert.Equal(2 + 3 + 2, cache.ProbeCount);   // modified for all three, size for the two pictures
        Assert.Equal(new RenameFileFacts(Taken, Modified, 40, 30), cache.Get(sources[0]));
        Assert.Equal(new RenameFileFacts(null, Modified, 0, 0), cache.Get(sources[2]));
    }
}
