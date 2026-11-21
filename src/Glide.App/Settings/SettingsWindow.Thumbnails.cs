using Avalonia.Interactivity;

namespace Glide.App.Settings;

/// <summary>
/// Explorer thumbnail provider controls (load, apply, restore, refresh).
/// Split from SettingsWindow.axaml.cs; behaviour unchanged.
/// </summary>
public partial class SettingsWindow
{
    // Starts from the stored settings so data without a control (the Custom mode selection) survives Apply.
    private Glide.App.Platform.ThumbnailSettings ReadThumbnailControls() => Glide.App.Platform.ThumbnailSettingsStore.Load() with
    {
        Enabled = ExplorerThumbnailsCheck.IsChecked == true,
        Mode = SelectedText(ThumbnailModeCombo, "Recommended") switch
        {
            "Glide for unsupported formats only" => Glide.App.Platform.ThumbnailProviderMode.UnsupportedOnly,
            "Glide for all supported formats" => Glide.App.Platform.ThumbnailProviderMode.All,
            "Custom" => Glide.App.Platform.ThumbnailProviderMode.Custom,
            _ => Glide.App.Platform.ThumbnailProviderMode.Recommended
        },
        Quality = SelectedText(ThumbnailQualityCombo, "Balanced"),
        PreferEmbedded = ThumbnailPreferEmbeddedCheck.IsChecked == true,
        MaxSourceSize = SelectedText(ThumbnailMaxSourceCombo, "Automatic") switch
        {
            "512 px" => 512,
            "1024 px" => 1024,
            "2048 px" => 2048,
            _ => 0
        }
    };

    private void ApplyThumbnailSettingsClicked(object? sender, RoutedEventArgs e)
    {
        var settings = ReadThumbnailControls();
        Glide.App.Platform.ThumbnailSettingsStore.Apply(settings, out var message, out var applied);
        Glide.App.Platform.ThumbnailSettingsStore.Save(applied);
        ThumbnailDiagnosticsText.Text = message + "\n" + Glide.App.Platform.ThumbnailSettingsStore.Describe(applied);
        PageSubtitle.Text = message;
    }

    private void RestoreThumbnailDefaultsClicked(object? sender, RoutedEventArgs e)
    {
        var settings = Glide.App.Platform.ThumbnailSettings.Default;
        Glide.App.Platform.ThumbnailSettingsStore.Save(settings);
        Glide.App.Platform.ThumbnailSettingsStore.Apply(settings, out var message);
        LoadThumbnailControls(settings);
        ThumbnailDiagnosticsText.Text = message + "\n" + Glide.App.Platform.ThumbnailSettingsStore.Describe(settings);
        PageSubtitle.Text = message;
    }

    private void LoadThumbnailControls(Glide.App.Platform.ThumbnailSettings thumbnails)
    {
        ExplorerThumbnailsCheck.IsChecked = thumbnails.Enabled;
        SelectByText(ThumbnailModeCombo, thumbnails.Mode switch
        {
            Glide.App.Platform.ThumbnailProviderMode.UnsupportedOnly => "Glide for unsupported formats only",
            Glide.App.Platform.ThumbnailProviderMode.All => "Glide for all supported formats",
            Glide.App.Platform.ThumbnailProviderMode.Custom => "Custom",
            _ => "Recommended"
        });
        SelectByText(ThumbnailQualityCombo, thumbnails.Quality);
        SelectByText(ThumbnailMaxSourceCombo, thumbnails.MaxSourceSize switch { 512 => "512 px", 1024 => "1024 px", 2048 => "2048 px", _ => "Automatic" });
        ThumbnailPreferEmbeddedCheck.IsChecked = thumbnails.PreferEmbedded;
    }

    private void RefreshThumbnailsClicked(object? sender, RoutedEventArgs e)
    {
        // Windows owns the thumbnail cache: delete this user's databases and ask the shell to rebuild.
        var removed = Glide.App.Platform.ThumbnailCacheRefresher.Refresh();
        ThumbnailDiagnosticsText.Text = $"Thumbnail cache refresh requested ({removed} cache files removed). " +
                                        "Restart Explorer if thumbnails still appear stale.";
        PageSubtitle.Text = "Thumbnail cache refresh requested.";
    }
}
