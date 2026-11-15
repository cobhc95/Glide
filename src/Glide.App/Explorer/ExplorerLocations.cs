namespace Glide.App.Explorer;

internal sealed record ExplorerLocation(string Name, string Path, string Icon);

/// <summary>Navigation-pane content: Quick access folders and This PC drives.</summary>
internal static class ExplorerLocations
{
    /// <summary>Test seams: replace the real known folders / drives with temp folders.</summary>
    internal static Func<IReadOnlyList<ExplorerLocation>>? QuickAccessOverride { get; set; }
    internal static Func<IReadOnlyList<ExplorerLocation>>? DrivesOverride { get; set; }

    public static IReadOnlyList<ExplorerLocation> QuickAccess()
    {
        if (QuickAccessOverride is { } overridden) return overridden();
        var list = new List<ExplorerLocation>();
        void Add(string name, string? path, string icon)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (!Directory.Exists(path)) return;
                var full = System.IO.Path.GetFullPath(path);
                if (list.Any(l => string.Equals(l.Path, full, StringComparison.OrdinalIgnoreCase))) return;
                list.Add(new ExplorerLocation(name, full, icon));
            }
            catch { /* an unreachable location is simply left out of the list */ }
        }
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop");
        Add("Downloads", Platform.WindowsKnownFolders.DownloadsIfExists(), "Download");
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Document");
        Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Image");
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (string.IsNullOrWhiteSpace(oneDrive))
            oneDrive = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive");
        Add("OneDrive", oneDrive, "Cloud");
        return list;
    }

    public static IReadOnlyList<ExplorerLocation> Drives()
    {
        if (DrivesOverride is { } overridden) return overridden();
        var list = new List<ExplorerLocation>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? drive.DriveType == DriveType.Removable ? "Removable Disk"
                        : drive.DriveType == DriveType.Network ? "Network Drive" : "Local Disk"
                        : drive.VolumeLabel;
                    list.Add(new ExplorerLocation($"{label} ({drive.Name.TrimEnd('\\')})", drive.RootDirectory.FullName, "Drive"));
                }
                catch { /* a drive that cannot be queried (ejected, offline) is left out of the list */ }
            }
        }
        catch { /* drive enumeration failure leaves the other locations intact */ }
        return list;
    }
}
