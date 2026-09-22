using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Glide.App.Services;

namespace Glide.App.Printing;

/// <summary>
/// Arguments for the Glide Print dialog. The preview bitmap is owned by the caller and
/// is never disposed here; the full-resolution bitmap is resolved lazily at spool time so
/// opening the dialog never pays a full decode up front.
/// </summary>
public sealed record PrintWindowArgs(
    string ImagePath,
    Bitmap PreviewBitmap,
    int ImageWidthPx,
    int ImageHeightPx,
    double DpiX,
    double DpiY,
    Func<CancellationToken, Task<(Bitmap Bitmap, int WidthPx, int HeightPx)>> FullResolver);

/// <summary>
/// Native-looking Glide Print window with a live WYSIWYG page preview. Preview and spool
/// share <see cref="PrintLayoutEngine.Compute"/>, and both use the driver's real printable
/// rectangle (never the whole sheet). Printer enumeration and driver queries happen only
/// here — never during normal Glide startup.
/// </summary>
public sealed class PrintWindow : Window
{
    private readonly PrintWindowArgs _args;
    private PrintDocumentSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _printCts;
    private bool _printing;
    private bool _loadingPrinters = true;

    private PrintPaperGeometry? _paperPortrait;
    private PrintPrinterCapabilities? _caps;

    private readonly ComboBox _printerCombo = new() { MinWidth = 240 };
    private readonly ComboBox _paperCombo = new() { MinWidth = 200 };
    private readonly ComboBox _sourceCombo = new() { MinWidth = 200 };
    private readonly RadioButton _portraitRadio = new() { Content = "Portrait" };
    private readonly RadioButton _landscapeRadio = new() { Content = "Landscape" };
    private readonly NumericUpDown _copiesBox = new() { Minimum = 1, Maximum = 99, Increment = 1, Width = 90 };
    private readonly ComboBox _scalingCombo = new() { MinWidth = 200 };
    private readonly NumericUpDown _customScaleBox = new() { Minimum = 1, Maximum = 3200, Increment = 5, Width = 110 };
    private readonly CheckBox _keepAspectCheck = new() { Content = "Keep aspect ratio" };
    private readonly CheckBox _autoRotateCheck = new() { Content = "Automatically rotate for best fit" };
    private readonly ComboBox _hAlignCombo = new() { MinWidth = 130 };
    private readonly ComboBox _vAlignCombo = new() { MinWidth = 130 };
    private readonly ComboBox _colorCombo = new() { MinWidth = 130 };
    private readonly NumericUpDown _marginLeft = new() { Minimum = 0, Maximum = 75, Width = 100 };
    private readonly NumericUpDown _marginTop = new() { Minimum = 0, Maximum = 75, Width = 100 };
    private readonly NumericUpDown _marginRight = new() { Minimum = 0, Maximum = 75, Width = 100 };
    private readonly NumericUpDown _marginBottom = new() { Minimum = 0, Maximum = 75, Width = 100 };
    private readonly PrintPreviewControl _preview = new() { MinWidth = 320, MinHeight = 320 };
    private readonly TextBlock _infoText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _warningText = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#E5B53B")) };
    private readonly TextBlock _statusText = new();
    private readonly Button _printButton = new() { Content = "Print" };
    private readonly Button _cancelButton = new() { Content = "Cancel" };
    private readonly Button _propertiesButton = new() { Content = "Properties…" };
    private Control? _sourceRow;
    private Bitmap? _grayscalePreview;
    private Bitmap? _grayscaleSource;

