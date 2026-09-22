using System.Runtime.InteropServices;
using Microsoft.Win32;
using Glide.Core;

namespace Glide.App.Platform;

/// <summary>
/// Windows Open-With/Capabilities registration for every extension Glide recognises. Glide adds
/// itself automatically — per-user on first launch and machine-wide from the installer — so the user
/// never has to configure it from Options. This deliberately never writes a UserChoice default
/// association: Windows remains the authority for the user's default-app selection.
/// </summary>
internal static class WindowsFileAssociationRegistration
{
    private const string ApplicationName = "Glide";
    private const string ApplicationExe = "Glide.exe";
    private const string ProgId = "Glide.Image";

    private const string StateKeyPath = @"Software\Glide";
    private const string RegisteredVersionValue = "OpenWithRegistrationVersion";
    private const string OptOutValue = "OpenWithOptOut";

    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>
    /// Automatic per-user integration, intended to run once per version during startup, off the UI
    /// thread. Idempotent and silent: it skips when a machine-wide (installer) registration already
    /// covers this executable or when the user has explicitly removed the registration.
    /// </summary>
    public static void EnsureRegisteredQuietly()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;

            // A machine-wide install already puts Glide in Open With for every user of this PC.
            var machineWideRegistered = IsRegisteredInHive(Registry.LocalMachine, executable);

            using var state = Registry.CurrentUser.CreateSubKey(StateKeyPath, writable: true);
            if (state is null) return;
            var optedOut = state.GetValue(OptOutValue) is int optOut && optOut != 0;
            var registeredVersion = state.GetValue(RegisteredVersionValue) as string;

            if (!ShouldAutoRegister(machineWideRegistered, optedOut, registeredVersion, CurrentVersion())) return;

            Register(out _);
        }
        catch { /* Best-effort: association setup must never disturb startup. */ }
    }

    /// <summary>
    /// Pure decision for whether startup should add the per-user registration. Kept separate from the
    /// registry I/O so the policy is unit-testable.
    /// </summary>
    internal static bool ShouldAutoRegister(bool machineWideRegistered, bool optedOut, string? registeredVersion, string currentVersion) =>
        !machineWideRegistered &&
        !optedOut &&
        !string.Equals(registeredVersion, currentVersion, StringComparison.Ordinal);

    /// <summary>Adds Glide to Windows Open With for every recognised extension (current user).</summary>
    public static bool Register(out string message) => RegisterInHive(Registry.CurrentUser, perUser: true, out message);

    /// <summary>Adds Glide to Windows Open With for every recognised extension (all users; needs elevation).</summary>
    public static bool RegisterMachineWide(out string message) => RegisterInHive(Registry.LocalMachine, perUser: false, out message);

    /// <summary>Removes the current user's registration and opts out of automatic re-registration.</summary>
    public static bool Unregister(out string message) => UnregisterInHive(Registry.CurrentUser, perUser: true, out message);

    /// <summary>Removes the machine-wide (installer) registration.</summary>
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
            var command = $"\"{executable}\" \"%1\"";

            using (var app = hive.CreateSubKey($@"Software\Classes\Applications\{ApplicationExe}", writable: true))
            {
                app?.SetValue("FriendlyAppName", ApplicationName, RegistryValueKind.String);
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

            // OpenWithProgids makes Glide discoverable from a file's native Open With menu without
            // attempting to change Windows' protected default-app UserChoice value.
            foreach (var extension in ImageFormatRegistry.Extensions)
            {
                using var openWith = hive.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
                openWith?.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            using (var capabilities = hive.CreateSubKey(@"Software\Glide\Capabilities", writable: true))
            {
                capabilities?.SetValue("ApplicationName", ApplicationName, RegistryValueKind.String);
                capabilities?.SetValue("ApplicationDescription", "Fast lightweight image viewer", RegistryValueKind.String);
                using var associations = capabilities?.CreateSubKey("FileAssociations", writable: true);
                if (associations is not null)
                    foreach (var extension in ImageFormatRegistry.Extensions)
                        associations.SetValue(extension, ProgId, RegistryValueKind.String);
            }

            using (var registered = hive.CreateSubKey(@"Software\RegisteredApplications", writable: true))
                registered?.SetValue(ApplicationName, @"Software\Glide\Capabilities", RegistryValueKind.String);

            if (perUser)
            {
                // Record the version so startup does not rewrite ~600 values every launch, and clear
                // any earlier opt-out because the user has now explicitly (re-)enabled integration.
                using var state = Registry.CurrentUser.CreateSubKey(StateKeyPath, writable: true);
                state?.SetValue(RegisteredVersionValue, CurrentVersion(), RegistryValueKind.String);
                state?.DeleteValue(OptOutValue, throwOnMissingValue: false);
            }

            NotifyAssociationChanged();
            message = $"Glide is available in Windows Open With for {ImageFormatRegistry.Extensions.Count} recognised formats.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Open With registration failed: {ex.Message}";
            return false;
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
            hive.DeleteSubKeyTree($@"Software\Classes\Applications\{ApplicationExe}", throwOnMissingSubKey: false);
            hive.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            hive.DeleteSubKeyTree(@"Software\Glide\Capabilities", throwOnMissingSubKey: false);

            using (var registered = hive.OpenSubKey(@"Software\RegisteredApplications", writable: true))
                registered?.DeleteValue(ApplicationName, throwOnMissingValue: false);

            foreach (var extension in ImageFormatRegistry.Extensions)
            {
                using var openWith = hive.OpenSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }

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
