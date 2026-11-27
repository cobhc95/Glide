using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Explorer;
using Glide.Core.Renaming;
using Glide.Core.Tags;
using Glide.Tests.Support;

namespace Glide.Core.Tests;

/// <summary>
/// Issue #4: the batch rename dialog and the tag sidebar on the headless backend with the real app styles. Set
/// GLIDE_SAVE_RENDERS to a folder to write the PNG previews (synthetic file names only).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class BatchRenameDialogTests
{
    public BatchRenameDialogTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static readonly string? SaveDirectory = Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");

    private static void UseAppStyles(Window window)
    {
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Styles.Insert(0, new FluentTheme());
        window.Styles.Insert(1, new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
    }

    /// <summary>
    /// Shows a window so its content gets the Fluent templates: on the headless backend content that is already in a
    /// window when the window first shows is not templated, so it is attached after the first pass.
    /// </summary>
    private static void ShowThemed(Window window)
    {
        var content = window.Content;
        window.Content = null;
        window.Show();
        Pump(window);
        window.Content = content;
        Pump(window);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }

    private static List<BatchRenameSource> Sources(params string[] names) => names.Select(n => new BatchRenameSource(Path.Combine(@"C:\Pics", n), false)).ToList();

    private static BatchRenameDialog Open(IReadOnlyList<BatchRenameSource> sources, string? presets = null, Func<string, bool>? exists = null,
        IReadOnlyList<(string From, string To)>? lastBatch = null)
    {
        var dialog = new BatchRenameDialog(sources, presets, exists ?? (_ => false), lastBatch);
        UseAppStyles(dialog);
        ShowThemed(dialog);
        Pump(dialog);
        dialog.Editor.FocusEnd();
        Pump(dialog);
        return dialog;
    }

    /// <summary>Waits until the preview is planned (dates are read and long lists planned in the background).</summary>
    private static async Task SettleAsync(BatchRenameDialog dialog)
    {
        var ready = dialog.WhenPreviewReadyAsync();
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!ready.IsCompleted && DateTime.UtcNow < end) { Pump(dialog); await Task.Delay(10); }
        Pump(dialog);
        Assert.True(ready.IsCompleted, "the preview was planned");
    }

    private static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static Button Chip(BatchRenameDialog dialog, string key) => dialog.ChipButtons.Single(b => b.Name == "BatchRenameChip_" + key);

    /// <summary>Types into the box like a user: the caret goes to the end of the last text, then the text input arrives.</summary>
    private static void Type(BatchRenameDialog dialog, string text)
    {
        dialog.Editor.FocusEnd();
        Pump(dialog);
        dialog.KeyTextInput(text);
        Pump(dialog);
    }

    private static string TextOf(TextBlock block) =>
        block.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : block.Text ?? "";

    private sealed record Row(string Now, string After, string Status, bool Red, TextBlock AfterBlock);

    private static Row[] Rows(BatchRenameDialog dialog) =>
        dialog.PreviewList.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "PreviewRow").Select(border =>
        {
            var texts = border.GetVisualDescendants().OfType<TextBlock>().ToArray();
            var after = texts.Single(t => t.Name == "NewNameText");
            var status = texts.Single(t => t.Name == "RowStatus");
            var red = border.Background is ISolidColorBrush { Color: var c } && c == Color.Parse("#2A1A1A");
            return new Row(texts.Single(t => t.Name == "OldNameText").Text ?? "", TextOf(after), status.Text ?? "", red, after);
        }).ToArray();

    private static void Save(Window window, string name)
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
        Directory.CreateDirectory(SaveDirectory);
        Pump(window);
        var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(SaveDirectory, name));
    }

    // ------------------------------------------------------------------ batch rename dialog (5.2.0 rebuild, mockup batch-rename-v3)

    [Fact]
    public void The_dialog_opens_on_Old_name_with_nothing_to_rename_and_More_options_closed() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var dialog = Open(Sources("Filename.jpg", "other.png", "third.gif"));
        Assert.Equal("Rename 3 pictures", dialog.Title);
        Assert.Equal("{name}", dialog.Editor.Template);
        Assert.Single(dialog.Editor.TokenChips);                      // the default is just "Old name"
        Assert.False(dialog.RenameButton.IsEnabled);
        Assert.Equal("Rename", dialog.RenameButton.Content);
        Assert.False(dialog.MoreCard.IsVisible);
        Assert.Equal("▸ More options", dialog.MoreToggle.Content);
        Assert.Equal("Nothing will change yet", dialog.StatusText.Text);
        Assert.All(Rows(dialog), r => Assert.Equal("unchanged", r.Status));
        Assert.Equal(["Old name", "Number", "Date taken", "Date modified", "Folder name", "Size (W×H)"], dialog.ChipButtons.Select(b => ((string)b.Content!)[2..]));
        Assert.False(dialog.UndoButton.IsEnabled);                    // nothing to undo yet

        var mixed = Open([new(@"C:\Pics\a.jpg", false), new(@"C:\Pics\notes.txt", false), new(@"C:\Pics\Sub", true)]);
        Assert.Equal("Rename 3 items", mixed.Title);
        dialog.Close();
        mixed.Close();
    });

    [Fact]
    public Task Chips_add_pieces_and_typing_fills_the_text_between_them() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        var dialog = Open(Sources("IMG_1.jpg", "IMG_2.jpg", "IMG_3.jpg"));
        dialog.Editor.Template = "";
        Pump(dialog);
        Type(dialog, "Paris ");
        Click(Chip(dialog, "n"));
        Pump(dialog);
        Assert.Equal("Paris {n}", dialog.Editor.Template);
        Assert.Single(dialog.Editor.TokenChips);
        Type(dialog, " trip");                                        // the caret sits after the new piece
        Assert.Equal("Paris {n} trip", dialog.Editor.Template);
        await SettleAsync(dialog);
        Assert.Equal(["Paris 1 trip.jpg", "Paris 2 trip.jpg", "Paris 3 trip.jpg"], Rows(dialog).Select(r => r.After));
        Assert.Equal(["IMG_1.jpg", "IMG_2.jpg", "IMG_3.jpg"], Rows(dialog).Select(r => r.Now));
        Assert.All(Rows(dialog), r => Assert.Equal("✓", r.Status));
        Assert.Equal("3 will be renamed", dialog.StatusText.Text);
        Assert.Equal("Rename 3", dialog.RenameButton.Content);
        Assert.True(dialog.RenameButton.IsEnabled);
        dialog.Close();
    });

    [Fact]
    public void A_chip_goes_in_at_the_caret_and_replaces_a_selection() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var dialog = Open(Sources("a.jpg"));
        dialog.Editor.Template = "abcd";
        Pump(dialog);
        var box = dialog.Editor.TextPieces[0];
        box.Focus(); box.CaretIndex = 2;
        Pump(dialog);
        Click(Chip(dialog, "taken"));
        Pump(dialog);
        Assert.Equal("ab{taken}cd", dialog.Editor.Template);

        dialog.Editor.Template = "abcd";
        Pump(dialog);
        box = dialog.Editor.TextPieces[0];
        box.Focus(); box.SelectionStart = 1; box.SelectionEnd = 3;
        Pump(dialog);
        Click(Chip(dialog, "folder"));
        Pump(dialog);
        Assert.Equal("a{folder}d", dialog.Editor.Template);
        dialog.Close();
    });

    [Fact]
    public void Backspace_after_a_piece_removes_it_and_Delete_before_one_removes_it_and_arrows_step_over_pieces() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var dialog = Open(Sources("a.jpg"));
        dialog.Editor.Template = "a{n}b{taken}c";
        Pump(dialog);
        Assert.Equal(2, dialog.Editor.TokenChips.Count);
        Assert.Equal(["a", "b", "c"], dialog.Editor.TextPieces.Select(t => t.Text));

        var second = dialog.Editor.TextPieces[1];                     // the text between the two pieces
        second.Focus(); second.CaretIndex = 0;
        Pump(dialog);
        dialog.KeyPress(Key.Back, RawInputModifiers.None);            // at the start: removes the piece before it
        Pump(dialog);
        Assert.Equal("ab{taken}c", dialog.Editor.Template);
        Assert.Equal(["ab", "c"], dialog.Editor.TextPieces.Select(t => t.Text));
        Assert.Equal(1, dialog.Editor.TextPieces[0].CaretIndex);      // the caret stays at the join

        var first = dialog.Editor.TextPieces[0];
        first.Focus(); first.CaretIndex = 2;
        Pump(dialog);
        dialog.KeyPress(Key.Right, RawInputModifiers.None);           // over the piece into the next text
        Pump(dialog);
        Assert.True(dialog.Editor.TextPieces[1].IsFocused);
        Assert.Equal(0, dialog.Editor.TextPieces[1].CaretIndex);
        dialog.KeyPress(Key.Left, RawInputModifiers.None);            // and back
        Pump(dialog);
        Assert.True(dialog.Editor.TextPieces[0].IsFocused);

        dialog.KeyPress(Key.Delete, RawInputModifiers.None);          // at the end: removes the piece after it
        Pump(dialog);
        Assert.Equal("abc", dialog.Editor.Template);
        Assert.Empty(dialog.Editor.TokenChips);
        dialog.Close();
    });

    [Fact]
    public Task Replace_text_Numbers_and_Capitals_change_the_preview_live() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        var dialog = Open(Sources("IMG_4471.jpg", "IMG_4472.jpg"));
        dialog.Editor.Template = "{name} {n}";
        dialog.FindBox.Text = "img";                                  // plain text, ignores case
        dialog.ReplaceBox.Text = "photo";
        dialog.StartBox.Text = "7";
        dialog.LookCombo.SelectedIndex = 2;                           // 001
        dialog.CapitalsCombo.SelectedIndex = 4;                       // Sentence case
        await SettleAsync(dialog);
        Assert.Equal(["Photo_4471 007.jpg", "Photo_4472 008.jpg"], Rows(dialog).Select(r => r.After));

        dialog.CapitalsCombo.SelectedIndex = 2;                       // UPPER
        dialog.LookCombo.SelectedIndex = 0;                           // 1
        Pump(dialog);
        Assert.Equal(["PHOTO_4471 7.jpg", "PHOTO_4472 8.jpg"], Rows(dialog).Select(r => r.After));
        Assert.Equal(["1", "01", "001", "0001"], dialog.LookCombo.Items.OfType<string>());
        Assert.Equal(["Keep", "lower", "UPPER", "Title Case", "Sentence case"], dialog.CapitalsCombo.Items.OfType<string>());
        dialog.Close();
    });

    [Fact]
    public Task The_token_parts_are_highlighted_in_After() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        var dialog = Open(Sources("a.jpg"));
        dialog.Facts.ModifiedProbe = _ => new DateTime(2026, 5, 14, 10, 0, 0);
        dialog.Editor.Template = "{modified} Paris trip {n}";
        dialog.LookCombo.SelectedIndex = 2;
        await SettleAsync(dialog);
        var after = Rows(dialog).Single().AfterBlock;
        var runs = after.Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToArray();
        Assert.Equal(["2026-05-14", " Paris trip ", "001", ".jpg"], runs.Select(r => r.Text));
        Assert.Equal([true, false, true, false], runs.Select(r => r.Background is not null));
        dialog.Close();
    });

    [Fact]
    public Task A_conflict_is_red_counted_and_left_out_but_the_other_rows_still_rename() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        var dialog = Open(Sources("a.jpg", "b.jpg", "c.jpg"), exists: p => p.EndsWith("Trip 2.jpg", StringComparison.Ordinal));
        dialog.Editor.Template = "Trip {n}";
        await SettleAsync(dialog);
        var rows = Rows(dialog);
        Assert.Equal(["✓", "⚠ name taken", "✓"], rows.Select(r => r.Status));
        Assert.Equal([false, true, false], rows.Select(r => r.Red));
        var status = dialog.PreviewList.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RowStatus" && t.Text == "⚠ name taken");
        Assert.Equal(Color.Parse("#F06A6A"), ((ISolidColorBrush)status.Foreground!).Color);
        Assert.Equal("2 will be renamed · 1 problem", dialog.StatusText.Text);
        Assert.Equal("Rename 2", dialog.RenameButton.Content);
        Assert.True(dialog.RenameButton.IsEnabled);

        IReadOnlyList<(string From, string To)>? moves = null;
        dialog.Executor = m => { moves = m; return BatchRenameResult.Ok(m); };
        await dialog.RenameForTestsAsync();
        Assert.Equal([(@"C:\Pics\a.jpg", @"C:\Pics\Trip 1.jpg"), (@"C:\Pics\c.jpg", @"C:\Pics\Trip 3.jpg")], moves);   // the red one is left alone
    });

    [Fact]
    public Task Two_new_names_that_are_the_same_are_both_marked_and_Rename_is_off_when_nothing_is_left() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        var dialog = Open(Sources("a.jpg", "b.jpg"));
        dialog.Editor.Template = "same";
        await SettleAsync(dialog);
        Assert.Equal(["⚠ name taken", "⚠ name taken"], Rows(dialog).Select(r => r.Status));
        Assert.Equal("0 will be renamed · 2 problems", dialog.StatusText.Text);
        Assert.False(dialog.RenameButton.IsEnabled);

        dialog.Editor.Template = "bad:name";
        Pump(dialog);
        Assert.Equal(["⚠ not allowed", "⚠ not allowed"], Rows(dialog).Select(r => r.Status));
        Assert.False(dialog.RenameButton.IsEnabled);
        dialog.Close();
    });

    [Fact]
    public Task A_date_piece_with_no_date_is_skipped_and_dates_are_read_once_per_file() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        var dialog = Open(Sources("a.jpg", "notes.png", "b.jpg"));
        dialog.Facts.TakenProbe = p => Path.GetFileName(p) == "notes.png" ? null : new DateTime(2026, 5, 14, 9, 0, 0);
        dialog.Editor.Template = "{taken} Paris trip {n}";
        dialog.LookCombo.SelectedIndex = 2;
        await SettleAsync(dialog);
        var rows = Rows(dialog);
        Assert.Equal(["2026-05-14 Paris trip 001.jpg", "no date taken — left as is", "2026-05-14 Paris trip 002.jpg"], rows.Select(r => r.After));
        Assert.Equal(["✓", "skipped", "✓"], rows.Select(r => r.Status));
        Assert.Equal("2 will be renamed", dialog.StatusText.Text);   // a skipped row is not a problem
        var probes = dialog.Facts.ProbeCount;
        Assert.Equal(3, probes);

        dialog.CapitalsCombo.SelectedIndex = 2;
        dialog.StartBox.Text = "5";
        Type(dialog, "x");
        await SettleAsync(dialog);
        Assert.Equal(probes, dialog.Facts.ProbeCount);                // typing again reads nothing again
        dialog.Close();
    });

    [Fact]
    public async Task A_long_list_is_planned_in_the_background_after_a_pause()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var names = Enumerable.Range(1, BatchRenameDialog.SyncLimit + 50).Select(i => $"IMG_{i:0000}.jpg").ToArray();
            var dialog = Open(Sources(names));
            dialog.Editor.Template = "Trip {n}";
            Assert.False(dialog.RenameButton.IsEnabled);              // the preview is still being prepared
            await SettleAsync(dialog);
            Assert.Equal(names.Length, dialog.CurrentPlan.WillRenameCount);
            Assert.True(dialog.RenameButton.IsEnabled);
            Assert.Equal($"Rename {names.Length}", dialog.RenameButton.Content);
            dialog.Close();
        });
    }

    [Fact]
    public Task More_options_hold_date_format_remove_extension_sort_match_case_and_regular_expressions() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        var dialog = Open(Sources("IMG_4471.JPG", "IMG_4472.JPG"));
        dialog.Facts.ModifiedProbe = s => Path.GetFileName(s.Path) == "IMG_4471.JPG" ? new DateTime(2026, 5, 15, 8, 5, 0) : new DateTime(2026, 5, 14, 15, 30, 0);
        dialog.SetMoreOptions(true);
        Assert.True(dialog.MoreCard.IsVisible);
        Assert.Equal("▾ More options", dialog.MoreToggle.Content);
        Click(dialog.MoreToggle);
        Assert.False(dialog.MoreCard.IsVisible);
        Click(dialog.MoreToggle);
        Assert.Equal(["2026-05-14", "14-05-2026", "20260514", "2026-05-14 15.30", "Custom…"], dialog.DateFormatCombo.Items.OfType<string>());

        dialog.Editor.Template = "{modified} {n}";
        dialog.DateFormatCombo.SelectedIndex = 3;
        dialog.ExtensionCombo.SelectedIndex = 1;                      // lower case
        dialog.SortCombo.SelectedIndex = 3;                           // date modified: the 14th comes first
        await SettleAsync(dialog);
        Assert.Equal(["IMG_4472.JPG", "IMG_4471.JPG"], Rows(dialog).Select(r => r.Now));
        Assert.Equal(["2026-05-14 15.30 1.jpg", "2026-05-15 08.05 2.jpg"], Rows(dialog).Select(r => r.After));

        dialog.DateFormatCombo.SelectedIndex = 4;                     // Custom…
        Assert.True(dialog.CustomDateBox.IsVisible);
        dialog.CustomDateBox.Text = "yyyy_MM";
        Pump(dialog);
        Assert.Equal(["2026_05 1.jpg", "2026_05 2.jpg"], Rows(dialog).Select(r => r.After));

        dialog.Editor.Template = "{name}";
        dialog.RemoveFirstBox.Text = "4";
        dialog.RemoveDigitsCheck.IsChecked = true;
        dialog.ExtensionCombo.SelectedIndex = 2;                      // UPPER
        Pump(dialog);
        Assert.Equal(["⚠ not allowed", "⚠ not allowed"], Rows(dialog).Select(r => r.Status));   // the first four characters and all digits are gone: no name is left

        dialog.RemoveFirstBox.Text = "0";
        dialog.RemoveDigitsCheck.IsChecked = false;
        dialog.Editor.Template = "{name}";
        dialog.FindBox.Text = "(\\d+)";
        dialog.ReplaceBox.Text = "no$1";
        dialog.RegexCheck.IsChecked = true;
        Pump(dialog);
        Assert.Equal(["IMG_no4472.JPG", "IMG_no4471.JPG"], Rows(dialog).Select(r => r.After));
        dialog.FindBox.Text = "(broken";
        Pump(dialog);
        Assert.StartsWith("Not a valid regular expression", dialog.StatusText.Text);
        Assert.False(dialog.RenameButton.IsEnabled);

        dialog.RegexCheck.IsChecked = false;
        dialog.FindBox.Text = "img";
        dialog.ReplaceBox.Text = "pic";
        dialog.MatchCaseCheck.IsChecked = true;                       // "img" no longer matches "IMG"
        Pump(dialog);
        Assert.Equal(0, dialog.CurrentPlan.WillRenameCount);
        dialog.MatchCaseCheck.IsChecked = false;
        Pump(dialog);
        Assert.Equal(["pic_4472.JPG", "pic_4471.JPG"], Rows(dialog).Select(r => r.After));
        dialog.Close();
    });

    // ------------------------------------------------------------------ saved presets

    [Fact]
    public void Presets_are_saved_loaded_and_deleted_and_the_settings_string_is_reported() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var dialog = Open(Sources("IMG_1.jpg"));
        string? stored = null;
        dialog.PresetsChanged += json => stored = json;
        Assert.Equal("Saved  ▾", dialog.PresetsButton.Content);

        dialog.Editor.Template = "{modified} Paris {n}";
        dialog.FindBox.Text = "IMG";
        dialog.ReplaceBox.Text = "photo";
        dialog.StartBox.Text = "5";
        dialog.LookCombo.SelectedIndex = 2;
        dialog.CapitalsCombo.SelectedIndex = 3;
        dialog.DateFormatCombo.SelectedIndex = 1;
        dialog.SortCombo.SelectedIndex = 1;
        Assert.False(dialog.SavePreset("   "));                       // a blank name saves nothing
        Assert.True(dialog.SavePreset("Holiday photos"));
        Assert.Equal("Saved: Holiday photos  ▾", dialog.PresetsButton.Content);
        Assert.NotNull(stored);
        Assert.Equal("Holiday photos", Assert.Single(RenamePresets.Parse(stored)).Name);

        // Change everything, then load it back.
        dialog.Editor.Template = "{name}";
        dialog.FindBox.Text = "";
        dialog.StartBox.Text = "1";
        dialog.LookCombo.SelectedIndex = 0;
        dialog.CapitalsCombo.SelectedIndex = 0;
        dialog.DateFormatCombo.SelectedIndex = 0;
        dialog.SortCombo.SelectedIndex = 0;
        Assert.True(dialog.LoadPreset("holiday PHOTOS"));
        Pump(dialog);
        var recipe = dialog.Recipe;
        Assert.Equal("{modified} Paris {n}", recipe.Template);
        Assert.Equal(("IMG", "photo"), (recipe.Find, recipe.Replace));
        Assert.Equal((5, 3, RenameCase.Title, "dd-MM-yyyy", RenameSort.Name), (recipe.NumberStart, recipe.NumberDigits, recipe.Case, recipe.DateFormat, recipe.Sort));
        Assert.True(dialog.MoreCard.IsVisible);                       // a loaded preset shows the options it uses
        Assert.False(dialog.LoadPreset("nothing like it"));

        // A new dialog gets the presets from the settings string.
        var again = Open(Sources("IMG_1.jpg"), stored);
        again.PresetsChanged += json => stored = json;
        Assert.Equal(["Holiday photos"], again.Presets.Select(p => p.Name));
        Assert.True(again.LoadPreset("Holiday photos"));
        Assert.Equal("{modified} Paris {n}", again.Editor.Template);
        Assert.Equal(2, again.Editor.Pieces.Count(p => p.IsToken));
        again.DeletePreset("Holiday photos");
        Assert.Empty(again.Presets);
        Assert.Empty(RenamePresets.Parse(stored));
        Assert.Equal("Saved  ▾", again.PresetsButton.Content);
        dialog.Close();
        again.Close();
    });

    [Fact]
    public void The_saved_menu_lists_the_presets_and_saving_through_its_name_box_works() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var presets = RenamePresets.Serialize(RenamePresets.Save(RenamePresets.Save([], "One", new RenameRecipe { Template = "one{n}" }), "Two", new RenameRecipe { Template = "two{n}" }));
        var dialog = Open(Sources("a.jpg"), presets);
        Assert.Equal(["Two", "One"], dialog.Presets.Select(p => p.Name));
        var flyout = (Avalonia.Controls.Flyout)dialog.PresetsButton.Flyout!;
        flyout.ShowAt(dialog.PresetsButton);
        Pump(dialog);
        var panel = (Control)flyout.Content!;
        var loadButtons = panel.GetVisualDescendants().OfType<Button>().Where(b => b.Content is "One" or "Two").ToArray();
        Assert.Equal(["Two", "One"], loadButtons.Select(b => (string)b.Content!));
        Click(loadButtons[1]);
        Pump(dialog);
        Assert.Equal("one{n}", dialog.Editor.Template);
        Assert.Equal("Saved: One  ▾", dialog.PresetsButton.Content);

        flyout.ShowAt(dialog.PresetsButton);
        Pump(dialog);
        Assert.Equal("One", dialog.PresetNameBox.Text);               // the loaded name is offered, so Save replaces it
        dialog.PresetNameBox.Text = "Three";
        Click(dialog.SavePresetButton);
        Pump(dialog);
        Assert.Equal(["Three", "Two", "One"], dialog.Presets.Select(p => p.Name));
        dialog.Close();
    });

    // ------------------------------------------------------------------ rename, undo, failures

    [Fact]
    public Task Rename_with_chips_and_typing_renames_the_files_of_a_temp_folder_then_Undo_puts_them_back() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        using var dir = new TempDir("glide-batchdialog");
        foreach (var name in new[] { "one.jpg", "two.jpg", "three.png" }) File.WriteAllText(dir.Combine(name), name);
        var sources = new[] { "one.jpg", "two.jpg", "three.png" }.Select(n => new BatchRenameSource(dir.Combine(n), false)).ToList();

        var dialog = new BatchRenameDialog(sources);
        UseAppStyles(dialog);
        ShowThemed(dialog);
        Pump(dialog);
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        BatchRenameResult? done = null;
        dialog.Executor = moves => done = BatchRenameExecutor.Execute(moves);

        // Old name is already there: type after it, add a Number chip, type again.
        Type(dialog, " trip ");
        Click(Chip(dialog, "n"));
        Pump(dialog);
        dialog.LookCombo.SelectedIndex = 1;                           // 01
        await SettleAsync(dialog);
        Assert.Equal("{name} trip {n}", dialog.Editor.Template);
        Assert.Equal(["one trip 01.jpg", "two trip 02.jpg", "three trip 03.png"], Rows(dialog).Select(r => r.After));

        await dialog.RenameForTestsAsync();
        Pump(dialog);
        Assert.True(closed);
        Assert.True(done!.Success, done.Error);
        Assert.Equal(["one trip 01.jpg", "three trip 03.png", "two trip 02.jpg"], Directory.GetFiles(dir.Path).Select(p => Path.GetFileName(p)!).OrderBy(n => n).ToArray());
        Assert.Equal("one.jpg", File.ReadAllText(dir.Combine("one trip 01.jpg")));

        // Next time the dialog is opened (same session) the last batch can be undone.
        var again = new BatchRenameDialog([new(dir.Combine("two trip 02.jpg"), false)], null, null, done.Renamed);
        UseAppStyles(again);
        ShowThemed(again);
        Pump(again);
        Assert.True(again.UndoButton.IsEnabled);
        var closedAgain = false;
        again.Closed += (_, _) => closedAgain = true;
        await again.UndoForTestsAsync();
        Pump(again);
        Assert.True(closedAgain);
        Assert.Equal(["one.jpg", "three.png", "two.jpg"], Directory.GetFiles(dir.Path).Select(p => Path.GetFileName(p)!).OrderBy(n => n).ToArray());
        Assert.Equal("two.jpg", File.ReadAllText(dir.Combine("two.jpg")));
    });

    [Fact]
    public Task Undo_is_off_when_nothing_was_renamed_or_a_file_has_moved_and_says_why() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        IReadOnlyList<(string From, string To)> last = [(@"C:\Pics\a.jpg", @"C:\Pics\b.jpg")];
        var gone = Open(Sources("x.jpg"), exists: _ => false, lastBatch: last);
        Assert.False(gone.UndoButton.IsEnabled);                      // b.jpg is no longer there
        Assert.Contains("no longer there", (string)ToolTip.GetTip(gone.UndoButton)!);
        gone.Close();

        var fine = Open(Sources("x.jpg"), exists: p => p == @"C:\Pics\b.jpg", lastBatch: last);
        Assert.True(fine.UndoButton.IsEnabled);
        IReadOnlyList<(string From, string To)>? moves = null;
        fine.Executor = m => { moves = m; return BatchRenameResult.Ok(m); };
        await fine.UndoForTestsAsync();
        Assert.Equal([(@"C:\Pics\b.jpg", @"C:\Pics\a.jpg")], moves);

        var none = Open(Sources("x.jpg"));
        Assert.False(none.UndoButton.IsEnabled);
        Assert.Contains("nothing to undo", (string)ToolTip.GetTip(none.UndoButton)!);
        none.Close();
    });

    [Fact]
    public Task A_refused_rename_keeps_the_dialog_open_and_shows_why() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        var dialog = Open(Sources("a.jpg"));
        dialog.Executor = _ => new BatchRenameResult(false, [], "Nothing was renamed: The file is in use.");
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        dialog.Editor.Template = "new";
        await SettleAsync(dialog);
        await dialog.RenameForTestsAsync();
        Pump(dialog);
        Assert.False(closed);
        Assert.Equal("Nothing was renamed: The file is in use.", dialog.StatusText.Text);
        Assert.Equal(Color.Parse("#F06A6A"), ((ISolidColorBrush)dialog.StatusText.Foreground!).Color);
        dialog.Editor.Template = "newer";                             // the next change clears the message
        Pump(dialog);
        Assert.Equal("1 will be renamed", dialog.StatusText.Text);
        dialog.Close();
    });

    [Fact]
    public Task Enter_in_the_New_name_box_renames_like_the_Rename_button() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        var dialog = Open(Sources("a.jpg", "b.jpg"));
        IReadOnlyList<(string From, string To)>? moves = null;
        dialog.Executor = m => { moves = m; return BatchRenameResult.Ok(m); };
        dialog.Editor.Template = "x{n}";
        Pump(dialog);
        dialog.Editor.FocusEnd();
        Pump(dialog);
        dialog.KeyPress(Key.Enter, RawInputModifiers.None);
        for (var i = 0; i < 100 && moves is null; i++) { Pump(dialog); await Task.Delay(10); }
        Assert.Equal([(@"C:\Pics\a.jpg", @"C:\Pics\x1.jpg"), (@"C:\Pics\b.jpg", @"C:\Pics\x2.jpg")], moves);
    });

    [Fact]
    public void Escape_cancels_the_dialog_and_Cancel_closes_it() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var dialog = Open(Sources("a.jpg"));
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        dialog.KeyPress(Key.Escape, RawInputModifiers.None);
        Pump(dialog);
        Assert.True(closed);
    });

    [Fact]
    public Task Render_the_batch_rename_dialog() => AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
        string[] names = ["IMG_4471.jpg", "IMG_4472.jpg", "IMG_4480.JPG", "DSC00012.ARW", "Paris trip 005.jpg", "notes.png"];
        var taken = new Dictionary<string, DateTime?>
        {
            ["IMG_4471.jpg"] = new(2026, 5, 14), ["IMG_4472.jpg"] = new(2026, 5, 14), ["IMG_4480.JPG"] = new(2026, 5, 15),
            ["DSC00012.ARW"] = new(2026, 5, 15), ["Paris trip 005.jpg"] = new(2026, 5, 16), ["notes.png"] = null
        };
        var presets = RenamePresets.Serialize(RenamePresets.Save([], "Holiday photos", new RenameRecipe { Template = "{taken} Paris trip {n}", NumberDigits = 3 }));
        var dialog = Open(Sources(names), presets, p => p.EndsWith("2026-05-16 Paris trip 005.jpg", StringComparison.Ordinal) || p == @"C:\Pics\b.jpg", [(@"C:\Pics\a.jpg", @"C:\Pics\b.jpg")]);
        dialog.Facts.TakenProbe = p => taken[Path.GetFileName(p)];
        dialog.LoadPreset("Holiday photos");
        dialog.SetMoreOptions(false);
        dialog.FindBox.Text = "IMG";
        dialog.ReplaceBox.Text = "photo";
        dialog.Width = 980; dialog.Height = 760;
        await SettleAsync(dialog);
        Save(dialog, "batch-rename-v3.png");

        dialog.SetMoreOptions(true);
        Pump(dialog);
        Save(dialog, "batch-rename-v3-more.png");
        dialog.Close();
    });

    // ------------------------------------------------------------------ tag sidebar

    private static TagOptions Options(string groups = "+Family: parents, mom, dad, sibling_1\nFriends: anna, ben, carl") =>
        TagOptions.Create(groups, "", "", "", "", "");

    private static Window Host(TagSidebar sidebar)
    {
        var window = new Window { Width = TagSidebar.PanelWidth + 40, Height = 600, Content = sidebar };
        UseAppStyles(window);
        ShowThemed(window);
        Pump(window);
        return window;
    }

    private static CheckBox Box(TagSidebar sidebar, string tag) => sidebar.TagBoxes.Single(b => (string?)b.Tag == tag);

    [Fact]
    public void The_sidebar_ticks_the_tags_already_in_the_names_and_dashes_the_partial_ones() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var sidebar = new TagSidebar();
        var window = Host(sidebar);
        sidebar.Configure(Options(), applyImmediately: true);
        sidebar.SetTargets(["a {Family;mom}.jpg", "b {Family;mom;dad}.jpg"], "2 files selected");
        Pump(window);
        Assert.Equal(["parents", "mom", "dad", "sibling_1", "anna", "ben", "carl"], sidebar.TagBoxes.Select(b => (string)b.Tag!));
        Assert.True(Box(sidebar, "mom").IsChecked);
        Assert.Null(Box(sidebar, "dad").IsChecked);      // only one of the two files
        Assert.False(Box(sidebar, "anna").IsChecked);
        window.Close();
    });

    [Fact]
    public void Clicking_a_tag_asks_to_add_it_and_clicking_a_ticked_tag_asks_to_remove_it() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var sidebar = new TagSidebar();
        var window = Host(sidebar);
        sidebar.Configure(Options(), applyImmediately: true);
        sidebar.SetTargets(["a {Family;mom}.jpg", "b {Family;mom;dad}.jpg"], "2 files selected");
        var requests = new List<(string[] Add, string[] Remove)>();
        sidebar.ChangeRequested += (add, remove) => requests.Add((add.ToArray(), remove.ToArray()));

        Box(sidebar, "anna").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));   // not there: add
        Box(sidebar, "mom").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));    // on all: remove
        Box(sidebar, "dad").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));    // on some: add to the rest
        Assert.Equal(["anna"], requests[0].Add); Assert.Empty(requests[0].Remove);
        Assert.Empty(requests[1].Add); Assert.Equal(["mom"], requests[1].Remove);
        Assert.Equal(["dad"], requests[2].Add); Assert.Empty(requests[2].Remove);
        window.Close();
    });

    [Fact]
    public void With_apply_immediately_off_ticks_wait_for_Add_or_Remove() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var sidebar = new TagSidebar();
        var window = Host(sidebar);
        sidebar.Configure(Options(), applyImmediately: false);
        sidebar.SetTargets(["a {Family;mom}.jpg", "b.jpg"], "2 files selected");
        Pump(window);
        var requests = new List<(string[] Add, string[] Remove)>();
        sidebar.ChangeRequested += (add, remove) => requests.Add((add.ToArray(), remove.ToArray()));
        Assert.True(sidebar.AddButton.IsVisible);
        Assert.False(sidebar.AddButton.IsEnabled);

        Box(sidebar, "ben").IsChecked = true; Box(sidebar, "ben").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Box(sidebar, "carl").IsChecked = true; Box(sidebar, "carl").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(requests);                                         // nothing renamed yet
        Assert.Equal(["ben", "carl"], sidebar.StagedTags.OrderBy(t => t));
        Assert.True(sidebar.AddButton.IsEnabled);

        sidebar.AddButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Single(requests);
        Assert.Equal(["ben", "carl"], requests[0].Add.OrderBy(t => t));
        Assert.Empty(sidebar.StagedTags);

        Box(sidebar, "mom").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        sidebar.RemoveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["mom"], requests[1].Remove);
        window.Close();
    });

    [Fact]
    public void The_apply_immediately_box_tells_the_window_and_the_empty_state_points_to_Settings() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var sidebar = new TagSidebar();
        var window = Host(sidebar);
        sidebar.Configure(TagOptions.Create("", "", "", "", "", ""), applyImmediately: true);
        Pump(window);
        Assert.Empty(sidebar.TagBoxes);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "TagEmptyText" && t.Text!.Contains("Settings"));

        bool? changedTo = null;
        sidebar.ApplyImmediatelyChanged += v => changedTo = v;
        sidebar.ApplyImmediatelyCheck.IsChecked = false;
        Assert.False(changedTo);
        var closed = false; var settings = false;
        sidebar.CloseRequested += () => closed = true;
        sidebar.SettingsRequested += () => settings = true;
        sidebar.CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        sidebar.SettingsButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(closed && settings);
        window.Close();
    });

    [Fact]
    public void Nothing_selected_disables_the_tags() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var sidebar = new TagSidebar();
        var window = Host(sidebar);
        sidebar.Configure(Options(), applyImmediately: true);
        sidebar.SetTargets([], "");
        Pump(window);
        Assert.All(sidebar.TagBoxes, b => Assert.False(b.IsEnabled));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "TagSidebarCaption" && t.Text!.Contains("Select"));
        window.Close();
    });

    [Fact]
    public void Render_the_tag_sidebar() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
        var sidebar = new TagSidebar();
        var window = Host(sidebar);
        window.Width = 330; window.Height = 560;
        sidebar.Configure(Options(), applyImmediately: true);
        sidebar.SetTargets(["DCIM0078 {Family;parents;mom}.jpg", "DCIM0079 {Family;mom;dad;anna}.jpg", "DCIM0080 {mom}.jpg"], "3 files selected");
        Save(window, "tag-sidebar.png");
        sidebar.Configure(Options(), applyImmediately: false);
        sidebar.SetTargets(["DCIM0078 {Family;parents;mom}.jpg", "DCIM0079 {Family;mom;dad;anna}.jpg", "DCIM0080 {mom}.jpg"], "3 files selected");
        Box(sidebar, "ben").IsChecked = true; Box(sidebar, "ben").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Save(window, "tag-sidebar-select-then-add.png");
        window.Close();
    });
}