    public PrintWindow(PrintWindowArgs args)
    {
        _args = args;
        _settings = PrintSettingsStore.Load();
        Title = "Print — " + (string.IsNullOrWhiteSpace(args.ImagePath) ? "image" : Path.GetFileName(args.ImagePath));
        Width = 1020;
        Height = 680;
        MinWidth = 880;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _scalingCombo.ItemsSource = new[] { "Best fit", "Fill page", "Actual size", "Custom scale", "Stretch" };
        _hAlignCombo.ItemsSource = new[] { "Left", "Centre", "Right" };
        _vAlignCombo.ItemsSource = new[] { "Top", "Centre", "Bottom" };
        _colorCombo.ItemsSource = new[] { "Colour", "Grayscale" };
        var step = PrintUnits.UseMillimetres ? 0.5 : 0.05;
        foreach (var box in new[] { _marginLeft, _marginTop, _marginRight, _marginBottom }) box.Increment = (decimal)step;

        LoadSettingsIntoControls();
        WireEvents();

        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(new TextBlock { Text = "Print", FontSize = 21, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = $"{Path.GetFileName(args.ImagePath)}  •  {args.ImageWidthPx} × {args.ImageHeightPx} px  •  {args.DpiX:F0} DPI",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#AAB5C2"))
        });

        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(SectionLabel("Printer"));
        form.Children.Add(_printerCombo);
        var printerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        printerRow.Children.Add(_propertiesButton);
        form.Children.Add(printerRow);
        form.Children.Add(SectionLabel("Paper"));
        form.Children.Add(LabeledRow("Size", _paperCombo));
        _sourceRow = LabeledRow("Source", _sourceCombo);
        form.Children.Add(_sourceRow);
        var orientRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        orientRow.Children.Add(_portraitRadio);
        orientRow.Children.Add(_landscapeRadio);
        form.Children.Add(LabeledRow("Orientation", orientRow));
        form.Children.Add(LabeledRow("Copies", _copiesBox));
        form.Children.Add(LabeledRow("Colour", _colorCombo));
        form.Children.Add(SectionLabel("Layout"));
        form.Children.Add(LabeledRow("Scaling", _scalingCombo));
        form.Children.Add(LabeledRow("Custom scale %", _customScaleBox));
        form.Children.Add(_keepAspectCheck);
        form.Children.Add(_autoRotateCheck);
        form.Children.Add(LabeledRow("Horizontal", _hAlignCombo));
        form.Children.Add(LabeledRow("Vertical", _vAlignCombo));
        form.Children.Add(SectionLabel($"Margins ({PrintUnits.UnitLabel})"));
        form.Children.Add(LabeledRow("Left", _marginLeft));
        form.Children.Add(LabeledRow("Top", _marginTop));
        form.Children.Add(LabeledRow("Right", _marginRight));
        form.Children.Add(LabeledRow("Bottom", _marginBottom));

        // A vertical StackPanel gives a ScrollViewer unbounded height, so the options never
        // scrolled and were cut off. Use a Grid so the ScrollViewer owns the remaining height.
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 10 };
        left.Children.Add(heading);
        var scroll = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        left.Children.Add(scroll);

