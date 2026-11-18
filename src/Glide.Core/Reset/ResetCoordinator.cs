using Glide.Core.Data;
namespace Glide.Core.Reset;

/// <summary>Everything the reset flow touches, in one place so tests can hand it scratch folders and fakes.</summary>
public sealed class ResetServices
{
    public required ResetEnvironment Environment { get; init; }
    public required IResetFileSystem FileSystem { get; init; }
    public required IResetRegistry Registry { get; init; }
    public required IResetProcessHost Processes { get; init; }
    /// <summary>The folder that receives "Glide backup ..." folders (Documents\Glide backups).</summary>
    public required string BackupRoot { get; init; }
    /// <summary>The Glide.exe to start again (null: the restart is skipped and reported).</summary>
    public string? ExecutablePath { get; init; }
    public int ProcessId { get; init; }
    public string AppVersion { get; init; } = string.Empty;
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    public ResetExecutorOptions? ExecutorOptions { get; init; }
}

public sealed record ResetRequest(IReadOnlyList<ResetItemId> Items, bool SaveBackup);

public enum ResetOutcome
{
    /// <summary>The backup could not be written: nothing was changed.</summary>
    BackupFailed,
    /// <summary>The wipe ran in place (caches, registry); see <see cref="ResetResult.Report"/>.</summary>
    Completed,
    /// <summary>A helper was started; Glide must close now, the helper wipes and starts Glide again.</summary>
    RestartScheduled,
    /// <summary>The helper could not be started: nothing was changed.</summary>
    HelperFailed,
}

public sealed record ResetResult(ResetOutcome Outcome, ResetPlan Plan, string? BackupFolder, ResetReport? Report, string? Error);

/// <summary>
/// The whole flow behind the confirmation dialog: plan, optional backup, then either wipe in place or hand over to the
/// helper process (which waits for Glide to exit, wipes, reports problems and starts Glide again).
/// </summary>
public static class ResetCoordinator
{
    public static ResetResult Run(ResetRequest request, ResetServices services)
    {
        var plan = ResetPlanner.Plan(services.Environment, request.Items);
        string? backupFolder = null;
        if (request.SaveBackup)
        {
            var backup = ResetBackup.Create(services.Environment, plan.Items.Select(x => x.Item), services.FileSystem,
                services.BackupRoot, services.Now(), services.AppVersion);
            if (!backup.Success)
                return new ResetResult(ResetOutcome.BackupFailed, plan, backup.Folder, null,
                    "The backup could not be written, so nothing was reset: " + backup.Error);
            backupFolder = backup.Folder;
        }

        if (!plan.NeedsRestart)
        {
            var report = ResetExecutor.Execute(plan, services.FileSystem, services.Registry, services.ExecutorOptions);
            return new ResetResult(ResetOutcome.Completed, plan, backupFolder, report, null);
        }

        if (string.IsNullOrWhiteSpace(services.ExecutablePath))
            return new ResetResult(ResetOutcome.HelperFailed, plan, backupFolder, null, "Glide could not find its own program file, so nothing was reset.");
        var arguments = ResetHelperArguments.Build(plan.Items.Select(x => x.Item.Id), services.ProcessId, services.Environment.SettingsDirectory);
        if (!services.Processes.Start(services.ExecutablePath, arguments, out var error))
            return new ResetResult(ResetOutcome.HelperFailed, plan, backupFolder, null, "Glide could not start the reset helper, so nothing was reset: " + error);
        return new ResetResult(ResetOutcome.RestartScheduled, plan, backupFolder, null, null);
    }
}

/// <summary>Command line of the helper: <c>Glide.exe --reset-glide --items AllSettings,Caches --wait-pid 1234 --settings-dir "C:\..."</c>.</summary>
public sealed record ResetHelperArguments(IReadOnlyList<ResetItemId> Items, int WaitProcessId, string SettingsDirectory)
{
    public const string Switch = "--reset-glide";

    public static IReadOnlyList<string> Build(IEnumerable<ResetItemId> items, int waitProcessId, string settingsDirectory) =>
        [Switch, "--items", string.Join(",", items), "--wait-pid", waitProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--settings-dir", settingsDirectory];

    public static bool IsHelperCommand(IReadOnlyList<string> args) =>
        args.Count >= 1 && string.Equals(args[0], Switch, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(IReadOnlyList<string> args, out ResetHelperArguments? parsed)
    {
        parsed = null;
        if (!IsHelperCommand(args)) return false;
        string? items = null, pid = null, settings = null;
        for (var i = 1; i + 1 < args.Count; i += 2)
        {
            if (args[i].Equals("--items", StringComparison.OrdinalIgnoreCase)) items = args[i + 1];
            else if (args[i].Equals("--wait-pid", StringComparison.OrdinalIgnoreCase)) pid = args[i + 1];
            else if (args[i].Equals("--settings-dir", StringComparison.OrdinalIgnoreCase)) settings = args[i + 1];
        }
        if (string.IsNullOrWhiteSpace(items) || string.IsNullOrWhiteSpace(settings)) return false;
        var ids = new List<ResetItemId>();
        foreach (var part in items.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<ResetItemId>(part, ignoreCase: false, out var id) || !Enum.IsDefined(id)) return false;
            ids.Add(id);
        }
        if (ids.Count == 0 || !int.TryParse(pid ?? "0", System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var waitPid)) return false;
        parsed = new ResetHelperArguments(ids, waitPid, settings);
        return true;
    }
}

/// <summary>Shows the helper's one message box (the helper has no window of its own).</summary>
public interface IResetUserMessage
{
    void Show(string title, string message);
}

/// <summary>The helper's work: wait for Glide to exit, wipe, tell the user about problems, start Glide again.</summary>
public static class ResetHelper
{
    public static readonly TimeSpan WaitForGlide = TimeSpan.FromSeconds(60);

    public static int Run(ResetHelperArguments arguments, ResetServices services, IResetUserMessage ui)
    {
        const string Title = "Reset Glide";
        if (arguments.WaitProcessId > 0 && !services.Processes.WaitForExit(arguments.WaitProcessId, WaitForGlide))
        {
            ui.Show(Title, "Glide did not close in time, so nothing was reset. Close Glide and try again from Settings > Windows & developer > Reset Glide.");
            return 2;
        }

        var plan = ResetPlanner.Plan(services.Environment, arguments.Items);
        var report = ResetExecutor.Execute(plan, services.FileSystem, services.Registry, services.ExecutorOptions);

        var restartNote = string.Empty;
        if (!string.IsNullOrWhiteSpace(services.ExecutablePath))
        {
            if (!services.Processes.Start(services.ExecutablePath, [], out var error))
                restartNote = "\n\nGlide could not be started again automatically (" + error + "). Start it from the Start menu.";
        }
        else restartNote = "\n\nGlide could not find its program file to start again. Start it from the Start menu.";

        if (!report.AllSucceeded)
            ui.Show(Title, "Some items could not be reset:\n\n" + report.Describe() + "\n\nGlide is starting now; the items listed above are unchanged." + restartNote);
        else if (restartNote.Length > 0)
            ui.Show(Title, "The selected items were reset." + restartNote);
        return report.AllSucceeded ? 0 : 1;
    }
}
