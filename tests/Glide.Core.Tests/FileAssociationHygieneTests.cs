using Glide.App.Platform;

namespace Glide.Core.Tests;

/// <summary>
/// Policy tests for keeping dev / portable builds from overriding the installed Glide's file associations. They use a
/// fake registry view and fake file system: nothing touches the real registry or launches Glide.
/// </summary>
[Trait("Area", "Shell")]
public sealed class FileAssociationHygieneTests
{
    private const string Installed = @"C:\Program Files\Glide\Glide.exe";
    private const string Dev = @"C:\Users\me\Downloads\Glide Image Viewer\dist\Glide.exe";

    private sealed class FakeView(Dictionary<string, string?> commands) : IGlideClassesView
    {
        public IReadOnlyList<string> GlideClassNames() => commands.Keys.ToArray();
        public string? OpenCommand(string className) => commands.TryGetValue(className, out var c) ? c : null;
    }

    private static string Cmd(string exe) => $"\"{exe}\" \"%1\"";

    private static FakeView View(string exe, params string[] classes) =>
        new(classes.ToDictionary(c => c, _ => (string?)Cmd(exe)));

    private static readonly string[] Classes =
        { "Glide.Image", "Glide.Image.png", "Glide.Image.jpg", @"Applications\Glide.exe" };

    // ---- source tree detection ------------------------------------------------------------

