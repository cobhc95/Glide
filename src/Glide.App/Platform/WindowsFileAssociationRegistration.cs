using System.Runtime.InteropServices;
using Microsoft.Win32;
using Glide.Core;

namespace Glide.App.Platform;

/// <summary>
/// Per-user Windows Open-With/Capabilities registration. This deliberately never writes a UserChoice
/// default association: Windows remains the authority for the user's default-app selection.
/// </summary>
internal static class WindowsFileAssociationRegistration
{
    private const string ApplicationName = "Glide";
    private const string ApplicationExe = "Glide.exe";
    private const string ProgId = "Glide.Image";

    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    public static bool Register(out string message)
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

            using (var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{ApplicationExe}", writable: true))
            {
                app?.SetValue("FriendlyAppName", ApplicationName, RegistryValueKind.String);
                using var supported = app?.CreateSubKey("SupportedTypes", writable: true);
                if (supported is not null)
                    foreach (var extension in ImageFormatRegistry.Extensions)
                        supported.SetValue(extension, string.Empty, RegistryValueKind.String);
                using var open = app?.CreateSubKey(@"shell\open\command", writable: true);
                open?.SetValue(string.Empty, command, RegistryValueKind.String);
            }

            using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}", writable: true))
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
                using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
                openWith?.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            using (var capabilities = Registry.CurrentUser.CreateSubKey(@"Software\Glide\Capabilities", writable: true))
            {
                capabilities?.SetValue("ApplicationName", ApplicationName, RegistryValueKind.String);
                capabilities?.SetValue("ApplicationDescription", "Fast lightweight image viewer", RegistryValueKind.String);
                using var associations = capabilities?.CreateSubKey("FileAssociations", writable: true);
                if (associations is not null)
                    foreach (var extension in ImageFormatRegistry.Extensions)
                        associations.SetValue(extension, ProgId, RegistryValueKind.String);
            }

            using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications", writable: true))
                registered?.SetValue(ApplicationName, @"Software\Glide\Capabilities", RegistryValueKind.String);

            NotifyAssociationChanged();
            message = $"Glide was added to Windows Open With for {ImageFormatRegistry.Extensions.Count} recognised formats.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Open With registration failed: {ex.Message}";
            return false;
        }
    }

    public static bool Unregister(out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            message = "Windows Open With registration is only available on Windows.";
            return false;
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\{ApplicationExe}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Glide\Capabilities", throwOnMissingSubKey: false);

            using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
                registered?.DeleteValue(ApplicationName, throwOnMissingValue: false);

            foreach (var extension in ImageFormatRegistry.Extensions)
            {
                using var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }

            NotifyAssociationChanged();
            message = "Glide's per-user Open With registration was removed. Windows default-app choices were not changed.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Removing Open With registration failed: {ex.Message}";
            return false;
        }
    }

    private static void NotifyAssociationChanged()
    {
        try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero); }
        catch { /* Registration itself has already succeeded; shell refresh is best-effort. */ }
    }
}
