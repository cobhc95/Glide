namespace Glide.Core.Tests;

/// <summary>
/// Tests that touch process-wide store state: WorkspaceSessionStore's write sequence / dedupe / imported-session flag,
/// PrintSettingsStore.DirectoryOverrideForTests and the GLIDE_SETTINGS_DIRECTORY environment variable. They share one
/// collection so they never overlap each other (or any other parallel collection) while that state is changing.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StoreStaticsCollection
{
    public const string Name = "Process-wide store statics";
}
