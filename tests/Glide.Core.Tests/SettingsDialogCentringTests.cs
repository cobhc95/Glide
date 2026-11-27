using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Windows;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.6 report: the "Unsaved settings changes" dialog always opened at the top-left of the screen. A position-only
/// placement saved under "settings-discard-confirm" (it was 0,0 on the reporting PC) was restored with a Manual startup
/// location, overriding CenterOwner on every later opening. Dialogs now never remember a position: they open centred on
/// their owner (DPI-aware, kept inside the owner's monitor working area).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Settings")]
public sealed class SettingsDialogCentringTests : IDisposable
{
    private static readonly ScreenWorkArea Full = new(0, 0, 1920, 1040, 1.0);
    private readonly List<IReadOnlyDictionary<string, WindowPlacementState>> _persisted = new();

    public SettingsDialogCentringTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        WindowPlacementService.PersistOverrideForTests = snapshot => _persisted.Add(snapshot);
        WindowPlacementService.ScreensOverrideForTests = _ => (new[] { Full }, Full);
    }

    public void Dispose()
    {
        WindowPlacementService.PersistOverrideForTests = null;
        WindowPlacementService.ScreensOverrideForTests = null;
        WindowPlacementService.ResetForTests();
    }

    private static PixelPoint ExpectedCentre(Window owner, Window dialog)
    {
        var scaling = owner.DesktopScaling;
        var ownerSize = owner.FrameSize ?? owner.ClientSize;
        var dialogSize = dialog.FrameSize ?? dialog.ClientSize;
        return new PixelPoint(
            owner.Position.X + (int)((ownerSize.Width - dialogSize.Width) * scaling / 2),
            owner.Position.Y + (int)((ownerSize.Height - dialogSize.Height) * scaling / 2));
    }

    private static void AssertNear(PixelPoint expected, PixelPoint actual, string what) =>
        Assert.True(Math.Abs(expected.X - actual.X) <= 2 && Math.Abs(expected.Y - actual.Y) <= 2, $"{what}: expected about {expected}, got {actual}");

    [Fact]
    public void Unsaved_changes_dialog_opens_centred_on_settings_even_with_a_saved_top_left_position()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            // The reporting PC's settings file: the dialog's remembered position was the screen's top-left corner.
            WindowPlacementService.ResetForTests(new Dictionary<string, WindowPlacementState>
            {
                ["settings-discard-confirm"] = new() { X = 0, Y = 0 },
            });
            var settings = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            settings.Show();
            try
            {
                settings.Position = new PixelPoint(420, 160);
                Dispatcher.UIThread.RunJobs();
                var check = settings.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Name == "PictureBorderCheck");
                check.IsChecked = !check.IsChecked;
                Dispatcher.UIThread.RunJobs();

                typeof(SettingsWindow).GetMethod("CancelClicked", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(settings, [null, new RoutedEventArgs()]);
                Dispatcher.UIThread.RunJobs();
                var dialog = Assert.Single(settings.OwnedWindows);
                Assert.Equal("Unsaved settings changes", dialog.Title);
                AssertNear(ExpectedCentre(settings, dialog), dialog.Position, "unsaved-changes dialog");

                // Keep editing: closes the dialog; no position is written for it.
                var keep = dialog.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == "Keep editing");
                keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(settings.OwnedWindows);
                Assert.Null(WindowPlacementService.Get("settings-discard-confirm"));

                // Moved elsewhere, Settings reopens the prompt centred on its new position.
                settings.Position = new PixelPoint(700, 260);
                Dispatcher.UIThread.RunJobs();
                typeof(SettingsWindow).GetMethod("CancelClicked", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(settings, [null, new RoutedEventArgs()]);
                Dispatcher.UIThread.RunJobs();
                var again = Assert.Single(settings.OwnedWindows);
                AssertNear(ExpectedCentre(settings, again), again.Position, "second opening");
                again.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == "Keep editing")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
            }
            finally
            {
                settings.UnsavedChangesPromptForTests = _ => SettingsWindow.UnsavedSettingsChoice.Discard;
                settings.Close();
            }
        });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(40)]
    public void Unsaved_changes_dialog_matches_the_mockup_and_a_long_list_scrolls_at_a_fixed_height(int count)
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() => WithAppStyles(() =>
        {
            WindowPlacementService.ResetForTests();
            var settings = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { Width = 930, Height = 700 };
            settings.Show();
            try
            {
                var changes = Enumerable.Range(1, count)
                    .Select(i => new SettingChange("Status Bar Size", "Small", "Large " + i) { Label = "Status bar size" }).ToList();
                var (dialog, dontRemind) = settings.BuildUnsavedChangesDialog(changes);
                Assert.Equal("Unsaved settings changes", dialog.Title);
                Assert.False(dialog.CanResize); // no maximise button
                Assert.Equal(SizeToContent.Manual, dialog.SizeToContent);
                Assert.Equal("Don't remind me again", ((TextBlock)dontRemind.Content!).Text);
                dialog.Show(settings);
                for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout(); }

                Assert.Equal(SettingsWindow.UnsavedChangesDialogWidth, dialog.ClientSize.Width, 1.0);
                Assert.Equal(SettingsWindow.UnsavedChangesDialogHeight, dialog.ClientSize.Height, 1.0);
                var heading = dialog.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "UnsavedChangesHeading");
                Assert.Equal($"{count} settings not applied", heading.Text);
                Assert.Equal(new[] { "Keep editing", "Discard", "Apply and close" },
                    dialog.GetLogicalDescendants().OfType<Button>().Where(b => b is not CheckBox).Select(b => b.Content as string).ToArray());

                var scroll = dialog.GetLogicalDescendants().OfType<ScrollViewer>().Single(s => s.Name == "UnsavedChangesScroll");
                Assert.True(scroll.Bounds.Height <= SettingsWindow.UnsavedChangesListMaxHeight + 0.5, $"list height {scroll.Bounds.Height}");
                if (count > 5) Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 1, $"a long list scrolls (extent {scroll.Extent}, viewport {scroll.Viewport})");
                else Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5, "a short list does not scroll");

                // Every button is fully inside the fixed-height dialog.
                foreach (var b in dialog.GetLogicalDescendants().OfType<Button>().Where(b => b is not CheckBox))
                {
                    var bottom = b.TranslatePoint(new Point(0, b.Bounds.Height), dialog)!.Value.Y;
                    Assert.True(bottom <= dialog.ClientSize.Height, $"{b.Content} bottom {bottom}");
                }

                // Rows: "Label: old → new" with the old value dimmed.
                var row = scroll.GetLogicalDescendants().OfType<TextBlock>().First();
                var runs = row.Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToList();
                Assert.Equal("Status bar size: Small → Large 1", string.Concat(runs.Select(r => r.Text)));
                Assert.True(runs[1].IsSet(Avalonia.Controls.Documents.TextElement.ForegroundProperty)); // dimmed
                Assert.False(runs[0].IsSet(Avalonia.Controls.Documents.TextElement.ForegroundProperty));
                Assert.False(runs[2].IsSet(Avalonia.Controls.Documents.TextElement.ForegroundProperty));
                dialog.Close();
            }
            finally { settings.Close(); }
        }));
    }

    /// <summary>The real theme (templates, so scroll viewers lay out) for the body only.</summary>
    private static void WithAppStyles(Action body)
    {
        var app = Application.Current!;
        var styles = new Avalonia.Styling.IStyle[]
        {
            new Avalonia.Themes.Fluent.FluentTheme(),
            new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") }
        };
        for (var i = 0; i < styles.Length; i++) app.Styles.Insert(i, styles[i]);
        try { body(); }
        finally { foreach (var style in styles) app.Styles.Remove(style); }
    }

    [Fact]
    public void Change_labels_use_the_catalogue_label_in_sentence_case()
    {
        Assert.Equal("1 setting not applied", SettingsWindow.UnsavedChangesHeading(1));
        Assert.Equal("3 settings not applied", SettingsWindow.UnsavedChangesHeading(3));
        var before = new GlideSettingsState();
        var after = before.CloneState();
        after.SwitchToNewTabs = !after.SwitchToNewTabs;
        after.ShowFolderCounter = !after.ShowFolderCounter;
        var changes = SettingsChangeSet.Compute(before, after);
        var tabs = changes.Single(c => c.Name == "Switch To New Tabs");
        Assert.Equal("Open new tabs in the", tabs.Label);
        foreach (var c in changes)
        {
            var words = c.Label.Split(' ');
            Assert.True(char.IsUpper(words[0][0]), c.Label);
            Assert.DoesNotContain(words.Skip(1), w => w.Length > 1 && char.IsUpper(w[0]) && w.Skip(1).All(char.IsLower) && w is not ("Windows" or "Explorer" or "Glide"));
        }
        Assert.Equal("Status bar size", SettingsChangeSet.SentenceCase("Status Bar Size"));
        Assert.Equal("Show HDR badge", SettingsChangeSet.SentenceCase("Show HDR Badge"));
    }

    [Theory]
    [InlineData("reset-confirm")]
    [InlineData("reset-result")]
    [InlineData("backup-import")]
    [InlineData("hotkey-confirm")]
    [InlineData("hotkey-preset-name")]
    [InlineData("profile-add")]
    [InlineData("confirm")]
    public void Position_only_dialogs_never_restore_or_save_a_position(string key)
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            WindowPlacementService.ResetForTests(new Dictionary<string, WindowPlacementState> { [key] = new() { X = 0, Y = 0 } });
            var owner = new Window { Width = 900, Height = 640 };
            owner.Show();
            try
            {
                owner.Position = new PixelPoint(500, 220);
                var dialog = new Window { Width = 470, SizeToContent = SizeToContent.Height, CanResize = false, Content = new TextBlock { Text = "Sure?" } };
                WindowPlacementService.Track(dialog, key);
                Assert.Equal(WindowPlacementMode.PositionOnly, WindowPlacementService.ModeOf(dialog));
                Assert.Equal(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);
                dialog.Show(owner);
                Dispatcher.UIThread.RunJobs();
                AssertNear(ExpectedCentre(owner, dialog), dialog.Position, key);
                dialog.Position = new PixelPoint(10, 10);
                dialog.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.Null(WindowPlacementService.Get(key)); // the stale entry is dropped and nothing new is remembered
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void Centring_is_dpi_aware_and_kept_inside_the_owner_monitor()
    {
        // Owner 1000x700 DIP at 150% on a monitor at x=1920: dialog 420x300 DIP.
        var screen = new ScreenWorkArea(1920, 0, 2560, 1400, 1.5);
        var (x, y) = WindowPlacementResolver.CenterOnOwner(2000, 100, 1500, 1050, 630, 450, screen);
        Assert.Equal((2000 + (1500 - 630) / 2, 100 + (1050 - 450) / 2), (x, y));
        // An owner hanging off the bottom-right corner: the dialog stays fully inside the working area.
        var (cx, cy) = WindowPlacementResolver.CenterOnOwner(4200, 1200, 1500, 1050, 630, 450, screen);
        Assert.Equal((screen.Right - 630, screen.Bottom - 450), (cx, cy));
        // Off the top-left too.
        var (lx, ly) = WindowPlacementResolver.CenterOnOwner(1000, -900, 600, 400, 630, 450, screen);
        Assert.Equal((screen.X, screen.Y), (lx, ly));
        // No monitor known: plain centring.
        Assert.Equal((100 + 435, 100 + 300), WindowPlacementResolver.CenterOnOwner(100, 100, 1500, 1050, 630, 450, null));
    }

    [Theory]
    [InlineData(980, 792, 0)]    // batch rename: 980x760 client plus title bar, owner maximized
    [InlineData(980, 792, 32)]   // same, with a title-bar inset
    [InlineData(980, 1100, 32)]  // taller than the work area: shrinks
    public void Fixed_size_dialog_fits_a_1389x828_work_area(int w, int h, int inset)
    {
        var area = new ScreenWorkArea(0, 0, 1389, 828, 1.0);
        var (x, y, fw, fh) = WindowPlacementResolver.FitDialog(0, 0, 1389, 828, w, h, 760, 592, inset, area);
        Assert.True(y >= area.Y + inset && x >= area.X);
        Assert.True(x + fw <= area.Right && y + fh <= area.Bottom);
        Assert.True(fh >= 592 && fw >= 760);
        // An owner hanging above the top of the screen still gives a dialog whose title bar is reachable.
        var (_, ty, _, _) = WindowPlacementResolver.FitDialog(0, -600, 1389, 828, w, h, 760, 592, inset, area);
        Assert.True(ty >= area.Y + inset);
    }
}
