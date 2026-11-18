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
        // WebP is decoded by the vendored libwebp decoder inside Glide.ShellThumbnail.dll. Windows
        // registers its in-box "Photo Thumbnail Provider" for .webp, but that handler needs the
        // optional "Webp Image Extensions" WIC codec and fails without it — so a weakly-handled
        // .webp must still be claimed by Glide or Explorer is left with a generic icon.
        ".webp",
        ".svg", ".svgz", ".tga", ".targa", ".icb", ".vda", ".vst", ".pcx", ".pnm", ".ppm", ".pgm", ".pbm",
        ".pam", ".qoi", ".hdr", ".rgbe", ".wbmp", ".xbm", ".xpm", ".sgi", ".rgb", ".rgba", ".bw",
        ".psd", ".psb", ".pdd"
    };

    /// <summary>
    /// Windows' in-box "Photo Thumbnail Provider". It renders through whichever WIC codec is installed, so for
    /// HEIC/HEIF/AVIF/JPEG XL/camera RAW it only works once the optional Store codec is present; on a clean PC
    /// (and on LTSC/Server, which have no Store) it fails and Explorer shows a blank page icon. Glide's provider
    /// decodes those formats itself (codecs\glidecodecs.dll / codecs\glidemanaged.dll), so in Recommended mode it
    /// takes over from this handler - but never from a third-party handler.
    /// </summary>
    public const string WindowsPhotoThumbnailProvider = "{C7657C4A-9F68-40fa-A4DF-96BC08EB3551}";

    public static string ProviderDllPath => Path.Combine(AppContext.BaseDirectory, ProviderDllName);
    public static bool ProviderDllPresent => File.Exists(ProviderDllPath);

    /// <summary>Resolves the extension set for a mode from the authoritative Glide format registry.</summary>
    public static IReadOnlyList<string> ExtensionsFor(ThumbnailProviderMode mode, IEnumerable<string>? custom = null)
        => ResolveExtensions(mode, ImageFormatRegistry.Extensions, ReadRegisteredHandler,
            BundledCodecLibraries.AvailableExtensions(Path.Combine(Path.GetDirectoryName(ProviderDllPath) ?? AppContext.BaseDirectory,
                BundledCodecLibraries.FolderName)),
            custom);

    /// <summary>
    /// Pure policy behind <see cref="ExtensionsFor"/> (unit-tested over a fake handler table; never touches the
    /// registry). <paramref name="existingHandler"/> returns the CLSID currently registered as an extension's
    /// thumbnail handler (or null); <paramref name="codecLibraryFormats"/> are the formats the provider decodes
    /// through a codec library that is actually installed. Recommended claims an extension when it has no
    /// handler, when Glide is already its handler, when Glide decodes it natively, or when it is a codec-library
    /// format whose only handler is Windows' codec-dependent Photo Thumbnail Provider.
    /// </summary>
    internal static IReadOnlyList<string> ResolveExtensions(ThumbnailProviderMode mode, IEnumerable<string> all,
        Func<string, string?> existingHandler, IEnumerable<string> codecLibraryFormats, IEnumerable<string>? custom = null)
    {
        var codecFormats = new HashSet<string>(codecLibraryFormats, StringComparer.OrdinalIgnoreCase);
        bool Foreign(string extension)
        {
            var handler = existingHandler(extension);
            return handler is not null && !handler.Equals(ProviderClsid, StringComparison.OrdinalIgnoreCase);
        }
        bool CodecDependentPlatformHandler(string extension)
            => codecFormats.Contains(extension) &&
               string.Equals(existingHandler(extension), WindowsPhotoThumbnailProvider, StringComparison.OrdinalIgnoreCase);

        return mode switch
        {
            ThumbnailProviderMode.All => all.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ThumbnailProviderMode.Custom => (custom ?? Array.Empty<string>())
                .Where(ImageFormatRegistry.IsSupported)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ThumbnailProviderMode.UnsupportedOnly => all.Where(extension => !Foreign(extension)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            _ => all.Where(extension => !Foreign(extension) || GlideNativeFormats.Contains(extension) || CodecDependentPlatformHandler(extension))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    /// <summary>Formats Glide's provider decodes natively (compact decoders, libwebp, Direct2D SVG).</summary>
    internal static IReadOnlyCollection<string> NativeProviderFormats => GlideNativeFormats;


    public static bool IsRegistered() => !string.IsNullOrWhiteSpace(RegisteredDllPath(perUser: true));

    /// <summary>The DLL path Explorer would load for this hive's CLSID registration (read-only), or null.</summary>
    public static string? RegisteredDllPath(bool perUser)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var hive = perUser ? Registry.CurrentUser : Registry.LocalMachine;
            using var key = hive.OpenSubKey($@"Software\Classes\CLSID\{ProviderClsid}\InprocServer32");
            return key?.GetValue(string.Empty) is string value && !string.IsNullOrWhiteSpace(value) ? value : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Extensions whose effective thumbnail handler is Glide (per-user entry first, then machine-wide).
    /// Read-only; used for the status line and to seed the Custom mode selection.
    /// </summary>
    public static IReadOnlyList<string> ExtensionsRoutedToGlide(bool perUserOnly = false)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var routed = new List<string>();
        foreach (var extension in ImageFormatRegistry.Extensions)
        {
            var handler = perUserOnly ? ReadHandler(Registry.CurrentUser, extension) : ReadRegisteredHandler(extension);
            if (handler is not null && handler.Equals(ProviderClsid, StringComparison.OrdinalIgnoreCase)) routed.Add(extension);
        }
        return routed;
    }

    /// <summary>Gathers the read-only facts behind the Settings status line.</summary>
    public static ThumbnailProviderFacts ProbeFacts()
    {
        var userPath = RegisteredDllPath(perUser: true);
        var machinePath = RegisteredDllPath(perUser: false);
        var effective = !string.IsNullOrWhiteSpace(userPath) ? userPath : machinePath;
        var glideVersion = typeof(WindowsThumbnailRegistration).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "unknown";
        return new ThumbnailProviderFacts(
            LocalDllPath: ProviderDllPath,
            LocalDllExists: ProviderDllPresent,
            LocalDllVersion: ThumbnailProviderStatus.ReadDllVersion(ProviderDllPath),
            UserRegisteredPath: userPath,
            MachineRegisteredPath: machinePath,
            EffectiveDllExists: ThumbnailProviderStatus.RegisteredFileExists(effective),
            EffectiveDllVersion: ThumbnailProviderStatus.ReadDllVersion(effective),
            RoutedFormats: ExtensionsRoutedToGlide().Count,
            GlideVersion: glideVersion);
    }

    public static bool Register(bool perUser, IEnumerable<string> extensions, out string message)
        => Apply(perUser, extensions, register: true, out message);
    // NOTE: perUser=false targets HKLM and requires elevation; the installer calls this via
    // --register-thumbnails from an elevated process. No command-line quoting applies here
    // (InprocServer32 stores a bare DLL path, not a command); verified no quoting bug.

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
                    if (key is null) continue;
                    // Taking over a handler in the same hive (machine-wide: Windows' Photo Thumbnail Provider in
                    // HKLM) remembers it, so unregistering/uninstalling puts it back instead of leaving none.
                    if (PreviousHandlerToRemember(key.GetValue(string.Empty) as string) is { } previous)
                        key.SetValue(PreviousHandlerValue, previous, RegistryValueKind.String);
                    key.SetValue(string.Empty, ProviderClsid, RegistryValueKind.String);
                }
            }
            else
            {
                hive.DeleteSubKeyTree(clsidPath, throwOnMissingSubKey: false);
                foreach (var extension in list)
                {
                    using var key = hive.OpenSubKey($@"Software\Classes\{extension}\ShellEx\{ThumbnailHandlerGuid}", writable: true);
                    if (key?.GetValue(string.Empty) is string value && value.Equals(ProviderClsid, StringComparison.OrdinalIgnoreCase))
                    {
                        if (key.GetValue(PreviousHandlerValue) is string previous && IsClsid(previous))
                            key.SetValue(string.Empty, previous, RegistryValueKind.String);
                        else
                            key.DeleteValue(string.Empty, throwOnMissingValue: false);
                    }
                    key?.DeleteValue(PreviousHandlerValue, throwOnMissingValue: false);
                }
            }

            try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero); } catch (Exception loggedEx) { Glide.Diagnostics.Runtime.LiveDiagnosticTrace.WriteException("thumbnails", "assoc_notify_failed", loggedEx); }
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

    /// <summary>Named value next to the handler default that remembers the handler Glide replaced in that hive.</summary>
    private const string PreviousHandlerValue = "GlidePreviousHandler";

    /// <summary>
    /// Pure: which handler to remember when Glide takes over <paramref name="current"/>: another handler is
    /// remembered; when Glide is already the handler, nothing changes, so re-registering never forgets (or
    /// overwrites) the original it remembered the first time.
    /// </summary>
    internal static string? PreviousHandlerToRemember(string? current)
    {
        if (current is null || !IsClsid(current) || current.Equals(ProviderClsid, StringComparison.OrdinalIgnoreCase)) return null;
        return current;
    }

    private static bool IsClsid(string value) => Guid.TryParse(value, out _) && value.Trim().StartsWith('{');

    private static string? ReadRegisteredHandler(string extension)
        => ReadHandler(Registry.CurrentUser, extension) ?? ReadHandler(Registry.LocalMachine, extension);

    private static string? ReadHandler(RegistryKey hive, string extension)
    {
        try
        {
            using var key = hive.OpenSubKey($@"Software\Classes\{extension}\ShellEx\{ThumbnailHandlerGuid}");
            if (key?.GetValue(string.Empty) is string value && !string.IsNullOrWhiteSpace(value)) return value;
        }
        catch { /* key missing or unreadable means no existing handler to preserve */ }
        return null;
    }
}
