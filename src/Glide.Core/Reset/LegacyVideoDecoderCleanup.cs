namespace Glide.Core.Reset;

/// <summary>
/// Glide 4.6 to 5.0.3 could download an optional FFmpeg video decoder into %LOCALAPPDATA%\Glide\ffmpeg-&lt;version&gt;
/// (and left ffmpeg-download-&lt;guid&gt; folders behind when a download was interrupted). From 5.0.4 Glide plays videos
/// only with the codecs installed in Windows and never loads those files, so they are removed: once per run on startup
/// idle (<see cref="RemoveLeftovers"/>), and by Reset Glide's "Image and thumbnail caches" item. Both go through the
/// Reset executor, so <see cref="ResetPathGuard"/> keeps the delete inside Glide's own folder.
/// <para>This is the only file in src\ allowed to name the old decoder (the no-FFmpeg source test exempts it).</para>
/// </summary>
public static class LegacyVideoDecoderCleanup
{
    /// <summary>Folders of the old downloaded decoder (and of interrupted downloads) inside %LOCALAPPDATA%\Glide.</summary>
    public const string FolderPattern = "ffmpeg-*";

    public static ResetTarget Target(ResetEnvironment environment) =>
        new DirectoriesMatchingTarget(environment.LocalGlideDirectory, FolderPattern);

    /// <summary>
    /// Deletes any leftover decoder folders in <paramref name="localGlideDirectory"/>. Cheap when there are none (one
    /// directory listing). Returns the number of folders removed; never throws.
    /// </summary>
    public static int RemoveLeftovers(string localGlideDirectory, IResetFileSystem? fileSystem = null)
    {
        try
        {
            fileSystem ??= PhysicalResetFileSystem.Instance;
            if (!fileSystem.DirectoryExists(localGlideDirectory) || !fileSystem.EnumerateDirectories(localGlideDirectory, FolderPattern).Any())
                return 0;
            var environment = new ResetEnvironment(
                SettingsDirectory: localGlideDirectory,
                LocalGlideDirectory: localGlideDirectory,
                TempDirectory: Path.GetTempPath(),
                InstallDirectory: AppContext.BaseDirectory,
                ImageExtensions: []);
            var plan = new ResetPlan(environment, [new ResetItemPlan(ResetCatalog.Info(ResetItemId.Caches), [Target(environment)])]);
            var report = ResetExecutor.Execute(plan, fileSystem, NoRegistry.Instance, new ResetExecutorOptions { Attempts = 2 });
            var result = report.Items.SelectMany(x => x.Targets).FirstOrDefault();
            return result?.Outcome == ResetTargetOutcome.Removed && result.Detail is { } detail && int.TryParse(detail.Split(' ')[0], out var removed)
                ? removed : 0;
        }
        catch (Exception)
        {
            return 0; // best effort: a locked or missing folder is retried on the next start
        }
    }

    /// <summary>The cleanup touches files only.</summary>
    private sealed class NoRegistry : IResetRegistry
    {
        public static readonly NoRegistry Instance = new();
        public bool KeyExists(string subKey) => false;
        public bool DeleteKeyTree(string subKey) => false;
        public string? GetStringValue(string subKey, string valueName) => null;
        public bool ValueExists(string subKey, string valueName) => false;
        public bool DeleteValue(string subKey, string valueName) => false;
        public void SetDword(string subKey, string valueName, int value) { }
    }
}
