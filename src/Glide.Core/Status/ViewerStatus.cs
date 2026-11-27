using System.Globalization;
using Glide.Core.Settings;

namespace Glide.Core.Status;

/// <summary>
/// Stage D (C9): what the status bar and the text overlay show about the current image, as one immutable value.
/// MainWindow builds it from the navigator, the decoded image and the viewport (<c>MainWindow.CurrentViewerStatus</c>);
/// <see cref="ViewerStatusFormatter"/> turns it into text without touching any control, so it is testable and
/// UI-toolkit independent.
/// </summary>
public readonly record struct ViewerStatusModel(
    string? Path,
    int Index,
    int Count,
    int PixelWidth,
    int PixelHeight,
    long FileSize,
    int ZoomPercent,
    (int Width, int Height)? Selection = null,
    DateTime? Modified = null,
    DateTime? Taken = null)
{
    public bool HasImage => !string.IsNullOrWhiteSpace(Path) && Count > 0;
}

/// <summary>Pure text formatting for the status bar stats line and the text overlay ("picture counter").</summary>
public static class ViewerStatusFormatter
{
    public const string ReadyText = "Ready";
    public const string DefaultCounterTemplate = "[{index}/{total}]";

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    /// <summary>The status bar stats line. Segments use non-breaking spaces so a wrapped line breaks only at the
    /// separators, never in the middle of a value such as "3744 × 5616".</summary>
    public static string StatsText(in ViewerStatusModel model, StatusBarOptions options, bool fullPathInTitle)
    {
        if (!model.HasImage) return ReadyText;
        var path = model.Path!;
        var parts = new List<string>();
        if (options.StatFileName) parts.Add(fullPathInTitle ? path : System.IO.Path.GetFileName(path));
        if (options.StatIndex) parts.Add($"{model.Index + 1} / {model.Count}");
        if (options.StatResolution && model.PixelWidth > 0) parts.Add($"{model.PixelWidth} × {model.PixelHeight}");
        if (options.StatZoom) parts.Add($"{model.ZoomPercent}%");
        if (options.StatFileSize && model.FileSize > 0) parts.Add(FormatFileSize(model.FileSize));
        if (options.StatFormat) parts.Add(CodecCapabilityRegistry.GetLongestExtension(path).TrimStart('.').ToUpperInvariant());
        if (model.Selection is { } selection) parts.Add($"Sel {selection.Width} × {selection.Height}");
        return parts.Count == 0
            ? System.IO.Path.GetFileName(path)
            : string.Join("  •  ", parts.Select(p => p.Replace(' ', '\u00A0')));
    }

    /// <summary>The text overlay template with its <c>{token}</c>s replaced (line breaks are applied by the caller).
    /// Round 4: the shared engine (<see cref="TextTemplate"/>), so short tokens, date formats and {sep} work here too.</summary>
    public static string CounterText(in ViewerStatusModel model, string? template, string? separator = TextTemplate.DefaultSeparator) =>
        TextTemplate.Render(string.IsNullOrWhiteSpace(template) ? DefaultCounterTemplate : template, Values(model), separator);

    /// <summary>The template values of the current image.</summary>
    public static TemplateValues Values(in ViewerStatusModel model) =>
        new(model.Path, model.Index, model.Count, model.PixelWidth, model.PixelHeight, model.FileSize, model.ZoomPercent, model.Modified, model.Taken);

    public static string FolderPathOf(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : System.IO.Path.GetDirectoryName(path) ?? string.Empty;
}
