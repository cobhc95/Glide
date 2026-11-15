using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Glide.App.Services;
using Glide.Core.Windows;

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
///
/// Responsiveness (5.0.x): printer-driver queries are the slow part (hundreds of ms each, seconds for network or
/// Remote Desktop redirected printers). They run off the UI thread through <see cref="PrintQueryCache"/>, once per
/// printer (capabilities) and once per printer + paper + source (geometry), never on orientation, scaling, margin,
/// alignment, colour or copies changes: those recompute the page geometry synchronously (pure math) and redraw at
/// once. Paper/printer changes draw a provisional sheet immediately, show "Updating preview…" and refine when the
/// driver answers; the latest selection wins. The preview draws a screen-sized proxy of the image. Printing itself
/// is unchanged: it re-reads the live driver and uses the full-resolution image.
/// </summary>
public sealed class PrintWindow : Window
{
    /// <summary>Wait after a printer/paper/source change before asking the driver, so scrolling through a list asks once.</summary>
    internal const int SelectionDebounceMs = 120;

    private readonly PrintWindowArgs _args;
    private readonly PrintQueryCache _cache;
    private PrintDocumentSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _printCts;
    private bool _printing;
    private bool _loadingPrinters = true;
    private bool _printersLoaded;
    private bool _closed;
    private readonly Stopwatch _openClock = Stopwatch.StartNew();
    private bool _firstPreviewTraced;

    // Latest-wins token for printer/paper/source driver reads: a stale answer is dropped.
    private int _selectionVersion;
    private PrintPrinterCapabilities? _caps;
    private PrintPaperGeometry? _paperPortrait;
    private bool _geometryProvisional;
    private bool _readingDriver;

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
    private Bitmap? _proxy;
    private Bitmap? _grayscalePreview;
    private Bitmap? _grayscaleSource;

    public PrintWindow(PrintWindowArgs args) : this(args, null) { }

