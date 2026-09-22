using System.Runtime.InteropServices;
using Microsoft.Win32;
using Glide.Core;

namespace Glide.App.Platform;

/// <summary>How much of Glide's supported-format registry should route thumbnails to Glide.</summary>
internal enum ThumbnailProviderMode
{
    /// <summary>Safe default: fill gaps and own the formats Glide decodes natively.</summary>
    Recommended,
    /// <summary>Only extensions that have no other thumbnail handler at all.</summary>
    UnsupportedOnly,
    /// <summary>Every supported extension, overriding any existing handler.</summary>
    All,
    /// <summary>An explicit per-extension selection.</summary>
    Custom,
}

/// <summary>
/// Registers Glide's native Explorer thumbnail provider. The extension set is derived from
/// <see cref="ImageFormatRegistry"/> so a new Glide format automatically becomes thumbnail-capable.
/// Thumbnail registration is intentionally separate from Open With/file associations.
/// </summary>
internal static class WindowsThumbnailRegistration
{
    /// <summary>Must match CLSID_GlideThumbnailProvider in native/Glide.ShellThumbnail.</summary>
    public const string ProviderClsid = "{6E3C1B2A-9F41-4E7C-9B1E-2C7A5D8F0A31}";
    private const string ThumbnailHandlerGuid = "{E357FCCD-A995-4576-B01F-234630154E96}";
    private const string ProviderDllName = "Glide.ShellThumbnail.dll";
    private const string ProviderFriendlyName = "Glide Explorer Thumbnail Provider";

    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>Formats Glide decodes natively; Glide owns these even when a weak handler exists.</summary>
    private static readonly HashSet<string> GlideNativeFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".svgz", ".tga", ".targa", ".icb", ".vda", ".vst", ".pcx", ".pnm", ".ppm", ".pgm", ".pbm",
        ".pam", ".qoi", ".hdr", ".rgbe", ".wbmp", ".xbm", ".xpm", ".sgi", ".rgb", ".rgba", ".bw"
    };

    public static string ProviderDllPath => Path.Combine(AppContext.BaseDirectory, ProviderDllName);
    public static bool ProviderDllPresent => File.Exists(ProviderDllPath);

    /// <summary>Resolves the extension set for a mode from the authoritative Glide format registry.</summary>
    public static IReadOnlyList<string> ExtensionsFor(ThumbnailProviderMode mode, IEnumerable<string>? custom = null)
    {
        var all = ImageFormatRegistry.Extensions;
        return mode switch
        {
            ThumbnailProviderMode.All => all.ToArray(),
            ThumbnailProviderMode.Custom => (custom ?? Array.Empty<string>())
                .Where(ImageFormatRegistry.IsSupported)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ThumbnailProviderMode.UnsupportedOnly => all.Where(extension => !HasForeignHandler(extension)).ToArray(),
            _ => all.Where(extension => !HasForeignHandler(extension) || GlideNativeFormats.Contains(extension)).ToArray(),
        };
    }

    /// <summary>True when another application already registers a thumbnail handler for the extension.</summary>
    public static bool HasForeignHandler(string extension)
    {
        var existing = ReadRegisteredHandler(extension);
        return existing is not null && !existing.Equals(ProviderClsid, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRegistered()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{ProviderClsid}\InprocServer32");
            return key?.GetValue(string.Empty) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch { return false; }
    }

    public static bool Register(bool perUser, IEnumerable<string> extensions, out string message)
        => Apply(perUser, extensions, register: true, out message);

    public static bool Unregister(bool perUser, IEnumerable<string> extensions, out string message)
        => Apply(perUser, extensions, register: false, out message);

    private static bool Apply(bool perUser, IEnumerable<string> extensions, bool register, out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            message = "Explorer thumbnails are only available on Windows.";
            return false;
        }
        if (register && !ProviderDllPresent)
        {
            message = $"Thumbnail provider not found at {ProviderDllPath}.";
            return false;
        }

        var list = extensions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            var hive = perUser ? Registry.CurrentUser : Registry.LocalMachine;
            var clsidPath = $@"Software\Classes\CLSID\{ProviderClsid}";
            if (register)
            {
                using (var key = hive.CreateSubKey(clsidPath, writable: true))
                    key?.SetValue(string.Empty, ProviderFriendlyName, RegistryValueKind.String);
                using (var key = hive.CreateSubKey($@"{clsidPath}\InprocServer32", writable: true))
                {
                    key?.SetValue(string.Empty, ProviderDllPath, RegistryValueKind.String);
                    key?.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
                }
                foreach (var extension in list)
                {
                    using var key = hive.CreateSubKey($@"Software\Classes\{extension}\ShellEx\{ThumbnailHandlerGuid}", writable: true);
                    key?.SetValue(string.Empty, ProviderClsid, RegistryValueKind.String);
                }
            }
            else
            {
                hive.DeleteSubKeyTree(clsidPath, throwOnMissingSubKey: false);
                foreach (var extension in list)
                {
                    using var key = hive.OpenSubKey($@"Software\Classes\{extension}\ShellEx\{ThumbnailHandlerGuid}", writable: true);
                    if (key?.GetValue(string.Empty) is string value && value.Equals(ProviderClsid, StringComparison.OrdinalIgnoreCase))
                        key.DeleteValue(string.Empty, throwOnMissingValue: false);
                }
            }

            try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero); } catch { }
            message = register
                ? $"Glide Explorer thumbnails enabled for {list.Length} formats."
                : $"Glide Explorer thumbnails disabled for {list.Length} formats.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Explorer thumbnail registration failed: {ex.Message}";
            return false;
        }
    }

    private static string? ReadRegisteredHandler(string extension)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey($@"Software\Classes\{extension}\ShellEx\{ThumbnailHandlerGuid}");
                if (key?.GetValue(string.Empty) is string value && !string.IsNullOrWhiteSpace(value)) return value;
            }
            catch { }
        }
        return null;
    }
}
