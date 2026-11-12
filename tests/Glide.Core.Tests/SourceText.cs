namespace Glide.Core.Tests;

/// <summary>
/// Source-contract tests grep production source. MainWindow is a partial class split across
/// MainWindow.*.cs (4.5.2), so a contract about "MainWindow.axaml.cs" must see every partial.
/// </summary>
internal static class SourceText
{
    public static string Read(string path)
    {
        var text = File.ReadAllText(path);
        if (!string.Equals(Path.GetFileName(path), "MainWindow.axaml.cs", StringComparison.OrdinalIgnoreCase))
            return text;
        var directory = Path.GetDirectoryName(path)!;
        var parts = Directory.EnumerateFiles(directory, "MainWindow.*.cs")
            .Where(p => !string.Equals(Path.GetFileName(p), "MainWindow.axaml.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText);
        return text + "\n" + string.Join("\n", parts);
    }
}
