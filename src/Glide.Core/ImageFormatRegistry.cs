namespace Glide.Core;

/// <summary>
/// Single source of truth for every suffix Glide recognises as an image/document rasterisation
/// candidate. Recognition is deliberately broader than a guaranteed built-in decoder: the runtime
/// may satisfy a suffix through the fast core, Windows WIC, a lazily loaded verified provider, or a
/// non-icon Windows Shell preview. This lets folder navigation and file pickers cover the complete
/// 196-suffix product contract without paying an optional-codec cost during cold launch.
/// </summary>
public static class ImageFormatRegistry
{
    private static readonly string[] DeclaredExtensions = """
.jpg,.jpeg,.jpe,.jfif,.jif,.jfi,.pjpeg,.pjpg,.png,.apng,.mng,.jng,.bmp,.dib,.rle,.wbmp,.tif,.tiff,.btf,.gif,.ico,.cur,.ani,.icns,.webp,.heic,.heif,.heics,.heifs,.hif,.avif,.avifs,.svg,.svgz,.jxr,.wdp,.hdp,.jp2,.j2k,.j2c,.jpc,.jpx,.jpf,.jpm,.mj2,.jxl,.psd,.psb,.pdd,.xcf,.ora,.kra,.afphoto,.afdesign,.afpub,.clip,.csp,.pdn,.psp,.pspimage,.pxr,.tga,.targa,.icb,.vda,.vst,.dpx,.cin,.sgi,.rgb,.rgba,.bw,.ras,.sun,.iff,.lbm,.ilbm,.img,.pic,.pict,.pct,.pict2,.eps,.epsf,.ai,.pdf,.pnm,.ppm,.pgm,.pbm,.pam,.pfm,.pcx,.qoi,.hdr,.rgbe,.xyze,.exr,.dds,.ff,.fits,.fit,.fts,.fts.gz,.hdr.gz,.xbm,.xpm,.xwd,.cut,.mac,.mpo,.jps,.pns,.dng,.cr2,.cr3,.crw,.nef,.nrw,.arw,.srf,.sr2,.raf,.orf,.ori,.rw2,.rwl,.pef,.ptx,.3fr,.fff,.iiq,.cap,.eip,.mef,.mos,.mrw,.x3f,.erf,.kdc,.dcr,.k25,.bay,.srw,.rwz,.gpr,.mdc,.raw,.r3d,.ari,.cinema,.dcs,.drf,.dsc,.r2d,.rw1,.dcm,.dicom,.ima,.nii,.nii.gz,.mha,.mhd,.nrrd,.ndpi,.svs,.vms,.vmu,.scn,.mrxs,.bif,.czi,.lif,.lsm,.ome.tif,.ome.tiff,.vsi,.ktx,.ktx2,.pvr,.astc,.basis,.tex,.vtf,.wal,.spr,.cdr,.cmx,.cpt,.emf,.wmf,.emz,.wmz,.dwg,.dxf,.skp
""".Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
       .Select(x => x.ToLowerInvariant())
       .Distinct(StringComparer.OrdinalIgnoreCase)
       .ToArray();

    /// <summary>
    /// Proven, dependency-free normal path inherited from Glide 2.1. Performance work must not
    /// force provider discovery/loading for these suffixes. The decoder itself is signature-based,
    /// so aliases can still succeed through WIC/Avalonia without being promoted into this set.
    /// </summary>
    public static IReadOnlyList<string> CoreFastPathExtensions { get; } = new[]
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico"
    };

    public static IReadOnlyList<string> Extensions { get; } = DeclaredExtensions;

    private static readonly HashSet<string> ExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CoreSet = new(CoreFastPathExtensions, StringComparer.OrdinalIgnoreCase);

    // Only five declared suffixes are compound. Keep these in longest-first order so matching a
    // folder containing tens of thousands of files remains O(1)-ish instead of 196 EndsWith calls
    // per file. This matters because the 196-format contract must not make navigation slower.
    private static readonly string[] CompoundExtensions =
    {
        ".ome.tiff", ".ome.tif", ".nii.gz", ".fts.gz", ".hdr.gz"
    };

    /// <summary>Matches the longest declared suffix, preserving compound formats such as .nii.gz and .ome.tiff.</summary>
    public static string? Match(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        // Callers frequently pass an already-normalised extension. Avoid Path parsing in that hot path.
        if (path[0] == '.' && path.IndexOfAny(new[] { '/', '\\' }) < 0)
        {
            var direct = path.ToLowerInvariant();
            return ExtensionSet.Contains(direct) ? direct : null;
        }

        foreach (var compound in CompoundExtensions)
            if (path.EndsWith(compound, StringComparison.OrdinalIgnoreCase)) return compound;

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return null;
        extension = extension.ToLowerInvariant();
        return ExtensionSet.Contains(extension) ? extension : null;
    }

    public static string GetLongestExtension(string path)
    {
        var match = Match(path);
        if (match is not null) return match;
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (path[0] == '.' && path.IndexOfAny(new[] { '/', '\\' }) < 0) return path.ToLowerInvariant();
        return Path.GetExtension(path).ToLowerInvariant();
    }

    /// <summary>
    /// Product-level support means Glide recognises and routes the suffix. The eventual decoder can
    /// be core, platform, or lazily supplied. Failure to find a decoder is reported at open time and
    /// must never crash navigation or make optional-codec breadth part of application startup.
    /// </summary>
    public static bool IsSupported(string path) => ExtensionSet.Contains(GetLongestExtension(path));

    public static bool IsCoreFastPath(string extensionOrPath) => CoreSet.Contains(GetLongestExtension(extensionOrPath));
}
