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

    /// <summary>
    /// Search must match everything a user can read around a setting, not only its label: the control's
    /// tooltip, every drop-down option, and the other text (labels, hints, their tooltips) in its row.
    /// </summary>
    [Fact]
    public async Task EverySetting_IsSearchableByTooltipsOptionsAndRowText()
    {
        var failures = new List<string>();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            window.Show();
            try
            {
                var panel = window.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "SearchPanel");
                var type = typeof(SettingsWindow);
                var runRebuild = type.GetMethod("RunSearchRebuild", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var controls = (Dictionary<string, Control>)type.GetField("_settingControls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

                bool Finds(string query, string expectedLabel)
                {
                    runRebuild.Invoke(window, new object[] { query });
                    return panel.GetLogicalDescendants().OfType<TextBlock>().Any(t => string.Equals(t.Text, expectedLabel, StringComparison.Ordinal));
                }

                static IEnumerable<string> TextsOf(Control c)
                {
                    if (ToolTip.GetTip(c) is string tip && !string.IsNullOrWhiteSpace(tip)) yield return tip;
                    if (c is TextBlock { Text: { Length: > 0 } text }) yield return text;
                    if (c is ItemsControl items)
                        foreach (var item in items.Items)
                        {
                            if (item is string option && option.Length > 0) yield return option;
                            else if (item is ContentControl { Content: string optionContent } && optionContent.Length > 0) yield return optionContent;
                        }
                }

                foreach (var setting in SettingsCatalog.All)
                {
                    if (!controls.TryGetValue(setting.Id, out var control)) continue;
                    var texts = new List<string>(TextsOf(control));
                    // The row is the control's panel parent when it is a grid row (label + editor).
                    if (control.GetVisualParent() is Grid row)
                        foreach (var sibling in row.Children.OfType<Control>().Where(c => !ReferenceEquals(c, control)))
                            texts.AddRange(TextsOf(sibling));

                    foreach (var text in texts.Distinct(StringComparer.Ordinal))
                        if (!Finds(text, setting.Label))
                            failures.Add($"{setting.Id}: search by on-screen text missed '{(text.Length > 60 ? text[..60] + "..." : text)}'");
                }
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(60)));
    }

    /// <summary>
    /// Class-of-bug guard: every interactive control on a Settings page must be mapped to a catalogued
    /// setting (so it is searchable and editable from search), except the few that are not settings.
    /// </summary>
    [Fact]
    public async Task EveryInteractiveSettingsControl_IsMappedToACatalogSetting()
    {
        var unmapped = new List<string>();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            window.Show();
            try
            {
                var type = typeof(SettingsWindow);
                var controls = (Dictionary<string, Control>)type.GetField("_settingControls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var mapped = controls.Values.ToHashSet();
                // Not settings: the search/filter inputs, lists driven by their own editors, and
                // mirrors of a mapped control.
                var exempt = new HashSet<string>(StringComparer.Ordinal)
                {
                    "SearchBox", "SearchCategoryCombo", "HotkeyFilterBox", "HotkeyList", "StatusVisibleMirrorCheck", "StatusHoverMirrorCheck",
                    "FullscreenXCloseCheck"
                };
                foreach (var control in window.GetLogicalDescendants().OfType<Control>())
                {
                    var interactive = control is CheckBox or ComboBox or NumericUpDown or TextBox or Slider or RadioButton or ToggleSwitch;
                    // Disabled controls are placeholders for unavailable features, not settings.
                    if (!interactive || mapped.Contains(control) || !control.IsEnabled) continue;
                    if (control.Name is { } n && exempt.Contains(n)) continue;
                    // Controls inside a dynamically built grid (gesture matrix, profile rows) are covered by their own catalogue action.
                    if (control.GetLogicalAncestors().OfType<Control>().Any(a => a.Name is "GestureMatrixHost" or "ProfileRows" or "SearchPanel")) continue;
                    var wording = ToolTip.GetTip(control) as string ?? (control as ContentControl)?.Content as string ?? "";
                    // The per-profile rename box is built for each saved profile (searchable through "Profiles & presets").
                    if (wording.StartsWith("Rename this profile", StringComparison.Ordinal)) continue;
                    unmapped.Add($"{control.GetType().Name} '{control.Name ?? "(unnamed)"}' {wording}".Trim());
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
        Assert.True(unmapped.Count == 0, "Interactive settings controls with no catalogued setting (unsearchable):\n" + string.Join("\n", unmapped.Take(60)));
    }

    private static List<string> ResultLabels(StackPanel panel)
        => panel.Children.Select(child => child switch
        {
            Border { Child: Grid grid } => grid.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
            TextBlock header => "#" + header.Text,
            _ => null
        }).Where(label => label is not null).Select(label => label!).ToList();

    [Fact]
    public async Task Search_lists_title_matches_before_related_matches_and_related_can_be_hidden()
    {
        var failures = new List<string>();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            window.Show();
            try
            {
                var panel = window.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "SearchPanel");
                var run = typeof(SettingsWindow).GetMethod("RunSearchRebuild", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var related = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.Name == "SearchShowRelatedCheck");

                // "strength" is in the title "Always-on-top strength" but only in the *description* of "Glow intensity".
                run.Invoke(window, new object[] { "strength" });
                var labels = ResultLabels(panel);
                var title = labels.IndexOf("Always-on-top strength");
                var header = labels.IndexOf("#Related settings");
                var glow = labels.IndexOf("Glow intensity");
                if (title < 0) failures.Add("title match 'Always-on-top strength' missing");
                if (header < 0 || glow < 0) failures.Add("related match 'Glow intensity' / header missing while related results are shown");
                if (title >= 0 && header >= 0 && title > header) failures.Add("a title match was listed after the related header");
                if (header >= 0 && glow >= 0 && glow < header) failures.Add("a related match was listed before the related header");

                related.IsChecked = false;
                run.Invoke(window, new object[] { "strength" });
                labels = ResultLabels(panel);
                if (!labels.Contains("Always-on-top strength")) failures.Add("title match missing when related results are hidden");
                if (labels.Contains("Glow intensity") || labels.Contains("#Related settings")) failures.Add("related results still shown after turning them off");
                related.IsChecked = true;

                // Page text must not leak into a setting's search entry: a rare token returns almost nothing.
                run.Invoke(window, new object[] { "hwnd" });
                labels = ResultLabels(panel);
                if (labels.Contains("Browsing / image quality profile")) failures.Add("Performance page text leaked into unrelated settings ('hwnd' matched the quality profile)");
                if (labels.Count > 6) failures.Add($"'hwnd' returned {labels.Count} results; page text is leaking into search entries");

                // The settings from the report are findable by their on-screen wording, as editable cards.
                foreach (var (query, expected) in new[]
                {
                    ("keep glide on top", "Always on top"), ("exiting fullscreen", "Behavior after exiting fullscreen"),
                    ("restoring from fullscreen", "Auto-center window on restore"), ("selection buffer", "Selection buffer around the picture")
                })
                {
                    run.Invoke(window, new object[] { query });
                    if (!ResultLabels(panel).Contains(expected)) failures.Add($"'{query}' did not find '{expected}'");
                }
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures));
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
