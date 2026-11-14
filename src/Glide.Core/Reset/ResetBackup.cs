using System.Globalization;
using System.Text;

namespace Glide.Core.Reset;

public sealed record ResetBackupResult(bool Success, string? Folder, IReadOnlyList<string> Files, string? Error);

/// <summary>
/// Writes a timestamped copy of Glide's user data (settings, profiles, last session, history and the small settings files)
/// into a new folder before anything is wiped. A backup that cannot be completed stops the reset.
/// </summary>
public static class ResetBackup
{
    /// <summary>The files copied when they exist: everything a person would be sorry to lose and cannot rebuild.</summary>
    public static IReadOnlyList<string> UserDataFileNames { get; } =
    [
        ResetCatalog.SettingsFileName,
        ResetCatalog.ProfilesFileName,
        ResetCatalog.SessionFileName,
        ResetCatalog.HistoryFileName,
        ResetCatalog.ThumbnailSettingsFileName,
        ResetCatalog.PrintSettingsFileName,
        ResetCatalog.OverlayLayoutFileName,
    ];

    public static string FolderNameFor(DateTime now) => "Glide backup " + now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);

    public static ResetBackupResult Create(
        ResetEnvironment env, IEnumerable<ResetItemInfo> selected, IResetFileSystem fs, string backupRoot, DateTime now, string appVersion)
    {
        string? folder = null;
        var copied = new List<string>();
        try
        {
            folder = Path.Combine(backupRoot, FolderNameFor(now));
            for (var n = 2; fs.DirectoryExists(folder); n++) folder = Path.Combine(backupRoot, FolderNameFor(now) + " (" + n + ")");
            fs.CreateDirectory(folder);

            foreach (var name in UserDataFileNames)
            {
                var source = Path.Combine(env.SettingsDirectory, name);
                if (!fs.FileExists(source)) continue;
                var target = Path.Combine(folder, name);
                fs.CopyFile(source, target);
                // "Complete" means the copy is as long as the original at the time of the copy; a short copy stops the reset.
                if (fs.FileLength(target) != fs.FileLength(source))
                    throw new IOException($"The copy of {name} is incomplete.");
                copied.Add(name);
            }

            var info = new StringBuilder();
            info.AppendLine("Glide backup made before Reset Glide");
            info.AppendLine("Created: " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            info.AppendLine("Glide version: " + appVersion);
            info.AppendLine("Settings folder: " + env.SettingsDirectory);
            info.AppendLine("Reset items: " + string.Join(", ", selected.Select(x => x.Title)));
            info.AppendLine();
            info.AppendLine(copied.Count == 0 ? "No settings files existed yet, so nothing was copied." : "Files copied:");
            foreach (var name in copied) info.AppendLine("  " + name);
            info.AppendLine();
            info.AppendLine("To restore: close Glide, then copy these files back into the settings folder above.");
            info.AppendLine("Windows registry entries, caches and the downloaded video codec pack are not part of the backup.");
            fs.WriteAllTextAtomic(Path.Combine(folder, "backup-info.txt"), info.ToString());
            return new ResetBackupResult(true, folder, copied, null);
        }
        catch (Exception ex)
        {
            return new ResetBackupResult(false, folder, copied, ex.Message);
        }
    }
}
