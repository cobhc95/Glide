using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Glide.App.Settings;
using Glide.Core.Settings;
using Xunit;

namespace Glide.Core.Tests;

/// <summary>
/// Global Settings-search coverage contract. Every setting that is wired to a live control must be
/// declared in the catalogue; every catalogued setting must be findable by its label, by every
/// declared search term, and by the visible wording of its control (Content for check boxes/buttons,
/// the sibling label TextBlock for combo/number/text editors); every non-action setting must have a
/// mapped editor and every action setting an actionable companion. Also writes a full audit TSV.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SettingsSearchCoverageTests
{
    public SettingsSearchCoverageTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
    }

    [Fact]
    public async Task EverySetting_IsSearchableAndEditable()
    {
        var failures = new List<string>();
        var auditLines = new List<string>
        {
            "id\tcategory\tkind\tlabel\tmapped\tinCatalog\thasControl\tcontrolEnabled\thasCompanion\tvisibleWording\tfoundByLabel\tfoundByTerms\tfoundByVisibleWording\tverdict"
        };

        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            window.Show();
            try
            {
                var panel = window.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "SearchPanel");
                Assert.NotNull(panel);

                var type = typeof(SettingsWindow);
                var runRebuild = type.GetMethod("RunSearchRebuild", BindingFlags.NonPublic | BindingFlags.Instance);
                var controlsField = type.GetField("_settingControls", BindingFlags.NonPublic | BindingFlags.Instance);
                var companionsMethod = type.GetMethod("CreateSearchCompanionButtons", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(runRebuild);
                Assert.NotNull(controlsField);
                Assert.NotNull(companionsMethod);
                var controls = (Dictionary<string, Control>)controlsField!.GetValue(window)!;
                var catalogIds = SettingsCatalog.All.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

                bool CardExists(string query, string expectedLabel)
                {
                    runRebuild!.Invoke(window, new object[] { query });
                    return panel!.GetLogicalDescendants().OfType<TextBlock>().Any(t => string.Equals(t.Text, expectedLabel, StringComparison.Ordinal));
                }

                // Mirrors the runtime haystack: a combo/number/text editor's visible label is a
                // sibling TextBlock, not Content.
                static string? RowLabel(Control control)
                {
                    if (control is ContentControl { Content: string content } && !string.IsNullOrWhiteSpace(content))
                        return content;
                    if (control.GetVisualParent() is not Panel parent) return null;
                    var siblings = parent.Children;
                    for (var i = siblings.IndexOf(control) - 1; i >= 0; i--)
                    {
                        if (siblings[i] is TextBlock label && !string.IsNullOrWhiteSpace(label.Text)) return label.Text;
                        if (siblings[i] is not TextBlock) break;
                    }
                    return null;
                }

                // Class-of-bug guard: every UI-wired setting must be declared in the catalogue.
                // Exception: the embedded Explorer was removed, so its controls remain in the tree but
                // are intentionally hidden and must not be exposed (see the Explorer-removal contract).
                var hiddenLegacyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "general.newExplorerTabLastLocation",
                    "general.newExplorerTabDefaultDirectory",
                    "appearance.explorerTheme",
                    "tabs.navigateToFolderBehavior"
                };
                foreach (var mappedId in controls.Keys)
                    if (!catalogIds.Contains(mappedId) && !hiddenLegacyIds.Contains(mappedId))
                        failures.Add($"UI setting not declared in SettingsCatalog (unsearchable): {mappedId}");

                foreach (var setting in SettingsCatalog.All)
                {
                    var foundByLabel = CardExists(setting.Label, setting.Label);
                    if (!foundByLabel) failures.Add($"label search missed: {setting.Id} ('{setting.Label}')");

                    var foundByTerms = true;
                    foreach (var term in setting.SearchTerms.Where(t => !string.IsNullOrWhiteSpace(t)))
                    {
                        if (!CardExists(term, setting.Label))
                        {
                            foundByTerms = false;
                            failures.Add($"term search missed: {setting.Id} term='{term}'");
                        }
                    }

                    controls.TryGetValue(setting.Id, out var control);
                    var hasControl = control is not null;
                    var controlEnabled = control?.IsEnabled == true;
                    var visibleWording = control is null ? null : RowLabel(control);

                    var foundByVisibleWording = true;
                    if (!string.IsNullOrWhiteSpace(visibleWording) && !CardExists(visibleWording, setting.Label))
                    {
                        foundByVisibleWording = false;
                        failures.Add($"visible-wording search missed: {setting.Id} wording='{visibleWording}'");
                    }

                    var companions = ((System.Collections.IEnumerable)companionsMethod!.Invoke(window, new object[] { setting.Id })!).Cast<object>().ToList();
                    var hasCompanion = companions.Count > 0;

                    string verdict;
                    if (setting.Kind == SettingKind.Action)
                        verdict = hasCompanion || hasControl ? "actionable" : "NO ACTION";
                    else if (hasControl)
                        verdict = "editable";
                    else
                        verdict = "NO EDITOR";
                    if (verdict is "NO ACTION" or "NO EDITOR") failures.Add($"not editable/actionable: {setting.Id} ({setting.Kind})");

                    auditLines.Add(string.Join('\t',
                        setting.Id, setting.Category, setting.Kind, setting.Label.Replace('\t', ' '),
                        hasControl, catalogIds.Contains(setting.Id), hasControl, controlEnabled, hasCompanion,
                        (visibleWording ?? "").Replace('\t', ' '),
                        foundByLabel, foundByTerms, foundByVisibleWording, verdict));
                }
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });

        try
        {
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "glide-settings-search-audit.tsv"), auditLines);
        }
        catch { }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(60)));
    }
}
