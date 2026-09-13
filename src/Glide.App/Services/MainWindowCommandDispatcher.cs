using Glide.Core.Commands;

namespace Glide.App.Services;

/// <summary>
/// Small command-routing boundary for the composition root.  MainWindow supplies the
/// already-owned actions; this class owns lookup, unknown-command handling and the
/// testable one-command-at-a-time execution contract.
/// </summary>
public sealed class MainWindowCommandDispatcher
{
    private readonly IReadOnlyDictionary<GlideCommand, Func<Task>> _actions;

    public MainWindowCommandDispatcher(IReadOnlyDictionary<GlideCommand, Func<Task>> actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }


    public static IReadOnlyList<GlideCommand> MissingCommands(IEnumerable<GlideCommand> commands)
    {
        var supplied = commands.ToHashSet();
        return Enum.GetValues<GlideCommand>()
            .Where(command => command != GlideCommand.None && !supplied.Contains(command))
            .ToArray();
    }

    public static void AssertComplete(IEnumerable<GlideCommand> commands)
    {
        var missing = MissingCommands(commands);
        if (missing.Count != 0)
            throw new InvalidOperationException($"Semantic command dispatcher is incomplete: {string.Join(", ", missing)}");
    }

    internal IReadOnlyCollection<GlideCommand> MappedCommands => _actions.Keys.ToArray();

    public bool CanExecute(GlideCommand command) => _actions.ContainsKey(command);

    public Task ExecuteAsync(GlideCommand command) =>
        _actions.TryGetValue(command, out var action) ? action() : Task.CompletedTask;
}
