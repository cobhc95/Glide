using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Glide.App.Printing;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Data;
using Glide.Core.Reset;

namespace Glide.Core.Tests;

/// <summary>
/// Guard rail: every top-level window Glide can open remembers its placement through WindowPlacementService, or is
/// allow-listed in WindowPlacementCatalog.AllowList with a reason. A new Window subclass, or a new ad-hoc
/// <c>new Window { ... }</c> dialog, fails here until it is registered.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Window")]
public sealed class WindowPlacementGuardTests
{
    public WindowPlacementGuardTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    /// <summary>How to construct each tracked Window subclass headless. Add the new window here AND track it.</summary>
    private static readonly Dictionary<Type, Func<Window>> Factories = new()
    {
        [typeof(PrintWindow)] = () =>
        {
            var bitmap = new WriteableBitmap(new Avalonia.PixelSize(8, 8), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            return new PrintWindow(new PrintWindowArgs("x.jpg", bitmap, 8, 8, 96, 96, _ => throw new InvalidOperationException()),
                new PrintQueryCache(FakePrintQueries.Standard()));
        },
        [typeof(TitleBarCustomizerWindow)] = () => new TitleBarCustomizerWindow(TitleBarButtonCatalog.DefaultButtonIds),
        [typeof(StatusBarCustomizerWindow)] = () => new StatusBarCustomizerWindow(string.Empty),
        [typeof(ResetConfirmWindow)] = () => new ResetConfirmWindow(ResetPlanner.Plan(
            new ResetEnvironment(Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath(), [".jpg"]), [ResetItemId.Caches])),
        [typeof(ResetResultWindow)] = () => new ResetResultWindow("Reset Glide", "Done", ""),
        [typeof(BackupImportWindow)] = () => new BackupImportWindow(new GlideBackupService.ImportPlan
        {
            SourcePath = "x.glidebackup",
            Manifest = new Glide.Core.Data.GlideBackupManifest(),
            Categories = [ResetItemId.AllSettings],
            Settings = new GlideSettingsState(),
        }),
        [typeof(Glide.App.Updates.UpdatePromptWindow)] = () => new Glide.App.Updates.UpdatePromptWindow(new Glide.App.Updates.UpdatePromptModel("5.0.0", "5.0.1", "Released today on GitHub", "https://github.com/cobhc95/Glide/releases/tag/v5.0.1")),
    };

    [Fact]
    public void Every_Window_subclass_is_tracked_or_allow_listed_with_a_reason()
    {
        var windows = typeof(PrintWindow).Assembly.GetTypes()
            .Where(t => typeof(Window).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        Assert.Contains(typeof(PrintWindow), windows);
        var problems = new List<string>();
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            foreach (var type in windows)
            {
                if (WindowPlacementCatalog.AllowList.TryGetValue(type.FullName!, out var reason))
                {
                    if (string.IsNullOrWhiteSpace(reason) || reason.Length < 20) problems.Add($"{type.FullName}: allow-listed without a real reason");
                    continue;
                }
                if (!Factories.TryGetValue(type, out var factory))
                {
                    problems.Add($"{type.FullName}: new window type. Call WindowPlacementService.Track(this, \"<key>\") in its constructor, add the key to WindowPlacementCatalog.Entries and a factory to WindowPlacementGuardTests (or allow-list it with a reason).");
                    continue;
                }
                var window = factory();
                try
                {
                    var key = WindowPlacementService.KeyOf(window);
                    if (key is null) problems.Add($"{type.FullName}: not tracked by WindowPlacementService");
                    else if (WindowPlacementCatalog.Find(key) is null) problems.Add($"{type.FullName}: key '{key}' missing from WindowPlacementCatalog.Entries");
                }
                finally { window.Close(); }
            }
        });
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_ad_hoc_dialog_in_the_app_source_is_tracked_with_a_catalogued_key()
    {
        var root = Path.Combine(SourceText.RepoRoot, "src", "Glide.App");
        var creation = new Regex(@"(?:var|Window)\s+(\w+)\s*=\s*(?:new\s+Window\b|CreateContentSizedDialog\()", RegexOptions.Compiled);
        var problems = new List<string>();
        var keysUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            var rel = Path.GetRelativePath(root, file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match track in Regex.Matches(lines[i], @"(?:PopupPlacementStore|WindowPlacementService)\.Track\(\s*\w+\s*,\s*""([^""]+)"""))
                    keysUsed.Add(track.Groups[1].Value);
                var m = creation.Match(lines[i]);
                if (!m.Success) continue;
                var variable = m.Groups[1].Value;
                var following = lines.Skip(i).Take(45).ToList();
                var tracked = following.Any(l => Regex.IsMatch(l, $@"(?:PopupPlacementStore|WindowPlacementService)\.Track\(\s*{Regex.Escape(variable)}\s*,"));
                // A factory (CreateContentSizedDialog) returns the window; each caller's own assignment is checked instead.
                var returned = following.Any(l => Regex.IsMatch(l, $@"\breturn\s+{Regex.Escape(variable)}\s*;"));
                if (returned) continue;
                if (!tracked) problems.Add($"{rel}:{i + 1}: '{variable}' is a new top-level window but is never passed to WindowPlacementService.Track / PopupPlacementStore.Track");
            }
        }
        foreach (var key in keysUsed)
            if (WindowPlacementCatalog.Find(key) is null) problems.Add($"key '{key}' is used but missing from WindowPlacementCatalog.Entries");
        foreach (var entry in WindowPlacementCatalog.Entries)
            if (entry.Key != SettingsWindowPlacement.StoreKey && !keysUsed.Contains(entry.Key)) problems.Add($"catalogue key '{entry.Key}' is not used by any window (stale entry?)");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Catalogue_keys_are_unique_and_stable_ids()
    {
        var keys = WindowPlacementCatalog.Entries.Select(e => e.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(keys, k => Assert.Matches("^[a-z][a-z0-9-]*$", k));
        Assert.Equal(SettingsWindowPlacement.StoreKey, WindowPlacementCatalog.Find("settings")!.Key);
    }
}
