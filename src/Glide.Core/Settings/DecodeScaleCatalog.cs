namespace Glide.Core.Settings;

/// <summary>When a viewed image is decoded at full source resolution.</summary>
public enum FullRefinePolicy
{
    /// <summary>Right after the first view (pre-5.1 behaviour; neighbours may also be prepared full).</summary>
    Immediate,
    /// <summary>After navigation has been idle for <see cref="DecodeScaleCatalog.IdleFullRefineDelayMs"/>, current image only.</summary>
    Idle,
    /// <summary>Only when zooming needs more pixels than the shown frame carries.</summary>
    OnZoom
}

/// <summary>Decoder family; each preset can scale them differently.</summary>
public enum DecodeFormatFamily { Jpeg, Wic, Heif, Raw, Other }

/// <summary>
/// One preset x format family row. Scales are relative to the pixels the viewport needs to show the whole
/// image (Fit): 1.0 = viewport-exact, 0.5 = half that, never above the source. <see cref="FirstViewScale"/> sizes
/// the first frame of the image being opened, <see cref="PrefetchScale"/> the frames prepared for neighbours.
/// </summary>
public readonly record struct DecodeScaleProfile(double FirstViewScale, double PrefetchScale, FullRefinePolicy Refine);

/// <summary>
/// Master table: performance preset -> per-format decode scales and full-resolution refine policy. The only place
/// these numbers live; Settings "Preset default" values resolve here and the imaging engine reads the resolved row.
/// 5.1: even Maximum quality shows a viewport-exact first frame and decodes full detail in the background on idle,
/// instead of decoding (and uploading) every 30 MP neighbour at full size.
/// </summary>
public static class DecodeScaleCatalog
{
    public const string MaximumSpeed = "Maximum speed";
    public const string Balanced = "Balanced";
    public const string MaximumQuality = "Maximum quality";

    /// <summary>Idle time before the <see cref="FullRefinePolicy.Idle"/> full-resolution decode.</summary>
    public const int IdleFullRefineDelayMs = 1500;
    /// <summary>Delay before a full decode once zooming needs more pixels (debounces wheel zoom).</summary>
    public const int ZoomFullRefineDelayMs = 120;
    public const double MinScale = 0.05, MaxScale = 1.0;

    private static readonly Dictionary<(string Preset, DecodeFormatFamily Family), DecodeScaleProfile> Rows = new()
    {
        // Maximum quality: viewport-exact first view and neighbours; full detail when idle.
        [(MaximumQuality, DecodeFormatFamily.Jpeg)] = new(1.0, 1.0, FullRefinePolicy.Idle),
        [(MaximumQuality, DecodeFormatFamily.Wic)] = new(1.0, 1.0, FullRefinePolicy.Idle),
        [(MaximumQuality, DecodeFormatFamily.Heif)] = new(1.0, 1.0, FullRefinePolicy.Idle),
        [(MaximumQuality, DecodeFormatFamily.Raw)] = new(1.0, 1.0, FullRefinePolicy.Idle),
        [(MaximumQuality, DecodeFormatFamily.Other)] = new(1.0, 1.0, FullRefinePolicy.Idle),
        // Balanced: slightly soft first view, half-size neighbours, refined to the viewport then full when idle.
        [(Balanced, DecodeFormatFamily.Jpeg)] = new(0.75, 0.5, FullRefinePolicy.Idle),
        [(Balanced, DecodeFormatFamily.Wic)] = new(0.75, 0.5, FullRefinePolicy.Idle),
        [(Balanced, DecodeFormatFamily.Heif)] = new(0.75, 0.5, FullRefinePolicy.Idle),
        [(Balanced, DecodeFormatFamily.Raw)] = new(1.0, 0.5, FullRefinePolicy.Idle),
        [(Balanced, DecodeFormatFamily.Other)] = new(0.75, 0.5, FullRefinePolicy.Idle),
        // Maximum speed: tiny frames while browsing, refined to the viewport; full detail only on zoom.
        [(MaximumSpeed, DecodeFormatFamily.Jpeg)] = new(0.25, 0.125, FullRefinePolicy.OnZoom),
        [(MaximumSpeed, DecodeFormatFamily.Wic)] = new(0.25, 0.125, FullRefinePolicy.OnZoom),
        [(MaximumSpeed, DecodeFormatFamily.Heif)] = new(0.25, 0.125, FullRefinePolicy.OnZoom),
        [(MaximumSpeed, DecodeFormatFamily.Raw)] = new(0.5, 0.25, FullRefinePolicy.OnZoom),
        [(MaximumSpeed, DecodeFormatFamily.Other)] = new(0.25, 0.125, FullRefinePolicy.OnZoom),
    };

    public static IReadOnlyCollection<string> Presets { get; } = [MaximumSpeed, Balanced, MaximumQuality];

    /// <summary>Settings choice labels for the refine override, in combo-box order ("Preset default" first).</summary>
    public static IReadOnlyList<string> RefineChoices { get; } = ["Preset default", "Immediately", "When idle", "On zoom only"];

    public static DecodeScaleProfile Get(string? preset, DecodeFormatFamily family)
    {
        var key = Presets.Contains(preset ?? "") ? preset! : Balanced;
        return Rows[(key, family)];
    }

    /// <summary>Applies the Settings overrides (0 % / "Preset default" keep the preset value).</summary>
    public static DecodeScaleProfile Resolve(string? preset, DecodeFormatFamily family, int firstViewPercent, int prefetchPercent, string? refineChoice)
    {
        var row = Get(preset, family);
        return new DecodeScaleProfile(
            firstViewPercent > 0 ? ClampScale(firstViewPercent / 100.0) : row.FirstViewScale,
            prefetchPercent > 0 ? ClampScale(prefetchPercent / 100.0) : row.PrefetchScale,
            ParseRefine(refineChoice) ?? row.Refine);
    }

    public static FullRefinePolicy? ParseRefine(string? choice) => choice?.Trim() switch
    {
        "Immediately" => FullRefinePolicy.Immediate,
        "When idle" => FullRefinePolicy.Idle,
        "On zoom only" => FullRefinePolicy.OnZoom,
        _ => null
    };

    public static double ClampScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, MinScale, MaxScale) : MaxScale;

    public static DecodeFormatFamily FamilyOf(string? path)
    {
        var ext = string.IsNullOrEmpty(path) ? "" : ImageFormatRegistry.GetLongestExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".jif" or ".jfi" or ".pjpeg" or ".pjpg" or ".mpo" => DecodeFormatFamily.Jpeg,
            ".png" or ".apng" or ".tif" or ".tiff" or ".bmp" or ".dib" or ".gif" or ".webp" or ".ico" or ".jxr" or ".wdp" or ".hdp" => DecodeFormatFamily.Wic,
            ".heic" or ".heif" or ".heics" or ".heifs" or ".hif" or ".avif" or ".avifs" or ".jxl" => DecodeFormatFamily.Heif,
            ".dng" or ".cr2" or ".cr3" or ".crw" or ".nef" or ".nrw" or ".arw" or ".srf" or ".sr2" or ".raf" or ".orf" or ".rw2"
                or ".rwl" or ".pef" or ".3fr" or ".iiq" or ".srw" or ".raw" or ".erf" or ".kdc" or ".dcr" or ".mrw" or ".x3f" => DecodeFormatFamily.Raw,
            _ => DecodeFormatFamily.Other
        };
    }
}
