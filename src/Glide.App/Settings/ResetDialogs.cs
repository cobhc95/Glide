using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Glide.App.Services;
using Glide.Core.Reset;

namespace Glide.App.Settings;

/// <summary>What the confirmation dialog returns when the user chooses to go ahead.</summary>
public sealed record ResetConfirmation(bool SaveBackup);

/// <summary>
/// "Reset the selected items?" - the modal confirmation of Settings &gt; Windows &amp; developer &gt; Reset Glide.
/// Lists exactly the selected items; Cancel has the focus and Esc cancels, Enter never starts the reset.
/// </summary>
public sealed class ResetConfirmWindow : Window
{
    public const double DialogWidth = 440;
    public const string RegistryWarningText = "Registry entries removed: Glide stops being offered in Open With until it is registered again.";

    public Button CancelButton { get; }
    public Button ConfirmButton { get; }
    public CheckBox BackupCheck { get; }
    public TextBlock IntroText { get; }
    public TextBlock WarningText { get; }
    public IReadOnlyList<string> ListedItems { get; }
    public ResetPlan Plan { get; }

    public ResetConfirmWindow(ResetPlan plan)
    {
        Plan = plan;
        Title = "Reset Glide";
        Width = DialogWidth;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("BrushSurface"));

        var titleText = new TextBlock { Text = "Reset the selected items?", FontSize = 15, FontWeight = FontWeight.Medium };
        titleText.Classes.Add("resetDialogTitle");
        IntroText = new TextBlock
        {
            Text = plan.NeedsRestart ? "Glide will close and restart. This can't be undone." : "This can't be undone.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        ListedItems = plan.Items.Select(x => x.Item.Title).ToArray();
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 8), Spacing = 2 };
        AutomationProperties.SetName(list, "Items that will be reset");
        foreach (var title in ListedItems)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*") };
            row.Children.Add(new TextBlock { Text = "•", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center });
            var text = new TextBlock { Text = title, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            list.Children.Add(row);
        }

        var backupLabel = new StackPanel();
        var backupTitle = new TextBlock { Text = "Save a backup of my settings first" };
        backupTitle.Classes.Add("resetItemTitle");
        backupLabel.Children.Add(backupTitle);
        var backupHint = new TextBlock { Text = "Written to Documents\\Glide backups", FontSize = 12, Margin = new Thickness(0, 1, 0, 0) };
        backupHint.Classes.Add("resetItemHint");
        backupLabel.Children.Add(backupHint);
        BackupCheck = new CheckBox { IsChecked = true, Content = backupLabel, Padding = new Thickness(0, 6) };
        BackupCheck.Classes.Add("resetItem");
        AutomationProperties.SetName(BackupCheck, "Save a backup of my settings first");

        WarningText = new TextBlock { Text = RegistryWarningText, IsVisible = plan.Includes(ResetItemId.RegistryEntries), Margin = new Thickness(0, 8, 0, 0) };
        WarningText.Classes.Add("resetWarning");

        CancelButton = new Button { Content = "Cancel", IsCancel = true };
        CancelButton.Classes.Add("secondaryAction");
        CancelButton.Classes.Add("resetBar");
        CancelButton.Click += (_, _) => Close(null);
        ConfirmButton = new Button { Content = plan.NeedsRestart ? "Reset and restart" : "Reset" };
        ConfirmButton.Classes.Add("dangerAction");
        ConfirmButton.Click += (_, _) => Close(new ResetConfirmation(BackupCheck.IsChecked == true));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(ConfirmButton);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(titleText);
        root.Children.Add(IntroText);
        root.Children.Add(list);
        root.Children.Add(BackupCheck);
        root.Children.Add(WarningText);
        root.Children.Add(buttons);
        Content = root;

        PopupPlacementStore.Track(this, "reset-confirm");
        // The destructive button is never the default: the dialog opens with Cancel focused, Enter activates the focused
        // button only, and Esc closes the dialog.
        Opened += (_, _) => CancelButton.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(null);
        };
    }
}

/// <summary>Plain report window: what a reset could not do (or why it did not start).</summary>
public sealed class ResetResultWindow : Window
{
    public TextBlock DetailsText { get; }
    public Button CloseButton { get; }

    public ResetResultWindow(string title, string summary, string details)
    {
        Title = "Reset Glide";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("BrushSurface"));

        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.Medium };
        var intro = new TextBlock { Text = summary, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        DetailsText = new TextBlock { Text = details, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        DetailsText.Classes.Add("resetItemHint");
        var scroll = new ScrollViewer { MaxHeight = 240, Content = DetailsText, Margin = new Thickness(0, 10, 0, 0), IsVisible = !string.IsNullOrWhiteSpace(details) };
        CloseButton = new Button { Content = "Close", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        CloseButton.Classes.Add("secondaryAction");
        CloseButton.Classes.Add("resetBar");
        CloseButton.Click += (_, _) => Close();

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(heading);
        root.Children.Add(intro);
        root.Children.Add(scroll);
        root.Children.Add(CloseButton);
        Content = root;
        PopupPlacementStore.Track(this, "reset-result");
        Opened += (_, _) => CloseButton.Focus();
    }
}
