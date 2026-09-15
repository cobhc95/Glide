using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Glide.App.Platform;

/// <summary>
/// Manages per-user Windows Run registry entry to allow Glide to start minimized/in background
/// on user login for instant sub-30ms first-image presentation.
/// </summary>
internal static class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Glide";

    public static bool SetRunAtStartup(bool enable, out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            message = "Windows startup registration is only available on Windows.";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                message = "Failed to open Windows Run registry key.";
                return false;
            }

            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                {
                    message = "Glide could not resolve its executable path.";
                    return false;
                }

                key.SetValue(ValueName, $"\"{exe}\" --background", RegistryValueKind.String);
                message = "Glide configured to start in background with Windows.";
                return true;
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                message = "Glide removed from Windows startup.";
                return true;
            }
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public static bool IsRunAtStartupConfigured()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }
}
