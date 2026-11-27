using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Glide.Core;

namespace Glide.App.Platform;

/// <summary>
/// Windows Open-With/Capabilities registration for every extension Glide recognises. Glide adds
/// itself automatically — per-user on first launch and machine-wide from the installer — so the user
/// never has to configure it from Options. This deliberately never writes (or forges) a UserChoice default
/// association: Windows remains the authority for the user's default-app selection.
///
/// Default viewer, the legitimate way: for every format Glide can open that has no handler at all (no
/// UserChoice, no other app's default ProgID or OpenWithProgids entry), registration sets Glide's ProgID as the
/// extension's default value in the hive being registered, so otherwise unowned types open in Glide. Another
/// app's default is never overwritten, every claim is recorded under Software\Glide\ClaimedDefaults, and
/// unregistering removes exactly those (only while the value is still Glide's). For everything else the user
/// picks Glide on Windows' own Default apps page (<see cref="OpenDefaultAppsPage"/>).
/// </summary>
internal static class WindowsFileAssociationRegistration
{
    private const string ApplicationName = "Glide";
    private const string ApplicationExe = "Glide.exe";
    private const string ProgId = "Glide.Image";
    /// <summary>Folder next to Glide.exe holding the per-extension document icons (round 4 I1).</summary>
    internal const string FileTypeIconFolder = "FileTypes";

    /// <summary>
    /// The ProgID an extension is registered under: <c>Glide.Image.png</c> when a document icon for it ships in
    /// <see cref="FileTypeIconFolder"/> (so Explorer shows a "PNG" page with the Glide mark, like ImageGlass), else
    /// the generic <c>Glide.Image</c> with the app icon.
    /// </summary>
    internal static string ProgIdFor(string extension, bool hasTypeIcon) =>
        hasTypeIcon ? $"{ProgId}.{extension.TrimStart('.').ToLowerInvariant()}" : ProgId;

    /// <summary>The generic ProgID or any per-extension one.</summary>
    internal static bool IsGlideProgId(string? progId) =>
        progId is not null && (progId.Equals(ProgId, StringComparison.OrdinalIgnoreCase) ||
                               progId.StartsWith(ProgId + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>The document icon for an extension, or null when none ships (compound suffixes never have one).</summary>
    internal static string? FileTypeIconPath(string executableDirectory, string extension, Func<string, bool>? exists = null)
    {
        if (extension.LastIndexOf('.') != 0) return null;
        var path = Path.Combine(executableDirectory, FileTypeIconFolder, extension.TrimStart('.').ToLowerInvariant() + ".ico");
        return (exists ?? File.Exists)(path) ? path : null;
    }

    internal const string GenericIconFile = "generic.ico";

    /// <summary>DefaultIcon of <c>Applications\Glide.exe</c>: the unlabelled document icon when it ships, else the app icon.</summary>
    internal static string ApplicationDefaultIcon(string executable, Func<string, bool>? exists = null)
    {
        var generic = Path.Combine(Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory, FileTypeIconFolder, GenericIconFile);
        return (exists ?? File.Exists)(generic) ? $"\"{generic}\",0" : $"\"{executable}\",0";
    }

    private static string ProgIdFor(string extension, string executable) =>
        ProgIdFor(extension, FileTypeIconPath(Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory, extension) is not null);

    private const string StateKeyPath = @"Software\Glide";
    private const string RegisteredVersionValue = "OpenWithRegistrationVersion";
    private const string OptOutValue = "OpenWithOptOut";
    private const string ClaimedDefaultsKeyPath = @"Software\Glide\ClaimedDefaults";
    private const string UserChoiceRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>
    /// Automatic per-user integration, intended to run once per version during startup, off the UI
    /// thread. Idempotent and silent. It never registers from a source tree / dev build (dist, bin, obj, a worktree)
    /// and never when ANY machine-wide Glide registration exists (a per-user copy would override the installed one),
    /// and it skips when the user has explicitly removed the registration. From the installed Glide it also removes
    /// stale per-user registrations left behind by dev builds (<see cref="HealStaleUserRegistration"/>).
    /// </summary>
    public static void EnsureRegisteredQuietly()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;

            var inSourceTree = FileAssociationHygiene.IsInSourceTree(executable, PathExists);
            var machineWideRegistered = FileAssociationHygiene.HasMachineWideRegistration(new RegistryClassesView(Registry.LocalMachine), File.Exists);

            // Any non-dev Glide (the installed one above all) cleans up what dev builds / vanished copies left in HKCU.
            if (FileAssociationHygiene.ShouldHealUserRegistration(inSourceTree))
                HealStaleUserRegistration(executable);

            // A dev build, or a PC with an installed Glide, never self-registers per user.
            if (inSourceTree || machineWideRegistered) return;

            // A reset that deleted Glide's per-user ProgID but not the defaults it claimed would leave those
            // extensions pointing at nothing; release them first (no-op when nothing was claimed).
            ReleaseOrphanedClaims(Registry.CurrentUser);

            using var state = Registry.CurrentUser.CreateSubKey(StateKeyPath, writable: true);
            if (state is null) return;
            var optedOut = state.GetValue(OptOutValue) is int optOut && optOut != 0;
            var registeredVersion = state.GetValue(RegisteredVersionValue) as string;

            if (!ShouldAutoRegister(machineWideRegistered, optedOut, registeredVersion, CurrentVersion(), inSourceTree)) return;

            Register(out _);
        }
        catch { /* Best-effort: association setup must never disturb startup. */ }
    }

