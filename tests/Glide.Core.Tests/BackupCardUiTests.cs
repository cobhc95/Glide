using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core.Data;
using Glide.Core.Reset;

namespace Glide.Core.Tests;

/// <summary>
/// Headless contract of Settings &gt; Windows &amp; developer &gt; Back up and restore: placement directly above Reset Glide,
/// the shared data-category tick boxes, Settings search, and the Export -&gt; Import flow against a scratch settings folder
/// (SettingsStore's test directory override; never the real profile).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Data")]
public sealed class BackupCardUiTests : IDisposable
{
    public BackupCardUiTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static readonly List<IStyle> InstalledStyles = new();

    private static void EnsureAppStyles()
    {
        if (InstalledStyles.Count > 0) return;
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        InstalledStyles.Add(new FluentTheme());
        InstalledStyles.Add(new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
        for (var i = 0; i < InstalledStyles.Count; i++) app.Styles.Insert(i, InstalledStyles[i]);
    }

    public void Dispose()
    {
        if (InstalledStyles.Count == 0) return;
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            foreach (var style in InstalledStyles) Application.Current!.Styles.Remove(style);
            InstalledStyles.Clear();
        });
    }

    private static T Named<T>(Visual root, string name) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().Single(c => (c as StyledElement)?.Name == name);

    private static object? Call(SettingsWindow window, string name, params object?[] args) =>
        typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static T Field<T>(SettingsWindow window, string name) =>
        (T)typeof(SettingsWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static SettingsCard Card(SettingsWindow window, string id) =>
        window.GetLogicalDescendants().OfType<SettingsCard>().Single(c => c.CardId == id);

    private static SettingsWindow Open(GlideSettingsState state, Action<GlideSettingsState, bool> apply, bool expand = true)
    {
        EnsureAppStyles();
        var window = new SettingsWindow(state, apply);
        window.Show();
        Call(window, "ShowCategory", "Windows", true, false, null!);
        window.Width = 930;
        window.Height = 700;
        Settle(window);
        Card(window, SettingsWindow.BackupCardId).IsExpanded = expand;
        Settle(window);
        return window;
    }

    private static DataCategorySelector Selector(SettingsWindow window, string field) => Field<DataCategorySelector>(window, field);

    // ---- placement and the shared category list ----

    [Fact]
    public async Task Card_sits_directly_above_Reset_Glide_and_shows_the_shared_category_list()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(new GlideSettingsState(), (_, _) => { }, expand: false);
            try
            {
                var cards = Named<StackPanel>(window, "WindowsPanel").Children.OfType<SettingsCard>().ToList();
                Assert.Equal("windows-reset", cards[^1].CardId);
                var card = cards[^2];
                Assert.Equal(SettingsWindow.BackupCardId, card.CardId);
                Assert.Equal("Back up and restore", card.Title);
                Assert.Equal("Export or import Glide's settings and data", card.Subtitle);
                Assert.False(card.IsExpanded);

                // One source for both cards: the backup tick boxes are Reset's first six, same order, titles and look.
                var backup = Selector(window, "_backupSelector");
                var reset = Selector(window, "_resetSelector");
                var backupNames = backup.Categories.Select(c => AutomationProperties.GetName(backup.Checks[c.Id])).ToArray();
                var resetNames = reset.Categories.Where(c => c.CanExport).Select(c => AutomationProperties.GetName(reset.Checks[c.Id])).ToArray();
                Assert.Equal(resetNames, backupNames);
                Assert.Equal(["All settings", "Hotkeys and mouse gestures", "Saved profiles and presets", "Window sizes and positions", "Recent files and Home list", "Last session"], backupNames.OfType<string>());
                Assert.Equal(9, reset.Checks.Count);
                Assert.All(backup.Checks.Values, c => Assert.Contains("resetItem", c.Classes));
                Assert.Equal("BackupAllSettingsCheck", backup.Checks[ResetItemId.AllSettings].Name);
                Assert.Equal("ResetAllSettingsCheck", reset.Checks[ResetItemId.AllSettings].Name);

                // Everything is ticked to start with; the bar counts and enables Export; Import never depends on ticks.
                var count = Named<TextBlock>(window, "BackupCountText");
                var export = Named<Button>(window, "BackupExportButton");
                var import = Named<Button>(window, "BackupImportButton");
                Assert.Equal("6 selected", count.Text);
                Assert.True(export.IsEnabled);
                Click(Named<Button>(window, "BackupClearButton"));
                Assert.Equal("Nothing selected", count.Text);
                Assert.False(export.IsEnabled);
                Assert.True(import.IsEnabled);
                Click(Named<Button>(window, "BackupSelectAllButton"));
                Assert.Equal(6, window.SelectedBackupItems().Count);
                backup.Checks[ResetItemId.RecentHistory].IsChecked = false;
                Assert.Equal("5 selected", count.Text);

                // Ticking is never a pending settings change.
                Settle(window);
                Assert.Equal(0, card.ChangedCount);
                Assert.Equal("Up to date", Named<TextBlock>(window, "DirtyText").Text);
                Assert.False(Named<Button>(window, "ApplyButton").IsEnabled);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Opening_the_card_from_a_command_expands_it_and_focuses_the_first_tick_box()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(new GlideSettingsState(), (_, _) => { }, expand: false);
            try
            {
                Call(window, "ShowCategory", "General", true, false, null!);
                Settle(window);
                window.OpenBackupCard();
                for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
                Assert.True(Card(window, SettingsWindow.BackupCardId).IsExpanded);
                Assert.True(Named<StackPanel>(window, "WindowsPanel").IsVisible);
                Assert.True(Selector(window, "_backupSelector").FirstCheck!.IsFocused);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    private static void Save(Visual visual, string name)
    {
        var folder = Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var size = new PixelSize((int)Math.Ceiling(visual.Bounds.Width), (int)Math.Ceiling(visual.Bounds.Height));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(visual);
        bitmap.Save(Path.Combine(folder, name));
    }

    [Theory]
    [InlineData(720, 520)]
    [InlineData(930, 700)]
    public async Task Expanded_card_fits_and_renders(double width, double height)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(new GlideSettingsState(), (_, _) => { });
            try
            {
                window.Width = width;
                window.Height = height;
                Settle(window);
                var card = Card(window, SettingsWindow.BackupCardId);
                var reset = Card(window, SettingsWindow.ResetCardId);
                reset.IsExpanded = true;
                Settle(window);
                var scroll = Named<ScrollViewer>(window, "ContentScroll");
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, "no horizontal scrolling");
                var body = card.Body.Bounds;
                foreach (var check in Selector(window, "_backupSelector").Checks.Values)
                    Assert.True(check.Bounds.Width > 0 && check.Bounds.Width <= body.Width + 0.5);
                // The bar buttons share the Reset bar's 30 px height.
                foreach (var name in new[] { "BackupSelectAllButton", "BackupClearButton", "BackupImportButton", "BackupExportButton" })
                    Assert.Equal(30, Named<Button>(window, name).Bounds.Height, 0.5);
                Save(card, $"backup-card-{(int)width}.png");
                Save(reset, $"reset-card-{(int)width}.png");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    // ---- search ----

    [Theory]
    [InlineData("backup", "Back up and restore")]
    [InlineData("back up", "Back up and restore")]
    [InlineData("restore", "Back up and restore")]
    [InlineData("transfer", "Export Glide backup")]
    [InlineData("migrate", "Import Glide backup")]
    [InlineData("move to new pc", "Export Glide backup")]
    [InlineData("move to new pc", "Import Glide backup")]
    [InlineData("settings file", "Back up and restore")]
    [InlineData("export", "Export Glide backup")]
    [InlineData("import", "Import Glide backup")]
    [InlineData("profile", "Back up and restore")]
    [InlineData("back up recent", "Back up recent files and Home list")]
    public async Task Settings_search_finds_the_card_and_both_actions(string query, string expectedLabel)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = Open(new GlideSettingsState(), (_, _) => { }, expand: false);
            try
            {
                Call(window, "RunSearchRebuild", query);
                var labels = Named<StackPanel>(window, "SearchPanel").GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains(expectedLabel, labels);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    // ---- Export -> Import ----

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "glide-backup-ui-tests", Guid.NewGuid().ToString("N"));
        public string Settings => Path.Combine(Root, "settings");
        public string Backups => Path.Combine(Root, "Glide backups");
        public string File => Path.Combine(Root, "move" + GlideBackupFormat.FileExtension);
        public Scratch() => Directory.CreateDirectory(Settings);
        public void Dispose() { TempDir.TryDelete(Root); }
    }

    private static SettingsWindow Wire(SettingsWindow window, Scratch scratch)
    {
        window.BackupSavePathOverride = () => Task.FromResult<string?>(scratch.File);
        window.BackupOpenPathOverride = () => Task.FromResult<string?>(scratch.File);
        window.BackupFolderOverride = scratch.Backups;
        return window;
    }

    private static async Task ExportFrom(Scratch scratch, GlideSettingsState state, params ResetItemId[] keep)
    {
        var source = Wire(Open(state, (_, _) => { }), scratch);
        try
        {
            var selector = Selector(source, "_backupSelector");
            foreach (var (id, check) in selector.Checks) check.IsChecked = keep.Contains(id);
            await source.RunBackupExportAsync();
            Assert.True(File.Exists(scratch.File));
            Assert.StartsWith("Backed up", Named<TextBlock>(source, "BackupResultText").Text);
        }
        finally { source.Close(); }
    }

    [Fact]
    public async Task Export_then_import_stages_settings_replaces_profiles_after_a_safety_backup_and_Cancel_rolls_back()
    {
        using var scratch = new Scratch();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = scratch.Settings;
            try
            {
                ProfileStore.Add("Travel", new GlideSettingsState { ThemeChoice = "Light" });
                await ExportFrom(scratch, new GlideSettingsState { ThemeChoice = "Light", ExternalProgram1 = @"C:\Elsewhere\tool.exe" },
                    ResetItemId.AllSettings, ResetItemId.ProfilesAndPresets);

                // "This PC": other profiles, a dark theme and its own external program.
                ProfileStore.ImportDocument(new ProfileStore.ProfileDocument { Profiles = [new ProfileStore.StoredProfile { Name = "Mine" }] });
                var applied = new List<(GlideSettingsState State, bool Commit)>();
                var window = Wire(Open(new GlideSettingsState { ThemeChoice = "Dark", ExternalProgram1 = @"C:\Mine\editor.exe" }, (s, c) => applied.Add((s.CloneState(), c))), scratch);
                BackupImportWindow? shown = null;
                window.BackupImportDialogOpened += d =>
                {
                    shown = d;
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Only what the file holds, all ticked; Cancel has the focus; the safety backup is offered.
                        Assert.Equal([ResetItemId.AllSettings, ResetItemId.ProfilesAndPresets], d.Selector.Categories.Select(c => c.Id).ToArray());
                        Assert.All(d.Selector.Checks.Values, c => Assert.True(c.IsChecked));
                        Assert.True(d.BackupCheck.IsVisible && d.BackupCheck.IsChecked == true);
                        Assert.Contains("external programs were not imported", d.NotesText.Text);
                        Assert.DoesNotContain("network", d.NotesText.Text); // nothing in this file named a network folder
                        Assert.True(d.CancelButton.IsFocused, "the safe button has the focus");
                        d.UpdateLayout();
                        Save(d, "backup-import-dialog.png");
                        Click(d.ImportButton);
                    }, DispatcherPriority.Background);
                };
                try
                {
                    await window.RunBackupImportAsync();
                    Settle(window);
                    Assert.NotNull(shown);

                    // All settings: staged (editors show it, preview applied, nothing committed), external program kept.
                    Assert.Equal("Light", (Named<ComboBox>(window, "ThemeCombo").SelectedItem as ComboBoxItem)?.Content);
                    Assert.Equal(@"C:\Mine\editor.exe", Named<TextBox>(window, "External1Box").Text);
                    Assert.Equal("Dark", Field<GlideSettingsState>(window, "_committed").ThemeChoice);
                    Assert.Contains(applied, a => !a.Commit && a.State.ThemeChoice == "Light");
                    Assert.DoesNotContain(applied, a => a.Commit);
                    Assert.True(Named<Button>(window, "ApplyButton").IsEnabled);

                    // Profiles: replaced on disk, after a safety backup of the old ones.
                    Assert.Contains(ProfileStore.ListProfiles(), p => p.Name == "Travel");
                    Assert.DoesNotContain(ProfileStore.ListProfiles(), p => p.Name == "Mine");
                    var safety = Assert.Single(Directory.GetFiles(scratch.Backups, "*.glidebackup"));
                    Assert.Contains("Mine", string.Join(",", GlideBackupService.ReadImport(safety, new GlideSettingsState()).Profiles!.Profiles.Select(p => p.Name)));
                    var status = Named<TextBlock>(window, "BackupResultText").Text!;
                    Assert.Contains("Staged All settings", status);
                    Assert.Contains("Imported Saved profiles and presets", status);

                    // Cancel: the staged settings are rolled back like any other unapplied Settings edit.
                    Call(window, "RollbackPreviewDeferred");
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal("Dark", applied[^1].State.ThemeChoice);
                    Assert.False(applied[^1].Commit);
                }
                finally
                {
                    typeof(SettingsWindow).GetField("_closeWithoutRollback", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                    window.Close();
                }
            }
            finally { SettingsStore.SettingsDirectoryOverrideForTests.Value = previous; }
        });
    }

    [Fact]
    public async Task OK_after_an_import_commits_the_staged_settings()
    {
        using var scratch = new Scratch();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = scratch.Settings;
            try
            {
                await ExportFrom(scratch, new GlideSettingsState { ThemeChoice = "Light", StartWithWindowsInBackground = true }, ResetItemId.AllSettings);
                var applied = new List<(GlideSettingsState State, bool Commit)>();
                var window = Wire(Open(new GlideSettingsState { ThemeChoice = "Dark" }, (s, c) => applied.Add((s.CloneState(), c))), scratch);
                window.BackupImportDialogOpened += d => Dispatcher.UIThread.Post(() =>
                {
                    Assert.False(d.BackupCheck.IsVisible); // nothing is replaced in place, so no safety backup is offered
                    Click(d.ImportButton);
                }, DispatcherPriority.Background);
                await window.RunBackupImportAsync();
                Call(window, "OkClicked", null, new RoutedEventArgs());
                Dispatcher.UIThread.RunJobs();
                var commit = Assert.Single(applied, a => a.Commit);
                Assert.Equal("Light", commit.State.ThemeChoice);
                Assert.False(commit.State.StartWithWindowsInBackground); // startup state is never imported
                Assert.False(Directory.Exists(scratch.Backups));
            }
            finally { SettingsStore.SettingsDirectoryOverrideForTests.Value = previous; }
        });
    }

    [Fact]
    public async Task A_cancelled_dialog_or_a_damaged_file_changes_nothing()
    {
        using var scratch = new Scratch();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = scratch.Settings;
            try
            {
                ProfileStore.Add("Travel", new GlideSettingsState());
                await ExportFrom(scratch, new GlideSettingsState { ThemeChoice = "Light" }, ResetItemId.AllSettings, ResetItemId.ProfilesAndPresets);
                ProfileStore.ImportDocument(new ProfileStore.ProfileDocument { Profiles = [new ProfileStore.StoredProfile { Name = "Mine" }] });

                var applied = new List<(GlideSettingsState State, bool Commit)>();
                var window = Wire(Open(new GlideSettingsState(), (s, c) => applied.Add((s, c))), scratch);
                try
                {
                    window.BackupImportDialogOpened += d => Dispatcher.UIThread.Post(() => Click(d.CancelButton), DispatcherPriority.Background);
                    await window.RunBackupImportAsync();
                    Assert.Contains("Nothing was changed", Named<TextBlock>(window, "BackupResultText").Text);
                    Assert.Empty(applied);
                    Assert.Contains(ProfileStore.ListProfiles(), p => p.Name == "Mine");

                    // A damaged file never reaches the dialog.
                    var opened = 0;
                    window.BackupImportDialogOpened += _ => opened++;
                    await File.WriteAllTextAsync(scratch.File, "not a backup at all, just some text pretending to be one");
                    await window.RunBackupImportAsync();
                    Assert.Equal(0, opened);
                    var status = Named<TextBlock>(window, "BackupResultText").Text!;
                    Assert.StartsWith("Import failed:", status);
                    Assert.Contains("Nothing was changed", status);
                    Assert.Empty(applied);
                    Assert.Equal("Up to date", Named<TextBlock>(window, "DirtyText").Text);
                }
                finally { window.Close(); }
            }
            finally { SettingsStore.SettingsDirectoryOverrideForTests.Value = previous; }
        });
    }
}
