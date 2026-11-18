using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>
/// 5.1.0: Ctrl+T (GlideCommand.NewTab) and the "+" button always put the new tab at the far right end (like a web
/// browser) and switch to it, whatever "New tabs open at" and "Open new tabs in the foreground" are set to.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Tabs")]
public sealed class NewTabCommandPlacementTests
{
    public NewTabCommandPlacementTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    [Theory]
    [InlineData(NewTabPositions.NextToCurrent, false, false)]
    [InlineData(NewTabPositions.NextToCurrent, true, false)]
    [InlineData(NewTabPositions.AtEnd, false, false)]
    [InlineData(NewTabPositions.NextToCurrent, false, true)]
    [InlineData(NewTabPositions.AtEnd, false, true)]
    public async Task Ctrl_T_and_the_plus_button_open_at_the_end_and_switch_to_the_tab(string position, bool switchSetting, bool plusButton)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var window = new MainWindow();
            try
            {
                // Pristine in-memory settings (never the user's): New tab opens = Welcome tab.
                SetField(window, "_settings", new GlideSettingsState { NewTabPosition = position, SwitchToNewTabs = switchSetting });
                var workspace = (WorkspaceState)GetField(window, "_workspace")!;
                workspace.Reset(Enumerable.Range(0, 3).Select(_ => (TabState)new HomeTabState(Guid.NewGuid())).ToArray(), 0);
                var before = workspace.Tabs.Select(t => t.Id).ToHashSet();

                if (plusButton)
                {
                    window.FindControl<Button>("NewTabButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
                }
                else await (Task)Invoke(window, "ExecuteCommandAsync", GlideCommand.NewTab)!;

                Assert.Equal(4, workspace.Tabs.Count);
                var added = workspace.Tabs.Single(t => !before.Contains(t.Id));
                Assert.Equal(3, workspace.Tabs.ToList().IndexOf(added)); // at the end, whatever "New tabs open at" says
                Assert.Same(added, workspace.Active); // always switches, even with the switch setting off
            }
            finally
            {
                window.Close();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
        });
        TempDir.TryDelete(root);
    }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static object? Invoke(MainWindow window, string method, params object?[] args)
    {
        var info = typeof(MainWindow).GetMethods(Flags).Single(m => m.Name == method && m.GetParameters().Length == args.Length);
        return info.Invoke(window, args);
    }

    private static object? GetField(MainWindow window, string name) => typeof(MainWindow).GetField(name, Flags)!.GetValue(window);

    private static void SetField(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Flags)!.SetValue(window, value);
}
