namespace Glide.Core;

/// <summary>Framework-free state for a Window-in-Window image overlay.</summary>
public sealed record OverlayState(
    Guid Id,
    string Path,
    double X,
    double Y,
    double Width,
    double Height,
    double Opacity = 1.0,
    double Zoom = 1.0,
    double PanX = 0,
    double PanY = 0,
    int ZIndex = 0)
{
    public OverlayState Normalize()
    {
        var width = Math.Clamp(Width, 80, 4096);
        var height = Math.Clamp(Height, 60, 4096);
        return this with
        {
            X = double.IsFinite(X) ? X : 0,
            Y = double.IsFinite(Y) ? Y : 0,
            Width = width,
            Height = height,
            Opacity = Math.Clamp(double.IsFinite(Opacity) ? Opacity : 1, .1, 1),
            Zoom = Math.Clamp(double.IsFinite(Zoom) ? Zoom : 1, .1, 16),
            PanX = double.IsFinite(PanX) ? PanX : 0,
            PanY = double.IsFinite(PanY) ? PanY : 0
        };
    }
}

public static class OverlayLayoutStore
{
    public static void Save(string path, IEnumerable<OverlayState> overlays)
    {
        var payload = new { version = 1, overlays = overlays.Select(x => x.Normalize()).ToArray() };
        var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var temp = path + ".tmp"; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(temp, json);
        // Validate the exact bytes before replacing the last known-good layout.
        _ = Parse(File.ReadAllText(temp));
        try { if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true); else File.Move(temp, path); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    public static IReadOnlyList<OverlayState> Load(string path)
    {
        if (!File.Exists(path)) return Array.Empty<OverlayState>();
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch
        {
            try { var backup = path + ".bak"; return File.Exists(backup) ? Parse(File.ReadAllText(backup)) : Array.Empty<OverlayState>(); } catch { return Array.Empty<OverlayState>(); }
        }
    }
    public static bool TryLoad(string path, out IReadOnlyList<OverlayState> overlays)
    {
        overlays = Array.Empty<OverlayState>();
        if (!File.Exists(path)) return false;
        try { overlays = Parse(File.ReadAllText(path)); return true; }
        catch { try { var backup = path + ".bak"; if (!File.Exists(backup)) return false; overlays = Parse(File.ReadAllText(backup)); return true; } catch { return false; } }
    }
    private static IReadOnlyList<OverlayState> Parse(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("version", out var version) && version.GetInt32() == 1 && doc.RootElement.TryGetProperty("overlays", out var rows))
            return System.Text.Json.JsonSerializer.Deserialize<OverlayState[]>(rows.GetRawText())?.Select(x => x.Normalize()).ToArray() ?? Array.Empty<OverlayState>();
        return System.Text.Json.JsonSerializer.Deserialize<OverlayState[]>(doc.RootElement.GetRawText())?.Select(x => x.Normalize()).ToArray() ?? Array.Empty<OverlayState>();
    }
}
