using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Glide.App.Services;
using Glide.Core.Data;
using Glide.Core.Reset;

namespace Glide.App.Settings;

/// <summary>What the import dialog returns when the user goes ahead.</summary>
public sealed record BackupImportChoice(IReadOnlyList<ResetItemId> Categories, bool SaveBackupFirst);

/// <summary>
/// "Import Glide backup" - lists the categories PRESENT in the chosen file (the shared <see cref="DataCategorySelector"/>,
/// all ticked) so the user can untick what they do not want, says which ones are staged and which replace data right away,
/// and offers a backup of the data that will be replaced. Cancel has the focus, Esc cancels, Enter never imports.
/// </summary>
public sealed class BackupImportWindow : Window
{
    public const double DialogWidth = 560;

    public Button CancelButton { get; }
    public Button ImportButton { get; }
    public CheckBox BackupCheck { get; }
    public TextBlock SummaryText { get; }
    public TextBlock NotesText { get; }
    internal DataCategorySelector Selector { get; }

    internal BackupImportWindow(GlideBackupService.ImportPlan plan)
    {
        Title = "Import Glide backup";
        Width = DialogWidth;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("BrushSurface"));

        var titleText = new TextBlock { Text = "Import from this backup?", FontSize = 15, FontWeight = FontWeight.Medium };
        var made = DescribeOrigin(plan.Manifest);
        SummaryText = new TextBlock
        {
            Text = Path.GetFileName(plan.SourcePath) + (made.Length > 0 ? " - " + made : string.Empty),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10),
        };

        Selector = new DataCategorySelector("BackupImport", GlideDataCapabilities.Backup,
            GlideDataCategoryCatalog.With(GlideDataCapabilities.Backup).Where(x => plan.Categories.Contains(x.Id)));
        Selector.SetAll(true);
        AutomationProperties.SetName(Selector, "What to import");

        var how = new TextBlock
        {
            Text = "All settings and hotkeys are staged: OK or Apply in Settings saves them, Cancel discards them. " +
                   "Profiles, window positions, recent files and the last session replace what is on this PC right away.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        };
        how.Classes.Add("resetItemHint");

        var backupLabel = new StackPanel();
        var backupTitle = new TextBlock { Text = "Save a backup of what will be replaced first" };
        backupTitle.Classes.Add("resetItemTitle");
        var backupHint = new TextBlock { Text = "Written to Documents\\Glide backups", FontSize = 12, Margin = new Thickness(0, 1, 0, 0) };
        backupHint.Classes.Add("resetItemHint");
        backupLabel.Children.Add(backupTitle);
        backupLabel.Children.Add(backupHint);
        BackupCheck = new CheckBox { IsChecked = true, Content = backupLabel, Padding = new Thickness(0, 6), Margin = new Thickness(0, 6, 0, 0) };
        BackupCheck.Classes.Add("resetItem");
        AutomationProperties.SetName(BackupCheck, "Save a backup of what will be replaced first");

        var notes = GlideBackupService.Notes(plan);
        NotesText = new TextBlock
        {
            Text = notes.Length == 0 ? string.Empty : "Left out on purpose: " + notes + ".",
            IsVisible = notes.Length > 0, Margin = new Thickness(0, 8, 0, 0),
        };
        NotesText.Classes.Add("resetWarning");

        CancelButton = new Button { Content = "Cancel", IsCancel = true };
        CancelButton.Classes.Add("secondaryAction");
        CancelButton.Classes.Add("resetBar");
        CancelButton.Click += (_, _) => Close(null);
        ImportButton = new Button { Content = "Import" };
        ImportButton.Classes.Add("primaryAction");
        ImportButton.Classes.Add("resetBar");
        ImportButton.Click += (_, _) =>
        {
            var chosen = Selector.Selected;
            if (chosen.Count == 0) return;
            Close(new BackupImportChoice(chosen, BackupCheck.IsChecked == true && BackupCheck.IsVisible));
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(ImportButton);

        void Refresh()
        {
            var chosen = Selector.Selected;
            ImportButton.IsEnabled = chosen.Count > 0;
            ImportButton.Content = chosen.Count == 0 ? "Import" : $"Import {chosen.Count} selected";
            // The backup of current data only matters when something is replaced in place.
            BackupCheck.IsVisible = chosen.Any(x => !GlideBackupService.IsStaged(x));
        }
        Selector.SelectionChanged += Refresh;
        Refresh();

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(titleText);
        root.Children.Add(SummaryText);
        root.Children.Add(Selector);
        root.Children.Add(how);
        root.Children.Add(BackupCheck);
        root.Children.Add(NotesText);
        root.Children.Add(buttons);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, MaxHeight = 720 };

        PopupPlacementStore.Track(this, "backup-import");
        // Importing is never the default: Cancel has the focus, Enter activates the focused button only, Esc closes.
        Opened += (_, _) => CancelButton.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(null);
        };
    }

    private static string DescribeOrigin(GlideBackupManifest manifest)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.AppVersion)) parts.Add("made with Glide " + Clip(manifest.AppVersion));
        if (DateTime.TryParse(manifest.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
            parts.Add("on " + created.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture));
        return string.Join(" ", parts);
    }

    private static string Clip(string text) => text.Length <= 32 ? text : text[..32] + "...";
}
