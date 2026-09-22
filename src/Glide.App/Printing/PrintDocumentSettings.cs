using System.Text.Json;

namespace Glide.App.Printing;

/// <summary>
/// Persisted print-layout preferences. Deliberately printer-agnostic: scaling, margins,
/// rotation, alignment, orientation and copies survive across sessions and printers, while
/// printer-specific capabilities (paper size/source names, resolution, colour/duplex driver
/// state) are re-resolved from the live driver on every dialog open and never persisted.
/// Stored in print.settings.json next to glide.settings.json; loaded lazily on first Print.
/// </summary>
public sealed record PrintDocumentSettings
{
    public string ScalingMode { get; set; } = "Best fit";
    public bool KeepAspectRatio { get; set; } = true;
    public bool AutoRotate { get; set; } = true;
    public string HAlign { get; set; } = "Centre";
    public string VAlign { get; set; } = "Centre";
    public string Orientation { get; set; } = "Portrait";
    public string ColorMode { get; set; } = "Colour";
    // Hundredths of an inch (Win32 print unit). Default 0.5 in / 12.7 mm all round.
    public double MarginLeft { get; set; } = 50;
    public double MarginTop { get; set; } = 50;
    public double MarginRight { get; set; } = 50;
    public double MarginBottom { get; set; } = 50;
    public double CustomScalePercent { get; set; } = 100;
    public int Copies { get; set; } = 1;

    public PrintScalingMode ScalingModeEnum => ScalingMode switch
    {
        "Fill page" => PrintScalingMode.FillPage,
        "Actual size" => PrintScalingMode.ActualSize,
        "Custom scale" => PrintScalingMode.CustomScale,
        "Stretch" => PrintScalingMode.Stretch,
        _ => PrintScalingMode.BestFit
    };

    public PrintHAlign HAlignEnum => HAlign switch
    {
        "Left" => PrintHAlign.Left,
        "Right" => PrintHAlign.Right,
        _ => PrintHAlign.Centre
    };

    public PrintVAlign VAlignEnum => VAlign switch
    {
        "Top" => PrintVAlign.Top,
        "Bottom" => PrintVAlign.Bottom,
        _ => PrintVAlign.Centre
    };

    public bool IsLandscape => string.Equals(Orientation, "Landscape", StringComparison.OrdinalIgnoreCase);

    public PrintDocumentSettings Normalized()
    {
        var copy = this with { };
        if (copy.ScalingMode is not ("Best fit" or "Fill page" or "Actual size" or "Custom scale" or "Stretch"))
            copy.ScalingMode = "Best fit";
        if (copy.HAlign is not ("Left" or "Centre" or "Right")) copy.HAlign = "Centre";
        if (copy.VAlign is not ("Top" or "Centre" or "Bottom")) copy.VAlign = "Centre";
        if (copy.Orientation is not ("Portrait" or "Landscape")) copy.Orientation = "Portrait";
        if (copy.ColorMode is not ("Colour" or "Grayscale")) copy.ColorMode = "Colour";
        copy.MarginLeft = Math.Clamp(copy.MarginLeft, 0, 300);
        copy.MarginTop = Math.Clamp(copy.MarginTop, 0, 300);
        copy.MarginRight = Math.Clamp(copy.MarginRight, 0, 300);
        copy.MarginBottom = Math.Clamp(copy.MarginBottom, 0, 300);
        copy.CustomScalePercent = Math.Clamp(copy.CustomScalePercent, 1, 3200);
        copy.Copies = Math.Clamp(copy.Copies, 1, 99);
        return copy;
    }
}

/// <summary>
/// Lazy file store for print-layout preferences. Never touched during normal startup;
/// only the Print flow calls Load/Save.
/// </summary>
public static class PrintSettingsStore
{
    private const string FileName = "print.settings.json";
    internal static string? DirectoryOverrideForTests;

    public static PrintDocumentSettings Load()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path)) return new PrintDocumentSettings();
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<PrintDocumentSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (loaded ?? new PrintDocumentSettings()).Normalized();
        }
        catch { return new PrintDocumentSettings(); }
    }

    public static void Save(PrintDocumentSettings settings)
    {
        try
        {
            var path = GetPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings.Normalized(),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, true);
        }
        catch { /* persistence must never break printing */ }
    }

    private static string GetPath()
    {
        if (!string.IsNullOrWhiteSpace(DirectoryOverrideForTests))
            return Path.Combine(DirectoryOverrideForTests, FileName);
        try
        {
            var dir = Settings.SettingsStore.GetSettingsDirectory();
            if (!string.IsNullOrWhiteSpace(dir)) return Path.Combine(dir, FileName);
        }
        catch { }
        return Path.Combine(AppContext.BaseDirectory, FileName);
    }
}
