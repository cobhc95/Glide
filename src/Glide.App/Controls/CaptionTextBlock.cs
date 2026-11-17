using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Glide.App.Controls;

/// <summary>
/// A wrapping caption clamped to <see cref="TextBlock.MaxLines"/> that, like Windows Explorer, always ends a clipped
/// name with an ellipsis. Avalonia's TextBlock drops the overflow silently when the last visible line ends at a break
/// opportunity ("aerial-view-green-mountains-8k-"), so the last line is rebuilt as "rest of the text" trimmed to one line.
/// <see cref="TextBlock.Text"/> stays the full name.
/// </summary>
public sealed class CaptionTextBlock : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override TextLayout CreateTextLayout(string? text)
    {
        var layout = base.CreateTextLayout(text);
        if (string.IsNullOrEmpty(text) || MaxLines <= 0 || TextWrapping == TextWrapping.NoWrap) return layout;
        var lines = layout.TextLines;
        var shown = 0;
        foreach (var line in lines) shown += line.Length;
        if (shown >= text.Length) return PreferBreakBeforeExtension(text, layout); // everything fits
        var keep = 0;
        for (var i = 0; i < Math.Min(MaxLines - 1, lines.Count); i++) keep += lines[i].Length;
        // Keep the first lines as laid out, then the whole remainder on one line, trimmed with an ellipsis.
        var head = text[..keep].TrimEnd();
        var tail = text[keep..].Replace("\n", " ", StringComparison.Ordinal);
        var trimmedTail = TrimToWidth(tail);
        return base.CreateTextLayout(head.Length == 0 ? trimmedTail : head + "\n" + trimmedTail);
    }

    /// <summary>
    /// When a wrap splits the file extension ("3m90fxiwwfo61.pn" / "g"), break before the dot instead so the extension
    /// stays whole ("3m90fxiwwfo61" / ".png"), provided the result still fits in <see cref="TextBlock.MaxLines"/>;
    /// otherwise the original layout is kept.
    /// </summary>
    private TextLayout PreferBreakBeforeExtension(string text, TextLayout layout)
    {
        var lines = layout.TextLines;
        if (lines.Count < 2) return layout;
        var dot = ExtensionBreakIndex(text);
        if (dot < 0) return layout;
        var splitsExtension = false;
        var position = 0;
        for (var i = 0; i < lines.Count - 1; i++)
        {
            position += lines[i].Length;
            if (position > dot && position < text.Length) { splitsExtension = true; break; }
        }
        if (!splitsExtension) return layout;
        var candidate = base.CreateTextLayout(text[..dot] + "\n" + text[dot..]);
        return candidate.TextLines.Count <= MaxLines ? candidate : layout;
    }

    /// <summary>Index of the extension's dot ("name.png" → 4), or -1 when there is no short extension to keep whole.</summary>
    internal static int ExtensionBreakIndex(string text)
    {
        var dot = text.LastIndexOf('.');
        if (dot <= 0 || dot >= text.Length - 1 || text.Length - dot > 10) return -1;
        for (var i = dot + 1; i < text.Length; i++) if (!char.IsLetterOrDigit(text[i])) return -1;
        return dot;
    }

    private string TrimToWidth(string tail)
    {
        var width = double.IsFinite(Bounds.Width) && Bounds.Width > 0 ? Bounds.Width - Padding.Left - Padding.Right : MaxWidth;
        if (Parent is Control { Width: var w } && double.IsFinite(w)) width = Math.Min(width, w);
        if (!double.IsFinite(width) || width <= 0) return tail;
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        double Measure(string value) =>
            new TextLayout(value, typeface, FontSize, null, TextAlignment.Left, TextWrapping.NoWrap).WidthIncludingTrailingWhitespace;
        if (Measure(tail) <= width) return tail;
        // Longest prefix that still fits with the ellipsis (binary search; only runs for clipped captions).
        int lo = 0, hi = tail.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Measure(tail[..mid] + "…") <= width) lo = mid; else hi = mid - 1;
        }
        return tail[..lo].TrimEnd() + "…";
    }
}