    [Fact]
    public void Dist_folder_inside_a_repo_with_build_cmd_and_sln_is_a_source_tree()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Users\me\Downloads\Glide Image Viewer\build.cmd",
            @"C:\Users\me\Downloads\Glide Image Viewer\Glide.sln",
        };
        Assert.True(FileAssociationHygiene.IsInSourceTree(Dev, files.Contains));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\Glide Image Viewer\.claude\worktrees\agent-1\dist\Glide.exe")]
    [InlineData(@"D:\src\Glide\src\Glide.App\bin\Debug\net10.0\Glide.exe")]
    [InlineData(@"D:\src\Glide\src\Glide.App\bin\Release\net10.0\win-x64\Glide.exe")]
    [InlineData(@"D:\src\Glide\src\Glide.App\obj\Release\Glide.exe")]
    public void Build_output_and_worktree_paths_are_source_trees_without_any_marker(string exe)
        => Assert.True(FileAssociationHygiene.IsInSourceTree(exe, _ => false));

    [Theory]
    [InlineData(@"C:\Program Files\Glide\Glide.exe")]
    [InlineData(@"D:\Tools\Glide Portable\Glide.exe")]
    [InlineData(@"D:\bin\Glide\Glide.exe")]
    public void Installed_and_portable_copies_are_not_source_trees(string exe)
        => Assert.False(FileAssociationHygiene.IsInSourceTree(exe, _ => false));

    [Fact]
    public void A_git_repo_alone_does_not_make_a_portable_copy_a_source_tree()
    {
        // e.g. a dotfiles repo in the home folder: needs build.cmd AND Glide.sln
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Users\me\.git", @"C:\Users\me\build.cmd" };
        Assert.False(FileAssociationHygiene.IsInSourceTree(@"C:\Users\me\Apps\Glide\Glide.exe", files.Contains));
    }

    // ---- command parsing -----------------------------------------------------------------

    [Theory]
    [InlineData("\"C:\\Program Files\\Glide\\Glide.exe\" \"%1\"", @"C:\Program Files\Glide\Glide.exe")]
    [InlineData("C:\\Glide\\Glide.exe %1", @"C:\Glide\Glide.exe")]
    [InlineData("C:\\Glide App\\Glide.exe \"%1\"", @"C:\Glide App\Glide.exe")]
    public void Executable_is_read_from_the_open_command(string command, string expected)
        => Assert.Equal(expected, FileAssociationHygiene.ExecutableOf(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"")]
    public void Empty_commands_name_no_executable(string? command)
        => Assert.Null(FileAssociationHygiene.ExecutableOf(command));

    // ---- machine-wide detection and auto-registration policy --------------------------------

    [Fact]
    public void Any_machine_wide_glide_registration_with_an_existing_exe_counts_whatever_the_path()
    {
        var machine = View(@"D:\Apps\Elsewhere\Glide.exe", "Glide.Image.png");
        Assert.True(FileAssociationHygiene.HasMachineWideRegistration(machine, p => p == @"D:\Apps\Elsewhere\Glide.exe"));
    }

    [Fact]
    public void A_machine_wide_registration_of_a_missing_exe_does_not_count()
    {
        var machine = View(Installed, Classes);
        Assert.False(FileAssociationHygiene.HasMachineWideRegistration(machine, _ => false));
        Assert.False(FileAssociationHygiene.HasMachineWideRegistration(View(Installed), _ => true));
    }

    [Fact]
    public void Only_glide_exe_counts_as_a_machine_wide_registration()
        => Assert.False(FileAssociationHygiene.HasMachineWideRegistration(View(@"C:\Other\Other.exe", "Glide.Image"), _ => true));

    [Fact]
    public void Dev_builds_never_auto_register_even_with_nothing_installed()
    {
        Assert.False(FileAssociationHygiene.ShouldAutoRegister(
            inSourceTree: true, anyMachineWideRegistration: false, optedOut: false, registeredVersion: null, currentVersion: "5.2.0.0"));
        Assert.False(WindowsFileAssociationRegistration.ShouldAutoRegister(
            machineWideRegistered: false, optedOut: false, registeredVersion: null, currentVersion: "5.2.0.0", inSourceTree: true));
    }

    [Fact]
    public void Any_machine_wide_registration_blocks_per_user_registration()
        => Assert.False(FileAssociationHygiene.ShouldAutoRegister(
            inSourceTree: false, anyMachineWideRegistration: true, optedOut: false, registeredVersion: null, currentVersion: "5.2.0.0"));

    [Fact]
    public void A_portable_copy_outside_a_source_tree_with_nothing_installed_still_self_registers()
        => Assert.True(FileAssociationHygiene.ShouldAutoRegister(
            inSourceTree: false, anyMachineWideRegistration: false, optedOut: false, registeredVersion: null, currentVersion: "5.2.0.0"));

    [Fact]
    public void Healing_never_runs_from_a_dev_build()
    {
        Assert.False(FileAssociationHygiene.ShouldHealUserRegistration(inSourceTree: true));
        Assert.True(FileAssociationHygiene.ShouldHealUserRegistration(inSourceTree: false));
    }

    // ---- stale per-user cleanup ---------------------------------------------------------------

    [Fact]
    public void The_reported_case_removes_every_per_user_class_pointing_at_the_dev_dist_exe()
    {
        var user = View(Dev, Classes);
        // dist\Glide.exe still exists on disk but lives in a source tree (marker files present).
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Dev,
            @"C:\Users\me\Downloads\Glide Image Viewer\build.cmd",
            @"C:\Users\me\Downloads\Glide Image Viewer\Glide.sln",
        };
        var plan = FileAssociationHygiene.PlanStaleCleanup(user, Installed, present.Contains, present.Contains);
        Assert.Equal(Classes.OrderBy(x => x), plan.StaleClassNames.OrderBy(x => x));
        Assert.True(plan.RemoveAll);
    }

    [Fact]
    public void Entries_pointing_at_a_missing_exe_are_stale()
    {
        var plan = FileAssociationHygiene.PlanStaleCleanup(View(@"D:\Gone\Glide.exe", Classes), Installed, _ => false, _ => false);
        Assert.Equal(Classes.Length, plan.StaleClassNames.Count);
        Assert.True(plan.RemoveAll);
    }

    [Fact]
    public void Entries_pointing_at_this_executable_are_kept()
    {
        var plan = FileAssociationHygiene.PlanStaleCleanup(View(Installed, Classes), Installed, _ => false, _ => false);
        Assert.True(plan.IsEmpty);
        Assert.False(plan.RemoveAll);
    }

    [Fact]
    public void Entries_pointing_at_another_existing_portable_copy_are_kept()
    {
        const string portable = @"D:\Tools\Glide Portable\Glide.exe";
        var plan = FileAssociationHygiene.PlanStaleCleanup(View(portable, Classes), Installed, p => p == portable, _ => false);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void A_mix_removes_only_the_stale_classes_and_keeps_the_per_user_registration()
    {
        var commands = new Dictionary<string, string?>
        {
            ["Glide.Image.png"] = Cmd(Dev),
            ["Glide.Image.jpg"] = Cmd(Installed),
        };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Installed };
        var plan = FileAssociationHygiene.PlanStaleCleanup(new FakeView(commands), Installed, present.Contains, _ => false);
        Assert.Equal(new[] { "Glide.Image.png" }, plan.StaleClassNames);
        Assert.False(plan.RemoveAll);
    }

    [Fact]
    public void A_class_without_a_command_is_left_alone()
    {
        var commands = new Dictionary<string, string?> { ["Glide.Image"] = null };
        Assert.True(FileAssociationHygiene.PlanStaleCleanup(new FakeView(commands), Installed, _ => false, _ => false).IsEmpty);
    }

    [Fact]
    public void An_empty_hive_has_nothing_to_clean()
        => Assert.True(FileAssociationHygiene.PlanStaleCleanup(new FakeView(new()), Installed, _ => false, _ => false).IsEmpty);

    // ---- wiring -------------------------------------------------------------------------------

    [Fact]
    public void Startup_registration_consults_the_hygiene_policy_before_writing_anything()
    {
        var source = SourceText.Read("src/Glide.App/Platform/WindowsFileAssociationRegistration.cs");
        Assert.Contains("FileAssociationHygiene.IsInSourceTree(executable", source);
        Assert.Contains("FileAssociationHygiene.HasMachineWideRegistration(", source);
        Assert.Contains("HealStaleUserRegistration(executable)", source);
        Assert.Contains("if (inSourceTree || machineWideRegistered) return;", source);
        // the source-tree / machine-wide gate precedes every per-user write in the startup path
        Assert.True(source.IndexOf("if (inSourceTree || machineWideRegistered) return;", StringComparison.Ordinal) <
                    source.IndexOf("ReleaseOrphanedClaims(Registry.CurrentUser);", StringComparison.Ordinal));
    }
}
