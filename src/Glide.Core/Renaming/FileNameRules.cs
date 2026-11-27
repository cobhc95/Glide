namespace Glide.Core.Renaming;

/// <summary>Windows file-name rules without touching the disk (the Explorer's own check also looks for clashes).</summary>
public static class FileNameRules
{
    public const string InvalidCharacters = "\\/:*?\"<>|";

    private static readonly System.Buffers.SearchValues<char> Invalid = System.Buffers.SearchValues.Create(InvalidCharacters);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Null when <paramref name="name"/> is a legal file name, otherwise a short user-facing reason.</summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A file name can't be empty.";
        if (name.AsSpan().IndexOfAny(Invalid) >= 0 || name.Any(char.IsControl)) return "A file name can't contain any of the following characters: \\ / : * ? \" < > |";
        if (name is "." or "..") return "A file name can't be \".\" or \"..\".";
        if (name[^1] is '.' or ' ') return "A file name can't end with a period or a space.";
        if (name.Length > 255) return "The file name is too long (255 characters maximum).";
        var dot = name.IndexOf('.');
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
        if (Reserved.Contains(stem)) return $"\"{name}\" is a reserved name and can't be used.";
        return null;
    }

    /// <summary>Removes every character a file name can't contain.</summary>
    public static string StripInvalid(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var chars = text.Where(c => Invalid.Contains(c) is false && !char.IsControl(c)).ToArray();
        return new string(chars);
    }
}