        var right = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 8 };
        right.Children.Add(_preview);
        Grid.SetRow(_infoText, 1); right.Children.Add(_infoText);
        Grid.SetRow(_warningText, 2); right.Children.Add(_warningText);

        var main = new Grid
        {
            Margin = new Thickness(20),
            ColumnDefinitions = new ColumnDefinitions("380,*"),
            ColumnSpacing = 18,
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 12
        };
        main.Children.Add(left);
        Grid.SetColumn(right, 1); main.Children.Add(right);

        _printButton.Classes.Add("primaryAction");
        _cancelButton.Classes.Add("secondaryAction");
        _propertiesButton.Classes.Add("secondaryAction");
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        footer.Children.Add(_statusText);
        Grid.SetColumn(_cancelButton, 1); footer.Children.Add(_cancelButton);
        Grid.SetColumn(_printButton, 2); footer.Children.Add(_printButton);
        Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 2);
        main.Children.Add(footer);

        Content = main;
        PopupPlacementStore.Track(this, "print");
        Opened += (_, _) => _ = RefreshPrintersAsync();
        Closing += (_, _) => { _lifetime.Cancel(); _printCts?.Cancel(); _grayscalePreview?.Dispose(); _grayscalePreview = null; };
    }

    private static TextBlock SectionLabel(string text) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(0, 6, 0, 0) };

    private static Grid LabeledRow(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), ColumnSpacing = 8 };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1); grid.Children.Add(control);
        return grid;
    }

    private void LoadSettingsIntoControls()
    {
        _scalingCombo.SelectedItem = _settings.ScalingMode;
        _hAlignCombo.SelectedItem = _settings.HAlign;
        _vAlignCombo.SelectedItem = _settings.VAlign;
        _colorCombo.SelectedItem = string.Equals(_settings.ColorMode, "Grayscale", StringComparison.OrdinalIgnoreCase) ? "Grayscale" : "Colour";
        _keepAspectCheck.IsChecked = _settings.KeepAspectRatio;
        _autoRotateCheck.IsChecked = _settings.AutoRotate;
        if (_settings.IsLandscape) _landscapeRadio.IsChecked = true; else _portraitRadio.IsChecked = true;
        _copiesBox.Value = _settings.Copies;
        _customScaleBox.Value = (decimal)_settings.CustomScalePercent;
        _marginLeft.Value = (decimal)PrintUnits.ToDisplay(_settings.MarginLeft);
        _marginTop.Value = (decimal)PrintUnits.ToDisplay(_settings.MarginTop);
        _marginRight.Value = (decimal)PrintUnits.ToDisplay(_settings.MarginRight);
        _marginBottom.Value = (decimal)PrintUnits.ToDisplay(_settings.MarginBottom);
        UpdateScalingDependentControls();
    }

    private void WireEvents()
    {
        _printerCombo.SelectionChanged += (_, _) => { if (!_loadingPrinters) _ = RefreshCapabilitiesAsync(); };
        _paperCombo.SelectionChanged += (_, _) => { if (!_loadingPrinters) _ = RefreshCapabilitiesAsync(); };
        _sourceCombo.SelectionChanged += (_, _) => { if (!_loadingPrinters) _ = RefreshCapabilitiesAsync(); };
        _portraitRadio.Checked += (_, _) => { if (!_loadingPrinters) _ = RefreshCapabilitiesAsync(); };
        _landscapeRadio.Checked += (_, _) => { if (!_loadingPrinters) _ = RefreshCapabilitiesAsync(); };
        _scalingCombo.SelectionChanged += (_, _) => { UpdateScalingDependentControls(); RefreshPreview(); };
        _customScaleBox.ValueChanged += (_, _) => RefreshPreview();
        _keepAspectCheck.IsCheckedChanged += (_, _) => RefreshPreview();
        _autoRotateCheck.IsCheckedChanged += (_, _) => RefreshPreview();
        _hAlignCombo.SelectionChanged += (_, _) => RefreshPreview();
        _vAlignCombo.SelectionChanged += (_, _) => RefreshPreview();
        _colorCombo.SelectionChanged += (_, _) => RefreshPreview();
        _copiesBox.ValueChanged += (_, _) => RefreshPreview();
        foreach (var box in new[] { _marginLeft, _marginTop, _marginRight, _marginBottom })
            box.ValueChanged += (_, _) => RefreshPreview();
        _propertiesButton.Click += (_, _) => ShowDriverProperties();
        _printButton.Click += (_, _) => _ = PrintNowAsync();
        _cancelButton.Click += (_, _) =>
        {
            if (_printing) _printCts?.Cancel();
            else Close(false);
        };
    }

    private void UpdateScalingDependentControls()
    {
        var mode = _scalingCombo.SelectedItem as string ?? "Best fit";
        _customScaleBox.IsEnabled = mode == "Custom scale";
        // Aspect is inherent to Fill (crop), Actual (1:1) and Stretch (fill): only Best fit
        // and Custom scale interpret the checkbox.
        _keepAspectCheck.IsEnabled = mode is "Best fit" or "Custom scale";
    }

    private void ReadControlsIntoSettings()
    {
        _settings.ScalingMode = _scalingCombo.SelectedItem as string ?? "Best fit";
        _settings.HAlign = _hAlignCombo.SelectedItem as string ?? "Centre";
        _settings.VAlign = _vAlignCombo.SelectedItem as string ?? "Centre";
        _settings.KeepAspectRatio = _keepAspectCheck.IsChecked == true;
        _settings.AutoRotate = _autoRotateCheck.IsChecked == true;
        _settings.Orientation = _landscapeRadio.IsChecked == true ? "Landscape" : "Portrait";
        _settings.ColorMode = string.Equals(_colorCombo.SelectedItem as string, "Grayscale", StringComparison.OrdinalIgnoreCase) ? "Grayscale" : "Colour";
        _settings.Copies = (int)(_copiesBox.Value ?? 1);
        _settings.CustomScalePercent = (double)(_customScaleBox.Value ?? 100m);
        _settings.MarginLeft = PrintUnits.FromDisplay((double)(_marginLeft.Value ?? 0m));
        _settings.MarginTop = PrintUnits.FromDisplay((double)(_marginTop.Value ?? 0m));
        _settings.MarginRight = PrintUnits.FromDisplay((double)(_marginRight.Value ?? 0m));
        _settings.MarginBottom = PrintUnits.FromDisplay((double)(_marginBottom.Value ?? 0m));
        _settings = _settings.Normalized();
    }

    private async Task RefreshPrintersAsync()
    {
        SetStatus("Loading printers…");
        SetControlsEnabled(false);
        try
        {
            var (names, def) = await Task.Run(() => (PrintService.GetPrinterNames(), PrintService.GetDefaultPrinterName()));
            if (_lifetime.IsCancellationRequested) return;
            _printerCombo.ItemsSource = names;
            _printerCombo.SelectedItem = names.FirstOrDefault(n => string.Equals(n, def, StringComparison.OrdinalIgnoreCase))
                ?? names.FirstOrDefault();
            if (names.Count == 0)
            {
                SetStatus("No printers installed. Install a printer (for example Microsoft Print to PDF) to print.");
                _loadingPrinters = false;
                SetControlsEnabled(false);
                RefreshPreview();
                return;
            }
            await RefreshCapabilitiesAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Could not list printers: " + ex.Message);
        }
        finally { _loadingPrinters = false; }
    }

    private async Task RefreshCapabilitiesAsync()
    {
        var printer = _printerCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(printer)) { RefreshPreview(); return; }
        SetStatus("Reading printer settings…");
        try
        {
            var landscape = _landscapeRadio.IsChecked == true;
            var paperWant = _paperCombo.SelectedItem as string;
            var sourceWant = _sourceCombo.SelectedItem as string;
            var caps = await Task.Run(() => PrintService.GetCapabilities(printer));
            if (_lifetime.IsCancellationRequested) return;
            _caps = caps;
            _loadingPrinters = true;
            try
            {
                _paperCombo.ItemsSource = caps.Papers.Select(p => p.Name).ToArray();
                _sourceCombo.ItemsSource = caps.Sources.Select(s => s.Name).ToArray();
                _paperCombo.SelectedItem = caps.Papers.Any(p => p.Name == paperWant) ? paperWant : caps.DefaultPaperName;
                _sourceCombo.SelectedItem = caps.Sources.Any(s => s.Name == sourceWant) ? sourceWant : caps.DefaultSourceName;
                // Some drivers (notably Microsoft Print to PDF) expose no paper trays; hide the
                // row instead of showing an empty, unselectable combo.
                if (_sourceRow is not null) _sourceRow.IsVisible = caps.Sources.Count > 0;
                _colorCombo.IsEnabled = caps.SupportsColor;
                if (!caps.SupportsColor) _colorCombo.SelectedItem = "Grayscale";
                else if (_colorCombo.SelectedItem is null) _colorCombo.SelectedItem = string.Equals(_settings.ColorMode, "Grayscale", StringComparison.OrdinalIgnoreCase) ? "Grayscale" : "Colour";
            }
            finally { _loadingPrinters = false; }
            var paper = _paperCombo.SelectedItem as string ?? caps.DefaultPaperName;
            var source = _sourceCombo.SelectedItem as string ?? caps.DefaultSourceName;
            _paperPortrait = await Task.Run(() => PrintService.GetPaperGeometry(printer, paper, source, landscape: false));
            if (_lifetime.IsCancellationRequested) return;
            SetControlsEnabled(true);
            SetStatus("");
            RefreshPreview();
        }
        catch (PrintNoPrinterException ex)
        {
            SetStatus(ex.Message);
            SetControlsEnabled(false);
            RefreshPreview();
        }
        catch (Exception ex)
        {
            SetStatus("Printer error: " + ex.Message);
            RefreshPreview();
        }
    }

    private void RefreshPreview()
    {
        try
        {
            ReadControlsIntoSettings();
            var paper = _paperPortrait;
            if (paper is null)
            {
                _preview.Paper = null;
                _preview.Layout = null;
                _infoText.Text = "Select a printer to preview the page.";
                _warningText.Text = "";
                return;
            }
            var input = new PrintLayoutInput(
                _args.ImageWidthPx, _args.ImageHeightPx, _args.DpiX, _args.DpiY,
                paper, _settings.IsLandscape,
                _settings.MarginLeft, _settings.MarginTop, _settings.MarginRight, _settings.MarginBottom,
                _settings.ScalingModeEnum, _settings.CustomScalePercent, _settings.KeepAspectRatio,
                _settings.AutoRotate, _settings.HAlignEnum, _settings.VAlignEnum);
            var layout = PrintLayoutEngine.Compute(input);
            _preview.Paper = paper;
            _preview.Landscape = _settings.IsLandscape;
            _preview.Layout = layout;
            ApplyPreviewBitmap();
            _preview.MarginLeft = _settings.MarginLeft;
            _preview.MarginTop = _settings.MarginTop;
            _preview.MarginRight = _settings.MarginRight;
            _preview.MarginBottom = _settings.MarginBottom;

            var sheetW = _settings.IsLandscape ? paper.PaperHeightHundredths : paper.PaperWidthHundredths;
            var sheetH = _settings.IsLandscape ? paper.PaperWidthHundredths : paper.PaperHeightHundredths;
            _infoText.Text = $"{_printerCombo.SelectedItem}  •  {_paperCombo.SelectedItem}  •  " +
                $"{PrintUnits.Format(sheetW)} × {PrintUnits.Format(sheetH)}  •  " +
                $"image {PrintUnits.Format(layout.DestWidth)} × {PrintUnits.Format(layout.DestHeight)}" +
                (layout.Rotated ? " (rotated)" : "") +
                $"  •  {_settings.Copies} cop{(_settings.Copies == 1 ? "y" : "ies")}";
            _warningText.Text = layout.Warning;
        }
        catch (Exception ex)
        {
            _warningText.Text = "Preview error: " + ex.Message;
        }
    }

    /// <summary>
    /// Selects the bitmap the preview draws: the colour source, or a cached luminance-grayscale
    /// copy when Grayscale is chosen. Conversion happens off the UI thread.
    /// </summary>
    private void ApplyPreviewBitmap()
    {
        var grayscale = string.Equals(_colorCombo.SelectedItem as string, "Grayscale", StringComparison.OrdinalIgnoreCase);
        if (!grayscale)
        {
            _preview.PreviewBitmap = _args.PreviewBitmap;
            return;
        }
        if (_grayscalePreview is not null && ReferenceEquals(_grayscaleSource, _args.PreviewBitmap))
        {
            _preview.PreviewBitmap = _grayscalePreview;
            return;
        }
        _preview.PreviewBitmap = _args.PreviewBitmap;
        var source = _args.PreviewBitmap;
        _ = Task.Run(() =>
        {
            var gray = PrintPreviewControl.CreateGrayscale(source);
            if (gray is null) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (string.Equals(_colorCombo.SelectedItem as string, "Grayscale", StringComparison.OrdinalIgnoreCase))
                {
                    _grayscalePreview?.Dispose();
                    _grayscalePreview = gray;
                    _grayscaleSource = source;
                    _preview.PreviewBitmap = gray;
                }
                else gray.Dispose();
            });
        });
    }

    private void ShowDriverProperties()
    {
        try
        {
            var printer = _printerCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(printer)) return;
            var handle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (!PrintDriverProperties.Show(handle, printer, out var landscape)) return;
            // The native sheet may have switched orientation (or paper). Reflect it in Glide's own
            // radio buttons first so the geometry re-read below uses the new orientation, then
            // refresh paper/source/geometry and the live preview.
            _loadingPrinters = true;
            try
            {
                _landscapeRadio.IsChecked = landscape;
                _portraitRadio.IsChecked = !landscape;
            }
            finally { _loadingPrinters = false; }
            _ = RefreshCapabilitiesAsync();
        }
        catch { }
    }

    private async Task PrintNowAsync()
    {
        if (_printing) return;
        var printer = _printerCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(printer))
        {
            SetStatus("Select a printer first.");
            return;
        }
        ReadControlsIntoSettings();
        PrintSettingsStore.Save(_settings);
        _printing = true;
        _printCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SetControlsEnabled(false);
        try
        {
            SetStatus("Loading full-resolution image…");
            var (bitmap, w, h) = await _args.FullResolver(_printCts.Token);
            using (bitmap)
            {
                if (_printCts.IsCancellationRequested) return;
                SetStatus("Encoding print image…");
                var png = await Task.Run(() =>
                {
                    using var ms = new MemoryStream();
                    bitmap.Save(ms);
                    return ms.ToArray();
                }, _printCts.Token);
                if (_printCts.IsCancellationRequested) return;

                var paper = _paperCombo.SelectedItem as string ?? "";
                var source = _sourceCombo.SelectedItem as string ?? "";
                var request = new PrintJobRequest(
                    _args.ImagePath, w, h, _args.DpiX, _args.DpiY,
                    printer, paper, source, _settings.IsLandscape,
                    _settings.MarginLeft, _settings.MarginTop, _settings.MarginRight, _settings.MarginBottom,
                    _settings.ScalingModeEnum, _settings.CustomScalePercent, _settings.KeepAspectRatio,
                    _settings.AutoRotate, _settings.HAlignEnum, _settings.VAlignEnum,
                    (short)_settings.Copies,
                    !string.Equals(_colorCombo.SelectedItem as string, "Grayscale", StringComparison.OrdinalIgnoreCase),
                    null);
                var progress = new Progress<string>(msg => SetStatus(msg));
                await PrintService.PrintAsync(() => new MemoryStream(png, writable: false), request, progress, _printCts.Token);
            }
            Close(true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Print cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus("Print failed: " + ex.Message);
        }
        finally
        {
            _printing = false;
            _printCts?.Dispose();
            _printCts = null;
            if (IsVisible) SetControlsEnabled(true);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        var hasPrinter = _printerCombo.Items?.Cast<object>().Any() == true && !string.IsNullOrWhiteSpace(_printerCombo.SelectedItem as string);
        var on = enabled && hasPrinter && !_printing;
        foreach (var c in new Control[] { _printerCombo, _paperCombo, _sourceCombo, _portraitRadio, _landscapeRadio,
                     _copiesBox, _scalingCombo, _customScaleBox, _keepAspectCheck, _autoRotateCheck,
                     _hAlignCombo, _vAlignCombo, _colorCombo, _marginLeft, _marginTop, _marginRight, _marginBottom,
                     _propertiesButton })
            c.IsEnabled = on || c == _printerCombo || c == _cancelButton;
        _printerCombo.IsEnabled = !_printing;
        _cancelButton.IsEnabled = true;
        _cancelButton.Content = _printing ? "Cancel printing" : "Cancel";
        _printButton.IsEnabled = on;
    }

    private void SetStatus(string message)
    {
        if (Dispatcher.UIThread.CheckAccess()) _statusText.Text = message;
        else Dispatcher.UIThread.Post(() => _statusText.Text = message);
    }
}