    /// <summary>Tests pass a <see cref="PrintQueryCache"/> over fake printers; the app uses the shared real one.</summary>
    internal PrintWindow(PrintWindowArgs args, PrintQueryCache? cache)
    {
        _args = args;
        _cache = cache ?? PrintQueryCache.Shared;
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
        // Remembers size, position and maximized state between openings (validated against the current monitors).
        WindowPlacementService.Track(this, "print", WindowPlacementMode.SizeAndPosition);

        // Until the driver answers: a clear "reading" state in the preview, layout options usable, paper/Print not yet.
        _preview.PlaceholderText = "Reading printer settings…";
        SetStatus("Reading printer settings…");
        UpdateControlStates();
        _ = PrepareProxyAsync();
        Opened += (_, _) => _ = LoadPrintersAsync();
        Closing += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _printCts?.Cancel();
            _preview.PreviewBitmap = null;
            _grayscalePreview?.Dispose(); _grayscalePreview = null;
            _proxy?.Dispose(); _proxy = null;
        };
    }

    /// <summary>Tests/diagnostics: receives (event, milliseconds since the window was created).</summary>
    internal Action<string, double>? Trace { get; set; }

    private void TraceEvent(string name)
    {
        var ms = _openClock.Elapsed.TotalMilliseconds;
        Trace?.Invoke(name, ms);
        if (string.Equals(Environment.GetEnvironmentVariable("GLIDE_PRINT_DEBUG"), "1", StringComparison.Ordinal))
            Debug.WriteLine($"print-window {name} {ms:F1} ms");
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
        // Only printer / paper / source need the driver; everything else is pure geometry and redraws at once.
        _printerCombo.SelectionChanged += (_, _) => { if (!_loadingPrinters) _ = SelectPrinterAsync(debounce: true); };
        _paperCombo.SelectionChanged += (_, _) => { if (!_loadingPrinters) _ = SelectPaperAsync(debounce: true); };
        _sourceCombo.SelectionChanged += (_, _) => { if (!_loadingPrinters) _ = SelectPaperAsync(debounce: true); };
        // Both radios, checked and unchecked: the group unchecks the other radio only after the clicked one reports, so
        // the last of the two events always sees the final orientation.
        _portraitRadio.IsCheckedChanged += (_, _) => RefreshPreview();
        _landscapeRadio.IsCheckedChanged += (_, _) => RefreshPreview();
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
        _customScaleBox.IsEnabled = !_printing && mode == "Custom scale";
        // Aspect is inherent to Fill (crop), Actual (1:1) and Stretch (fill): only Best fit
        // and Custom scale interpret the checkbox.
        _keepAspectCheck.IsEnabled = !_printing && mode is "Best fit" or "Custom scale";
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

    private async Task LoadPrintersAsync()
    {
        TraceEvent("opened");
        try
        {
            var (names, def) = await _cache.GetPrintersAsync();
            if (_closed) return;
            TraceEvent("printers");
            _loadingPrinters = true;
            try
            {
                _printerCombo.ItemsSource = names;
                _printerCombo.SelectedItem = names.FirstOrDefault(n => string.Equals(n, def, StringComparison.OrdinalIgnoreCase))
                    ?? names.FirstOrDefault();
            }
            finally { _loadingPrinters = false; }
            _printersLoaded = true;
            if (names.Count == 0)
            {
                SetStatus("No printers installed. Install a printer (for example Microsoft Print to PDF) to print.");
                _preview.PlaceholderText = "No printer";
                UpdateControlStates();
                RefreshPreview();
                return;
            }
            await SelectPrinterAsync(debounce: false);
        }
        catch (Exception ex)
        {
            _printersLoaded = true;
            _preview.PlaceholderText = "No printer";
            SetStatus("Could not list printers: " + ex.Message);
            UpdateControlStates();
        }
    }

    /// <summary>
    /// The printer changed (or the dialog opened): capabilities from the cache when known, else read once off the UI
    /// thread, then the paper geometry. The preview keeps drawing (with an "Updating preview…" badge) meanwhile.
    /// </summary>
    private async Task SelectPrinterAsync(bool debounce)
    {
        var version = ++_selectionVersion;
        var printer = _printerCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(printer))
        {
            _caps = null;
            UpdateControlStates();
            RefreshPreview();
            return;
        }
        try
        {
            if (_cache.TryGetCachedCapabilities(printer, out var cached))
            {
                ApplyCapabilities(cached);
            }
            else
            {
                _caps = null;
                SetReading(true, "Reading printer settings…");
                if (debounce)
                {
                    await Task.Delay(SelectionDebounceMs);
                    if (version != _selectionVersion || _closed) return;
                }
                var caps = await _cache.GetCapabilitiesAsync(printer);
                if (version != _selectionVersion || _closed) return;
                TraceEvent("capabilities");
                ApplyCapabilities(caps);
            }
            await RefreshGeometryAsync(version, debounce: false);
        }
        catch (PrintNoPrinterException ex)
        {
            if (version != _selectionVersion || _closed) return;
            FailDriverRead(ex.Message);
        }
        catch (Exception ex)
        {
            if (version != _selectionVersion || _closed) return;
            FailDriverRead("Printer error: " + ex.Message);
        }
    }

    /// <summary>Paper size or source changed by the user: no capability re-read, just this paper's geometry.</summary>
    private async Task SelectPaperAsync(bool debounce)
    {
        var version = ++_selectionVersion;
        try
        {
            await RefreshGeometryAsync(version, debounce);
        }
        catch (Exception ex)
        {
            if (version != _selectionVersion || _closed) return;
            FailDriverRead("Printer error: " + ex.Message);
        }
    }

    private void FailDriverRead(string message)
    {
        _readingDriver = false;
        _preview.BusyText = null;
        if (_paperPortrait is null) _preview.PlaceholderText = "No printer";
        SetStatus(message);
        UpdateControlStates();
        RefreshPreview();
    }

    private void ApplyCapabilities(PrintPrinterCapabilities caps)
    {
        _caps = caps;
        var paperWant = _paperCombo.SelectedItem as string;
        var sourceWant = _sourceCombo.SelectedItem as string;
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
            if (!caps.SupportsColor) _colorCombo.SelectedItem = "Grayscale";
            else if (_colorCombo.SelectedItem is null) _colorCombo.SelectedItem = string.Equals(_settings.ColorMode, "Grayscale", StringComparison.OrdinalIgnoreCase) ? "Grayscale" : "Colour";
        }
        finally { _loadingPrinters = false; }
        UpdateControlStates();
    }

    /// <summary>
    /// Brings the preview to the selected paper: cached geometry instantly; otherwise a provisional sheet of the right
    /// size at once, then the driver's real printable rectangle (latest selection wins).
    /// </summary>
    private async Task RefreshGeometryAsync(int version, bool debounce)
    {
        var printer = _printerCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(printer) || _caps is null) return;
        var paper = _paperCombo.SelectedItem as string ?? _caps.DefaultPaperName;
        var source = _sourceCombo.SelectedItem as string ?? _caps.DefaultSourceName;
        if (_cache.TryGetCachedGeometry(printer, paper, source, out var cached))
        {
            ApplyGeometry(cached);
            return;
        }
        if (_caps.Papers.FirstOrDefault(p => p.Name == paper) is { } option)
        {
            _paperPortrait = PrintPreviewGeometry.Provisional(option, _paperPortrait);
            _geometryProvisional = true;
        }
        SetReading(true, _paperPortrait is null ? "Reading printer settings…" : "Updating preview…");
        RefreshPreview();
        if (debounce)
        {
            await Task.Delay(SelectionDebounceMs);
            if (version != _selectionVersion || _closed) return;
        }
        var geometry = await _cache.GetGeometryAsync(printer, paper, source);
        if (version != _selectionVersion || _closed) return;
        TraceEvent("geometry");
        ApplyGeometry(geometry);
    }

    private void ApplyGeometry(PrintPaperGeometry geometry)
    {
        _paperPortrait = geometry;
        _geometryProvisional = false;
        SetReading(false, null);
        SetStatus("");
        UpdateControlStates();
        RefreshPreview();
    }

    private void SetReading(bool reading, string? text)
    {
        _readingDriver = reading;
        if (reading)
        {
            if (_paperPortrait is null) _preview.PlaceholderText = text ?? "Reading printer settings…";
            _preview.BusyText = _paperPortrait is null ? null : (text ?? "Updating preview…");
            SetStatus(text ?? "Reading printer settings…");
        }
        else
        {
            _preview.BusyText = null;
        }
        UpdateControlStates();
    }

    /// <summary>
    /// Recomputes the page with the shared engine and pushes it to the preview. Synchronous and cheap (no driver, no
    /// pixels), so every option change shows immediately.
    /// </summary>
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
                _infoText.Text = _readingDriver || !_printersLoaded ? "" : "Select a printer to preview the page.";
                _warningText.Text = "";
                return;
            }
            var layout = PrintLayoutEngine.Compute(BuildLayoutInput(paper));
            _preview.Paper = paper;
            _preview.Landscape = _settings.IsLandscape;
            _preview.Layout = layout;
            ApplyPreviewBitmap();
            _preview.MarginLeft = _settings.MarginLeft;
            _preview.MarginTop = _settings.MarginTop;
            _preview.MarginRight = _settings.MarginRight;
            _preview.MarginBottom = _settings.MarginBottom;
            if (!_firstPreviewTraced)
            {
                _firstPreviewTraced = true;
                TraceEvent("first-preview");
            }

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

    /// <summary>The layout input for <paramref name="paperPortrait"/> and the current options (the same values the spool path uses).</summary>
    internal PrintLayoutInput BuildLayoutInput(PrintPaperGeometry paperPortrait) => new(
        _args.ImageWidthPx, _args.ImageHeightPx, _args.DpiX, _args.DpiY,
        paperPortrait, _settings.IsLandscape,
        _settings.MarginLeft, _settings.MarginTop, _settings.MarginRight, _settings.MarginBottom,
        _settings.ScalingModeEnum, _settings.CustomScalePercent, _settings.KeepAspectRatio,
        _settings.AutoRotate, _settings.HAlignEnum, _settings.VAlignEnum);

    /// <summary>Makes the screen-sized preview proxy off the UI thread (the original is drawn until it is ready).</summary>
    private async Task PrepareProxyAsync()
    {
        var source = _args.PreviewBitmap;
        Bitmap? proxy = null;
        try
        {
            var sw = Stopwatch.StartNew();
            proxy = await Task.Run(() => PrintPreviewControl.CreateProxy(source));
            if (proxy is null) return;
            if (_closed) { proxy.Dispose(); return; }
            Trace?.Invoke("proxy-ready", sw.Elapsed.TotalMilliseconds);
            _proxy = proxy;
            _grayscalePreview?.Dispose();
            _grayscalePreview = null;
            _grayscaleSource = null;
            if (_preview.Paper is not null) ApplyPreviewBitmap();
        }
        catch
        {
            proxy?.Dispose();
        }
    }

    /// <summary>The bitmap the preview draws: the proxy when there is one, else the caller's preview bitmap.</summary>
    internal Bitmap PreviewSource => _proxy ?? _args.PreviewBitmap;

    /// <summary>
    /// Selects the bitmap the preview draws: the colour source, or a cached luminance-grayscale
    /// copy when Grayscale is chosen. Conversion happens off the UI thread.
    /// </summary>
    private void ApplyPreviewBitmap()
    {
        var source = PreviewSource;
        var grayscale = string.Equals(_colorCombo.SelectedItem as string, "Grayscale", StringComparison.OrdinalIgnoreCase);
        if (!grayscale)
        {
            _preview.PreviewBitmap = source;
            return;
        }
        if (_grayscalePreview is not null && ReferenceEquals(_grayscaleSource, source))
        {
            _preview.PreviewBitmap = _grayscalePreview;
            return;
        }
        _preview.PreviewBitmap = source;
        _ = Task.Run(() =>
        {
            var gray = PrintPreviewControl.CreateGrayscale(source);
            if (gray is null) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_closed && ReferenceEquals(PreviewSource, source) &&
                    string.Equals(_colorCombo.SelectedItem as string, "Grayscale", StringComparison.OrdinalIgnoreCase))
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
            // radio buttons, forget what was cached for this printer, and re-read it.
            _landscapeRadio.IsChecked = landscape;
            _portraitRadio.IsChecked = !landscape;
            _cache.Invalidate(printer);
            _ = SelectPrinterAsync(debounce: false);
        }
        catch { /* printer defaults are optional; the dialog keeps its current choices */ }
    }

    /// <summary>
    /// The spool request for the current options. Exactly what Print sends (factored out so a test can prove the preview
    /// geometry and the printed geometry come from the same input).
    /// </summary>
    internal PrintJobRequest BuildJobRequest(string printer, int widthPx, int heightPx)
    {
        var paper = _paperCombo.SelectedItem as string ?? "";
        var source = _sourceCombo.SelectedItem as string ?? "";
        return new PrintJobRequest(
            _args.ImagePath, widthPx, heightPx, _args.DpiX, _args.DpiY,
            printer, paper, source, _settings.IsLandscape,
            _settings.MarginLeft, _settings.MarginTop, _settings.MarginRight, _settings.MarginBottom,
            _settings.ScalingModeEnum, _settings.CustomScalePercent, _settings.KeepAspectRatio,
            _settings.AutoRotate, _settings.HAlignEnum, _settings.VAlignEnum,
            (short)_settings.Copies,
            !string.Equals(_colorCombo.SelectedItem as string, "Grayscale", StringComparison.OrdinalIgnoreCase),
            null);
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
        UpdateControlStates();
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

                var request = BuildJobRequest(printer, w, h);
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
            if (IsVisible) UpdateControlStates();
        }
    }

    /// <summary>
    /// Enables what can be used now. Layout options (orientation, scaling, margins, alignment, colour, copies) never
    /// wait for the driver; paper/source/Properties need the printer's capabilities; Print also needs the real
    /// (non-provisional) geometry; everything but Cancel is locked while printing.
    /// </summary>
    private void UpdateControlStates()
    {
        var hasPrinter = _printerCombo.Items?.Cast<object>().Any() == true && !string.IsNullOrWhiteSpace(_printerCombo.SelectedItem as string);
        var capsReady = hasPrinter && _caps is not null;
        var layoutOn = !_printing;
        foreach (var c in new Control[] { _portraitRadio, _landscapeRadio, _copiesBox, _scalingCombo, _autoRotateCheck,
                     _hAlignCombo, _vAlignCombo, _marginLeft, _marginTop, _marginRight, _marginBottom })
            c.IsEnabled = layoutOn;
        _colorCombo.IsEnabled = layoutOn && (_caps?.SupportsColor ?? true);
        UpdateScalingDependentControls();
        _paperCombo.IsEnabled = capsReady && !_printing;
        _sourceCombo.IsEnabled = capsReady && !_printing;
        _propertiesButton.IsEnabled = capsReady && !_printing;
        _printerCombo.IsEnabled = !_printing && _printersLoaded;
        _cancelButton.IsEnabled = true;
        _cancelButton.Content = _printing ? "Cancel printing" : "Cancel";
        _printButton.IsEnabled = capsReady && !_printing && _paperPortrait is not null && !_geometryProvisional && !_readingDriver;
    }

    private void SetStatus(string message)
    {
        if (Dispatcher.UIThread.CheckAccess()) _statusText.Text = message;
        else Dispatcher.UIThread.Post(() => _statusText.Text = message);
    }
}
