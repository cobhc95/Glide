using System.Diagnostics;
using System.Runtime.InteropServices;
using Glide.App.Settings;
using Glide.Core;
using Glide.Core.Reset;
using Microsoft.Win32;

namespace Glide.App.Platform;

/// <summary>
/// HKEY_CURRENT_USER for <see cref="IResetRegistry"/>. <paramref name="basePath"/> re-roots every path below a scratch key
/// (the tests' stand-in for the whole hive); production uses an empty base, i.e. the real HKCU.
/// </summary>
internal sealed class WindowsResetRegistry(RegistryKey root, string basePath) : IResetRegistry
{
    public WindowsResetRegistry() : this(Registry.CurrentUser, string.Empty) { }

    private string Full(string subKey) => basePath.Length == 0 ? subKey : basePath + "\\" + subKey;

    public bool KeyExists(string subKey)
    {
        using var key = root.OpenSubKey(Full(subKey));
        return key is not null;
    }

    public bool DeleteKeyTree(string subKey)
    {
        if (!KeyExists(subKey)) return false;
        root.DeleteSubKeyTree(Full(subKey), throwOnMissingSubKey: false);
        return true;
    }

    public string? GetStringValue(string subKey, string valueName)
    {
        using var key = root.OpenSubKey(Full(subKey));
        return key?.GetValue(valueName) as string;
    }

    public bool ValueExists(string subKey, string valueName)
    {
        using var key = root.OpenSubKey(Full(subKey));
        return key is not null && key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);
    }

    public bool DeleteValue(string subKey, string valueName)
    {
        if (!ValueExists(subKey, valueName)) return false;
        using var key = root.OpenSubKey(Full(subKey), writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
        return true;
    }

    public void SetDword(string subKey, string valueName, int value)
    {
        using var key = root.CreateSubKey(Full(subKey), writable: true);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }
}

internal sealed class WindowsResetProcessHost : IResetProcessHost
{
    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException) { return true; } // no such process: it has already exited
        catch (InvalidOperationException) { return true; }
    }

    public bool Start(string executable, IReadOnlyList<string> arguments, out string? error)
    {
        error = null;
        try
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) { error = "the process did not start"; return false; }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

/// <summary>The helper process has no window: its one message goes through a plain Windows message box.</summary>
internal sealed partial class WindowsResetUserMessage : IResetUserMessage
{
    private const uint MbOk = 0x0, MbIconWarning = 0x30, MbSetForeground = 0x10000, MbTopMost = 0x40000;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public void Show(string title, string message)
    {
        try { _ = MessageBox(IntPtr.Zero, message, title, MbOk | MbIconWarning | MbSetForeground | MbTopMost); }
        catch { /* nothing else to try: the helper has no UI */ }
    }
}

/// <summary>Builds the reset environment and services for the running copy (installed or portable).</summary>
internal static class ResetServicesFactory
{
    public static ResetEnvironment CreateEnvironment(string settingsDirectory) => new(
        SettingsDirectory: settingsDirectory,
        LocalGlideDirectory: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide"),
        TempDirectory: Path.GetTempPath(),
        InstallDirectory: AppContext.BaseDirectory,
        ImageExtensions: ImageFormatRegistry.Extensions,
        SettingsSchemaVersion: SettingsStore.CurrentSettingsSchema,
        // "All settings" writes these defaults for every option (the same reset as Settings' Restore defaults button).
        DefaultSettingsJson: SettingsDefaults.DefaultSettingsJson());

    public static ResetServices Create(string settingsDirectory) => new()
    {
        Environment = CreateEnvironment(settingsDirectory),
        FileSystem = PhysicalResetFileSystem.Instance,
        Registry = new WindowsResetRegistry(),
        Processes = new WindowsResetProcessHost(),
        BackupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Glide backups"),
        ExecutablePath = Environment.ProcessPath,
        ProcessId = Environment.ProcessId,
        AppVersion = AppVersion.Informational,
    };

    /// <summary>The services Settings uses for the live app.</summary>
    public static ResetServices CreateDefault() => Create(SettingsStore.GetSettingsDirectory());

    /// <summary>Runs the <c>--reset-glide</c> helper: wipe after the app has closed, then start it again.</summary>
    public static int RunHelper(ResetHelperArguments arguments) =>
        ResetHelper.Run(arguments, Create(arguments.SettingsDirectory), new WindowsResetUserMessage());
}