    /// <summary>
    /// Pure decision for whether startup should add the per-user registration. Kept separate from the
    /// registry I/O so the policy is unit-testable.
    /// </summary>
    internal static bool ShouldAutoRegister(bool machineWideRegistered, bool optedOut, string? registeredVersion, string currentVersion,
        bool inSourceTree = false) =>
        FileAssociationHygiene.ShouldAutoRegister(inSourceTree, machineWideRegistered, optedOut, registeredVersion, currentVersion);

    /// <summary>
    /// Removes per-user (HKCU) Glide ProgIDs and Applications\Glide.exe whose command points at another Glide.exe that
    /// is missing or lives in a source tree / dist folder: such a registration overrides the installed one. When no
    /// live per-user Glide class remains, the whole per-user registration goes (claimed defaults, capabilities, Open
    /// With entries) without setting the opt-out. Best effort.
    /// </summary>
    internal static void HealStaleUserRegistration(string thisExecutable)
    {
        try
        {
            var user = Registry.CurrentUser;
            var plan = FileAssociationHygiene.PlanStaleCleanup(new RegistryClassesView(user), thisExecutable, File.Exists, PathExists);
            if (plan.IsEmpty) return;

            if (plan.RemoveAll)
            {
                DeleteRegistration(user);
                using var state = user.OpenSubKey(StateKeyPath, writable: true);
                state?.DeleteValue(RegisteredVersionValue, throwOnMissingValue: false);
            }
            else
            {
                foreach (var name in plan.StaleClassNames)
                    user.DeleteSubKeyTree($@"Software\Classes\{name}", throwOnMissingSubKey: false);
            }
            NotifyAssociationChanged();
        }
        catch { /* best effort: a stale entry only affects the per-user override */ }
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Registry-backed <see cref="IGlideClassesView"/> over one hive's Software\Classes.</summary>
    private sealed class RegistryClassesView(RegistryKey hive) : IGlideClassesView
    {
        public IReadOnlyList<string> GlideClassNames()
        {
            var names = new List<string>();
            try
            {
                using var classes = hive.OpenSubKey(@"Software\Classes");
                if (classes is not null)
                    names.AddRange(classes.GetSubKeyNames().Where(IsGlideProgId));
                using var application = hive.OpenSubKey($@"Software\Classes\Applications\{ApplicationExe}");
                if (application is not null) names.Add($@"Applications\{ApplicationExe}");
            }
            catch { /* unreadable hive: report what was found */ }
            return names;
        }

        public string? OpenCommand(string className)
        {
            try
            {
                using var key = hive.OpenSubKey($@"Software\Classes\{className}\shell\open\command");
                return key?.GetValue(string.Empty) as string;
            }
            catch { return null; }
        }
    }

    /// <summary>Adds Glide to Windows Open With for every recognised extension (current user).</summary>
    public static bool Register(out string message) => RegisterInHive(Registry.CurrentUser, perUser: true, out message);

    /// <summary>Adds Glide to Windows Open With for every recognised extension (all users; needs elevation).</summary>
    // HKLM writes require elevation; the installer invokes this via --register-associations
    // from an elevated process. Per-user startup path (HKCU) never attempts HKLM.
    public static bool RegisterMachineWide(out string message) => RegisterInHive(Registry.LocalMachine, perUser: false, out message);

    /// <summary>Removes the current user's registration and opts out of automatic re-registration.</summary>
    public static bool Unregister(out string message) => UnregisterInHive(Registry.CurrentUser, perUser: true, out message);

    /// <summary>Removes the machine-wide (installer) registration.</summary>
    // HKLM removal likewise requires elevation; installer flag --unregister-associations.
    public static bool UnregisterMachineWide(out string message) => UnregisterInHive(Registry.LocalMachine, perUser: false, out message);

    /// <summary>True when this executable is registered in Open With for the current user or machine-wide.</summary>
    public static bool IsRegistered()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return false;
            return IsRegisteredInHive(Registry.CurrentUser, executable) || IsRegisteredInHive(Registry.LocalMachine, executable);
        }
        catch { return false; }
    }

    private static bool RegisterInHive(RegistryKey hive, bool perUser, out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            message = "Windows Open With registration is only available on Windows.";
            return false;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            message = "Glide could not resolve its executable path.";
            return false;
        }

        try
        {
            // File associations launch the real Glide process directly. A separate preview/launcher
            // window makes cold start look like two applications handing off, which is perceptually
            // slower even when the first pixel arrives early. Keep one HWND identity from first paint.
            // Quoting verified: both exe and "%1" are double-quoted so paths with spaces
            // (e.g. C:\Program Files\Glide\Glide.exe) and image paths with spaces/& resolve
            // to a single argv each. No behavior change.
            var command = $"\"{executable}\" \"%1\"";

            using (var app = hive.CreateSubKey($@"Software\Classes\Applications\{ApplicationExe}", writable: true))
            {
                app?.SetValue("FriendlyAppName", ApplicationName, RegistryValueKind.String);
                // The class Windows uses when the user chose Glide through "Open with" (UserChoice =
                // Applications\Glide.exe) instead of a per-extension ProgID. It cannot be per extension, so it gets the
                // unlabelled document icon rather than the bare app icon.
                using (var appIcon = app?.CreateSubKey("DefaultIcon", writable: true))
                    appIcon?.SetValue(string.Empty, ApplicationDefaultIcon(executable), RegistryValueKind.String);
                using var supported = app?.CreateSubKey("SupportedTypes", writable: true);
                if (supported is not null)
                    foreach (var extension in ImageFormatRegistry.Extensions)
                        supported.SetValue(extension, string.Empty, RegistryValueKind.String);
                using var open = app?.CreateSubKey(@"shell\open\command", writable: true);
                open?.SetValue(string.Empty, command, RegistryValueKind.String);
            }

            using (var progId = hive.CreateSubKey($@"Software\Classes\{ProgId}", writable: true))
            {
                progId?.SetValue(string.Empty, "Image file", RegistryValueKind.String);
                progId?.SetValue("FriendlyTypeName", "Glide image", RegistryValueKind.String);
                using var icon = progId?.CreateSubKey("DefaultIcon", writable: true);
                icon?.SetValue(string.Empty, $"{executable},0", RegistryValueKind.String);
                using var open = progId?.CreateSubKey(@"shell\open\command", writable: true);
                open?.SetValue(string.Empty, command, RegistryValueKind.String);
            }

            // Per-extension ProgIDs carry a document icon showing the extension (round 4 I1).
            var executableDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory;
            foreach (var extension in ImageFormatRegistry.Extensions)
            {
                if (FileTypeIconPath(executableDirectory, extension) is not { } iconPath) continue;
                var label = extension.TrimStart('.').ToUpperInvariant();
                using var typed = hive.CreateSubKey($@"Software\Classes\{ProgIdFor(extension, hasTypeIcon: true)}", writable: true);
                typed?.SetValue(string.Empty, $"{label} image", RegistryValueKind.String);
                typed?.SetValue("FriendlyTypeName", $"{label} image", RegistryValueKind.String);
                using var typedIcon = typed?.CreateSubKey("DefaultIcon", writable: true);
                typedIcon?.SetValue(string.Empty, $"\"{iconPath}\",0", RegistryValueKind.String);
                using var typedOpen = typed?.CreateSubKey(@"shell\open\command", writable: true);
                typedOpen?.SetValue(string.Empty, command, RegistryValueKind.String);
            }

            // OpenWithProgids makes Glide discoverable from a file's native Open With menu without
            // attempting to change Windows' protected default-app UserChoice value.
            foreach (var extension in ImageFormatRegistry.Extensions)
            {
                using var openWith = hive.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
                var extensionProgId = ProgIdFor(extension, executable);
                openWith?.SetValue(extensionProgId, Array.Empty<byte>(), RegistryValueKind.None);
                // One Glide entry per Open With list: drop the generic one an earlier version wrote.
                if (!extensionProgId.Equals(ProgId, StringComparison.OrdinalIgnoreCase))
                    openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }

            using (var capabilities = hive.CreateSubKey(@"Software\Glide\Capabilities", writable: true))
            {
                capabilities?.SetValue("ApplicationName", ApplicationName, RegistryValueKind.String);
                capabilities?.SetValue("ApplicationDescription", "Fast lightweight image viewer", RegistryValueKind.String);
                using var associations = capabilities?.CreateSubKey("FileAssociations", writable: true);
                if (associations is not null)
                    foreach (var extension in ImageFormatRegistry.Extensions)
                        associations.SetValue(extension, ProgIdFor(extension, executable), RegistryValueKind.String);
            }

            using (var registered = hive.CreateSubKey(@"Software\RegisteredApplications", writable: true))
                registered?.SetValue(ApplicationName, @"Software\Glide\Capabilities", RegistryValueKind.String);

            // Unowned formats open in Glide by default (never another app's; see the class summary).
            var claimed = ClaimUnownedDefaults(hive, perUser ? Registry.LocalMachine : Registry.CurrentUser, executable);

            if (perUser)
            {
                // Record the version so startup does not rewrite ~600 values every launch, and clear
                // any earlier opt-out because the user has now explicitly (re-)enabled integration.
                using var state = Registry.CurrentUser.CreateSubKey(StateKeyPath, writable: true);
                state?.SetValue(RegisteredVersionValue, CurrentVersion(), RegistryValueKind.String);
                state?.DeleteValue(OptOutValue, throwOnMissingValue: false);
            }

            NotifyAssociationChanged();
            message = $"Glide is available in Windows Open With for {ImageFormatRegistry.Extensions.Count} recognised formats" +
                      (claimed > 0 ? $" and is the default viewer for {claimed} formats no other app handles." : ".");
            return true;
        }
        catch (Exception ex)
        {
            message = $"Open With registration failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>Removes every Glide class, Open With, capability and claimed-default entry from one hive (no opt-out).</summary>
    private static void DeleteRegistration(RegistryKey hive)
    {
        // First, while the record of exactly which defaults are Glide's still exists.
        ReleaseClaimedDefaults(hive);
        hive.DeleteSubKeyTree($@"Software\Classes\Applications\{ApplicationExe}", throwOnMissingSubKey: false);
        hive.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        foreach (var extension in ImageFormatRegistry.Extensions.Where(extension => extension.LastIndexOf('.') == 0))
            hive.DeleteSubKeyTree($@"Software\Classes\{ProgIdFor(extension, hasTypeIcon: true)}", throwOnMissingSubKey: false);
        hive.DeleteSubKeyTree(@"Software\Glide\Capabilities", throwOnMissingSubKey: false);

        using (var registered = hive.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            registered?.DeleteValue(ApplicationName, throwOnMissingValue: false);

        foreach (var extension in ImageFormatRegistry.Extensions)
        {
            using var openWith = hive.OpenSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            if (extension.LastIndexOf('.') == 0) openWith?.DeleteValue(ProgIdFor(extension, hasTypeIcon: true), throwOnMissingValue: false);
        }
    }

    private static bool UnregisterInHive(RegistryKey hive, bool perUser, out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            message = "Windows Open With registration is only available on Windows.";
            return false;
        }

        try
        {
            DeleteRegistration(hive);

            if (perUser)
            {
                // Remember the opt-out so automatic integration does not silently undo the user's
                // explicit removal on the next launch.
                using var state = Registry.CurrentUser.CreateSubKey(StateKeyPath, writable: true);
                state?.SetValue(OptOutValue, 1, RegistryValueKind.DWord);
                state?.DeleteValue(RegisteredVersionValue, throwOnMissingValue: false);
            }

            NotifyAssociationChanged();
            message = "Glide's Open With registration was removed. Windows default-app choices were not changed.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Removing Open With registration failed: {ex.Message}";
            return false;
        }
    }

    // ------------------------------------------------------------------ default viewer -----------

    /// <summary>Everything the claim policy needs to know about one extension, read from the registry.</summary>
    /// <param name="Extension">The extension (".heic").</param>
    /// <param name="UserChoiceProgId">The current user's protected UserChoice ProgID, if any.</param>
    /// <param name="TargetHiveDefault">Software\Classes\.ext default value in the hive being registered.</param>
    /// <param name="OtherHiveDefault">The same value in the other hive (HKLM for per-user, HKCU for machine-wide).</param>
    /// <param name="OtherHiveDefaultResolves">Whether <paramref name="OtherHiveDefault"/> names a registered, openable ProgID.</param>
    /// <param name="OpenWithProgIds">OpenWithProgids entries of both hives.</param>
    internal sealed record DefaultClaimFacts(
        string Extension,
        string? UserChoiceProgId,
        string? TargetHiveDefault,
        string? OtherHiveDefault,
        bool OtherHiveDefaultResolves,
        IReadOnlyCollection<string> OpenWithProgIds);

    /// <summary>
    /// Pure claim policy. Glide becomes an extension's default only when nothing else owns it: no UserChoice
    /// (other than Glide), no other app in OpenWithProgids, no other value in the hive being written (even a
    /// dangling one is never overwritten), and the other hive's default (if any) is Glide's or does not
    /// resolve to a working handler.
    /// </summary>
    internal static bool ShouldClaimDefault(DefaultClaimFacts facts)
    {
        static bool IsNoneOrGlide(string? progId) => string.IsNullOrWhiteSpace(progId) || IsGlideProgId(progId);
        if (!IsNoneOrGlide(facts.UserChoiceProgId)) return false;
        if (facts.OpenWithProgIds.Any(progId => !IsNoneOrGlide(progId))) return false;
        if (!IsNoneOrGlide(facts.TargetHiveDefault)) return false;
        if (!IsNoneOrGlide(facts.OtherHiveDefault) && facts.OtherHiveDefaultResolves) return false;
        return true;
    }

    /// <summary>A recorded claim is released only while the extension's default is still Glide's ProgID.</summary>
    internal static bool ShouldReleaseClaim(string? currentDefault) =>
        IsGlideProgId(currentDefault);

    /// <summary>
    /// Formats Glide may become the default viewer for: those it can actually open on a clean PC (core and
    /// built-in decoders, the in-box WIC formats and the bundled codec library's suffixes). Merely recognised
    /// suffixes (.pdf, .dwg, .cdr, ...) and compound suffixes Windows cannot associate (.nii.gz) are never
    /// claimed: double-clicking them must not land in an error page.
    /// </summary>
    internal static IReadOnlyList<string> DefaultClaimCandidates(IEnumerable<string> codecLibraryExtensions)
    {
        var codec = new HashSet<string>(codecLibraryExtensions, StringComparer.OrdinalIgnoreCase);
        return ImageFormatRegistry.Extensions
            .Where(extension => extension.LastIndexOf('.') == 0)
            .Where(extension =>
            {
                var capability = CodecCapabilityRegistry.Describe(extension);
                return capability.DecodeEnabled || capability.Tier == CodecTier.NativeOs || codec.Contains(extension);
            })
            .ToArray();
    }

    /// <summary>The Settings "Default apps" page for Glide (per-user or machine-wide registration).</summary>
    internal static string DefaultAppsUri(bool perUser) =>
        $"ms-settings:defaultapps?registeredApp{(perUser ? "User" : "Machine")}={Uri.EscapeDataString(ApplicationName)}";

    /// <summary>
    /// Opens Windows' Default apps page on Glide so the user can press "Set default" (the only supported way to
    /// make Glide the default for formats another app already owns). Falls back to the general page.
    /// </summary>
    public static bool OpenDefaultAppsPage(out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            message = "Default apps is only available on Windows.";
            return false;
        }
        var perUser = false;
        try
        {
            using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications");
            perUser = registered?.GetValue(ApplicationName) is string;
        }
        catch { /* fall back to the machine-wide registration name */ }

        foreach (var uri in new[] { DefaultAppsUri(perUser), "ms-settings:defaultapps" })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                message = "Windows Default apps opened: choose \"Set default\" to open every supported format with Glide.";
                return true;
            }
            catch { /* try the general page */ }
        }
        message = "Windows Default apps could not be opened.";
        return false;
    }

    private static int ClaimUnownedDefaults(RegistryKey hive, RegistryKey otherHive, string executable)
    {
        var claimed = 0;
        var codecDirectory = Path.Combine(Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory, BundledCodecLibraries.FolderName);
        using var record = hive.CreateSubKey(ClaimedDefaultsKeyPath, writable: true);
        foreach (var extension in DefaultClaimCandidates(BundledCodecLibraries.AvailableExtensions(codecDirectory)))
        {
            try
            {
                if (!ShouldClaimDefault(ReadClaimFacts(hive, otherHive, extension))) continue;
                using var key = hive.CreateSubKey($@"Software\Classes\{extension}", writable: true);
                if (key is null) continue;
                var extensionProgId = ProgIdFor(extension, executable);
                key.SetValue(string.Empty, extensionProgId, RegistryValueKind.String);
                record?.SetValue(extension, extensionProgId, RegistryValueKind.String);
                claimed++;
            }
            catch { /* one unreadable/locked extension must not stop the rest */ }
        }
        return claimed;
    }

    private static void ReleaseClaimedDefaults(RegistryKey hive)
    {
        try
        {
            using (var record = hive.OpenSubKey(ClaimedDefaultsKeyPath))
            {
                if (record is null) return;
                foreach (var extension in record.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith('.')) continue;
                    try
                    {
                        using var key = hive.OpenSubKey($@"Software\Classes\{extension}", writable: true);
                        if (key is not null && ShouldReleaseClaim(key.GetValue(string.Empty) as string))
                            key.DeleteValue(string.Empty, throwOnMissingValue: false);
                    }
                    catch { /* leave this one; continue with the rest */ }
                }
            }
            hive.DeleteSubKeyTree(ClaimedDefaultsKeyPath, throwOnMissingSubKey: false);
        }
        catch { /* best effort: a leftover default only points at Glide */ }
    }

    private static void ReleaseOrphanedClaims(RegistryKey hive)
    {
        try
        {
            using var record = hive.OpenSubKey(ClaimedDefaultsKeyPath);
            if (record is null) return;
            // Any surviving Glide ProgID (generic or per-extension) means the registration is intact.
            if (new RegistryClassesView(hive).GlideClassNames().Any(name => !name.StartsWith("Applications\\", StringComparison.OrdinalIgnoreCase))) return;
        }
        catch { return; }
        ReleaseClaimedDefaults(hive);
    }

    private static DefaultClaimFacts ReadClaimFacts(RegistryKey hive, RegistryKey otherHive, string extension)
    {
        string? userChoice = null;
        foreach (var name in new[] { "UserChoice", "UserChoiceLatest" })
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{UserChoiceRoot}\{extension}\{name}");
            if (key?.GetValue("ProgId") is string value && !string.IsNullOrWhiteSpace(value)) { userChoice = value; break; }
        }

        static string? DefaultOf(RegistryKey root, string extension)
        {
            using var key = root.OpenSubKey($@"Software\Classes\{extension}");
            return key?.GetValue(string.Empty) as string;
        }

        var openWith = new List<string>();
        foreach (var root in new[] { hive, otherHive })
        {
            using var key = root.OpenSubKey($@"Software\Classes\{extension}\OpenWithProgids");
            if (key is not null) openWith.AddRange(key.GetValueNames().Where(name => !string.IsNullOrWhiteSpace(name)));
        }

        var otherDefault = DefaultOf(otherHive, extension);
        return new DefaultClaimFacts(extension, userChoice, DefaultOf(hive, extension), otherDefault,
            !string.IsNullOrWhiteSpace(otherDefault) && ProgIdResolves(otherDefault), openWith);
    }

    /// <summary>True when a ProgID is registered with something Windows can open it with.</summary>
    private static bool ProgIdResolves(string progId)
    {
        // Packaged (Store) apps register AppX* ProgIDs through their package; treat them as working.
        if (progId.StartsWith("AppX", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey($@"Software\Classes\{progId}\shell");
                if (key is not null) return true;
            }
            catch { /* unreadable: try the other hive */ }
        }
        return false;
    }

    private static bool IsRegisteredInHive(RegistryKey hive, string executable)
    {
        try
        {
            using var key = hive.OpenSubKey($@"Software\Classes\Applications\{ApplicationExe}\shell\open\command");
            var value = key?.GetValue(string.Empty) as string;
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Contains(executable, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string CurrentVersion() =>
        typeof(WindowsFileAssociationRegistration).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    private static void NotifyAssociationChanged()
    {
        try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero); }
        catch { /* Registration itself has already succeeded; shell refresh is best-effort. */ }
    }
}
