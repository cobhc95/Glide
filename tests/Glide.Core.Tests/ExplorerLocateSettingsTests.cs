using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Glide.App.Settings;
using Glide.Core.Navigation;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// Settings &gt; Tabs &gt; Explorer tab card: "Open a picture's folder in" (explorer.locateOpensIn) and the Browse… button of
/// "Alternative file manager (.exe)".
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerLocateSettingsTests
{
    public ExplorerLocateSettingsTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    [Fact]
    public void Locate_setting_is_a_catalogued_choice_with_search_terms_and_Same_tab_default()
    {
        var entry = SettingsCatalog.All.Single(x => x.Id == "explorer.locateOpensIn");
        Assert.Equal("Open a picture's folder in", entry.Label);
        Assert.Equal(SettingKind.Choice, entry.Kind);
        Assert.Equal(ExplorerLocateTargets.SameTab, new GlideSettingsState().ExplorerLocateOpensIn);
        foreach (var term in new[] { "ctrl+e", "open folder", "locate", "explorer", "same tab", "new tab", "new window", "file location" })
            Assert.Contains(term, entry.SearchTerms);
        Assert.Equal(ExplorerLocateTargets.All, SettingActionCatalog.Choices["explorer.locateOpensIn"]);
    }

    [Theory]
    [InlineData("Same tab", "Same tab")]
    [InlineData("new WINDOW", "New window")]
    [InlineData("Always a new tab", "Always a new tab")]
    [InlineData("Existing or new tab", "Existing or new tab")]
    [InlineData("Open in a popup", "Same tab")]
    [InlineData(null, "Same tab")]
    [InlineData("", "Same tab")]
    public void Unknown_saved_values_fall_back_to_the_default(string? saved, string expected) =>
        Assert.Equal(expected, ExplorerLocateTargets.Normalize(saved));

    [Fact]
    public async Task Unknown_saved_value_loads_as_Same_tab()
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            await Task.Yield();
            var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            try
            {
                var state = new GlideSettingsState { ExplorerLocateOpensIn = "Teleport" };
                SettingsStore.Save(state);
                Assert.Equal(ExplorerLocateTargets.SameTab, SettingsStore.Load().ExplorerLocateOpensIn);
            }
            finally { SettingsStore.SettingsDirectoryOverrideForTests.Value = previous; }
        });
        TempDir.TryDelete(root);
    }

    [Fact]
    public async Task Back_setting_is_greyed_out_for_Same_tab_and_enabled_for_tab_and_window_modes()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            await Task.Yield();
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            try
            {
                window.Show();
                Settle();
                var combo = Field<ComboBox>(window, "ExplorerLocateOpensInCombo");
                var row = Field<Grid>(window, "ExplorerBackReturnsToImageRow");
                Assert.Equal("Same tab", (combo.SelectedItem as ComboBoxItem)?.Content);
                Assert.False(row.IsEnabled);
                foreach (var mode in new[] { "Existing or new tab", "Always a new tab", "New window" })
                {
                    combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Content == mode);
                    Settle();
                    Assert.True(row.IsEnabled, mode);
                }
                // Both controls sit in the same card.
                Assert.Same(combo.Parent!.Parent, row.Parent);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Browse_button_sits_at_the_right_of_the_alternative_file_manager_box()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            await Task.Yield();
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            try
            {
                window.Show();
                Settle();
                var box = Field<TextBox>(window, "AlternativeFileManagerPathBox");
                var browse = Field<Button>(window, "AlternativeFileManagerBrowseButton");
                Assert.Same(box.Parent, browse.Parent);
                Assert.Equal("Browse…", browse.Content);
                Assert.Contains("secondaryAction", browse.Classes);
                Assert.Equal(Grid.GetColumn(box) + 1, Grid.GetColumn(browse));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task A_picked_program_fills_the_box_and_is_a_staged_change_and_clear_empties_it()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            var previous = SettingsWindow.AlternativeFileManagerPickerOverride;
            string? startFolder = "unset";
            SettingsWindow.AlternativeFileManagerPickerOverride = (_, start) => { startFolder = start; return Task.FromResult<string?>(@"C:\Tools\FileManager\fm.exe"); };
            try
            {
                window.Show();
                Settle();
                Assert.Equal("Up to date", Field<TextBlock>(window, "DirtyText").Text);
                await window.BrowseAlternativeFileManagerAsync();
                await SettleDebouncedAsync();
                Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), startFolder);
                Assert.Equal(@"C:\Tools\FileManager\fm.exe", Field<TextBox>(window, "AlternativeFileManagerPathBox").Text);
                Assert.Equal("1 change", Field<TextBlock>(window, "DirtyText").Text);
                Assert.True(Field<Button>(window, "ApplyButton").IsEnabled);

                Field<Button>(window, "AlternativeFileManagerClearButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await SettleDebouncedAsync();
                Assert.Equal(string.Empty, Field<TextBox>(window, "AlternativeFileManagerPathBox").Text);
                Assert.Equal("Up to date", Field<TextBlock>(window, "DirtyText").Text);
            }
            finally
            {
                SettingsWindow.AlternativeFileManagerPickerOverride = previous;
                window.Close();
            }
        });
    }

    /// <summary>Text edits reach the change counter after the live-preview debounce.</summary>
    private static async Task SettleDebouncedAsync()
    {
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
    }

    private static void Settle()
    {
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
    }

    private static T Field<T>(SettingsWindow window, string name) =>
        (T)typeof(SettingsWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
}
