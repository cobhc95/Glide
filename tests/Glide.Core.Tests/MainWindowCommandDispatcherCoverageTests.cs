using Glide.App.Services;
using Glide.Core.Commands;

namespace Glide.Core.Tests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class MainWindowCommandDispatcherCoverageTests
{
    [Fact]
    public void MissingCommands_excludes_None_and_reports_only_unmapped_semantic_actions()
    {
        var commands = Enum.GetValues<GlideCommand>().Where(command => command != GlideCommand.None).ToArray();
        Assert.Empty(MainWindowCommandDispatcher.MissingCommands(commands));
        Assert.Contains(GlideCommand.OpenFile, MainWindowCommandDispatcher.MissingCommands(commands.Where(command => command != GlideCommand.OpenFile)));
    }

    [Fact]
    public void AssertComplete_rejects_enabled_but_unmapped_semantic_actions()
    {
        var incomplete = Enum.GetValues<GlideCommand>()
            .Where(command => command != GlideCommand.None && command != GlideCommand.Settings);
        var error = Assert.Throws<InvalidOperationException>(() => MainWindowCommandDispatcher.AssertComplete(incomplete));
        Assert.Contains(nameof(GlideCommand.Settings), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Actual_MainWindow_production_map_contains_every_semantic_command()
    {
        // This creates the real MainWindow command dictionary; it is not an Enum.GetValues surrogate.
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var window = new Glide.App.MainWindow();
            try
            {
                var mapped = window.ProductionMappedCommandsForTests();
                // Derive the expectation from the enum so the count can never silently drift again.
                var expected = Enum.GetValues<GlideCommand>().Count(command => command != GlideCommand.None);
                Assert.Equal(expected, mapped.Count);
                Assert.Empty(MainWindowCommandDispatcher.MissingCommands(mapped));
            }
            finally { window.Close(); }
        });
    }
}
