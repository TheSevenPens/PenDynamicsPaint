using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PenDynamicsPaint.Drawing.MyPaint;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Paint;
using WinPenKit;
using WinPenKit.Avalonia;

namespace PenDynamicsPaint;

/// <summary>
/// The whole application: a document, a viewport onto it, and a pen.
/// </summary>
/// <remarks>
/// <para>
/// Split out of PenDynamicsLab, which exists to establish how the pen pipeline behaves and keeps
/// its canvases deliberately primitive for that reason. A painting application wants the opposite
/// -- a document with a size of its own, layers, per-brush settings -- and growing that inside a
/// diagnostic tool would have spent what made the tool useful.
/// </para>
/// <para>
/// What came across: the viewport mapping, the document session, the brush engine layer and the
/// stroke model. What did not: the raw-versus-processed comparison, the global pressure pipeline
/// and its settings pane, the recorder and the self tests. Those are the Lab's job and are better
/// done there. TheSevenPens/PenDynamicsLab#72 records the reasoning in full.
/// </para>
/// <para>
/// Dynamics are absent on purpose rather than missing. Pressure goes straight from the pen to the
/// brush. In a paint application a pressure curve belongs to a brush rather than to the
/// application, so inheriting the Lab's global pipeline would have meant importing the model this
/// split exists to leave behind.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    private IPenSession? _penSession;
    private IReadOnlyList<InputApi> _apis = [];
    private PaintSession _paint = null!;
    private bool _fitted;

    /// <summary>The brushes on offer. Edited in place; nothing is written to disk.</summary>
    private readonly List<BrushSettings> _brushes = [.. BrushLibrary.Defaults];

    private int _brushIndex;

    /// <summary>Guards the panel against reacting to its own repopulation.</summary>
    /// <remarks>
    /// Showing a brush sets every control, each of which raises the event that would edit it.
    /// Without this, selecting a brush would overwrite it with the one previously shown.
    /// </remarks>
    private bool _syncingBrush;

    /// <summary>The brush strokes are started with.</summary>
    private BrushSettings Brush => _brushes[_brushIndex];

    public MainWindow()
    {
        InitializeComponent();

        // 1500 x 1000 is a working area rather than a statement about what a document is. It is
        // the first thing a document-properties dialog takes over.
        _paint = new PaintSession(1500, 1000);
        PaintView.Session = _paint;
        PaintView.UndoRequested += (_, _) => { _paint.Undo(); PaintView.Invalidate(); };
        PaintView.ClearRequested += (_, _) => { _paint.Clear(); PaintView.Invalidate(); };
        PaintView.ClearLayerRequested += (_, _) => { _paint.ClearActiveLayer(); PaintView.Invalidate(); };

        // Fitted on the first real layout pass. Fitting to a zero-sized viewport would leave the
        // document off-screen at whatever the zoom clamp allowed.
        PaintView.Host.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name != "Bounds" || _fitted || PaintView.Host.Bounds.Width <= 0) return;
            _fitted = true;
            PaintView.Fit();
        };

        // The plot is drawn into a Canvas in its own coordinates, so it cannot be drawn until the
        // Canvas has some. The first pass is where that happens.
        CurvePlot.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Bounds") DrawCurve(Brush.Curve);
        };

        WireBrushPanel();

        LayerList.SelectionChanged += (_, _) =>
        {
            if (_syncingLayers || LayerList.SelectedIndex < 0) return;
            _paint.SetActiveLayer(ToStackIndex(LayerList.SelectedIndex));
            ShowSelectedLayer();
        };

        LayerOpacitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name != "Value") return;
            LayerOpacityLabel.Text = $"{LayerOpacitySlider.Value:F0}%";
            if (_syncingLayers) return;

            _paint.SetLayerOpacity(_paint.ActiveLayerIndex, LayerOpacitySlider.Value / 100.0);
            PaintView.Invalidate();
        };

        LayerNameBox.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name != "Text" || _syncingLayers) return;
            if (!_paint.RenameLayer(_paint.ActiveLayerIndex, LayerNameBox.Text ?? "")) return;
            RefreshLayerRow(_paint.ActiveLayerIndex);
        };

        RebuildLayerList();

        CompositingCombo.ItemsSource = new[] { "Wash", "Direct" };
        CompositingCombo.SelectedIndex = 0;
        CompositingCombo.SelectionChanged += (_, _) =>
            _paint.Compositing = CompositingCombo.SelectedIndex == 1
                ? StrokeCompositing.Direct
                : StrokeCompositing.Wash;

        // Tunnelling, not bubbling. A ComboBox swallows Space to open itself and a ListBox
        // swallows Delete, so by the time a bubbling handler saw either, the shortcut would
        // already have been eaten by whatever the user last clicked on.
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, Window_KeyUp, RoutingStrategies.Tunnel);

        _renderTimer.Tick += RenderTimer_Tick;

        Opened += (_, _) =>
        {
            PopulateApis();
            StartSession();
        };

        Closing += (_, _) =>
        {
            _renderTimer.Stop();
            _penSession?.Stop();
            _penSession?.Dispose();
            _paint.Dispose();
        };
    }

    // -- Layers ---------------------------------------------------

    /// <summary>Guards the panel against reacting to its own repopulation.</summary>
    /// <remarks>
    /// Rebuilding the list sets a selection, a checkbox and a slider, each of which raises the
    /// event that would change the document. Without this, showing the stack would edit it.
    /// </remarks>
    private bool _syncingLayers;

    /// <summary>The rows, top of the stack first, so index arithmetic stays in one place.</summary>
    private readonly List<LayerRow> _layerRows = [];

    /// <summary>One row of the panel: a visibility box and the layer's name.</summary>
    private sealed record LayerRow(Control Root, CheckBox Visible, TextBlock Name);

    /// <summary>Panel row to stack index. The panel shows the stack upside down.</summary>
    private int ToStackIndex(int row) => _paint.Layers.Count - 1 - row;

    /// <inheritdoc cref="ToStackIndex" />
    private int ToRow(int stackIndex) => _paint.Layers.Count - 1 - stackIndex;

    private void RebuildLayerList()
    {
        _syncingLayers = true;
        _layerRows.Clear();

        // Top of the stack first, which is the last element of Layers.
        for (int stackIndex = _paint.Layers.Count - 1; stackIndex >= 0; stackIndex--)
        {
            int index = stackIndex;
            var layer = _paint.Layers[index];

            var visible = new CheckBox
            {
                IsChecked = layer.IsVisible,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 0,
            };
            visible.IsCheckedChanged += (_, _) =>
            {
                if (_syncingLayers) return;
                _paint.SetLayerVisible(index, visible.IsChecked == true);
                PaintView.Invalidate();
            };

            var name = new TextBlock
            {
                Text = layer.Name,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };

            var root = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { visible, name },
            };

            _layerRows.Add(new LayerRow(root, visible, name));
        }

        LayerList.ItemsSource = _layerRows.Select(r => r.Root).ToList();
        LayerList.SelectedIndex = ToRow(_paint.ActiveLayerIndex);
        _syncingLayers = false;

        ShowSelectedLayer();
    }

    /// <summary>Put one row's name back in step, without rebuilding and losing the selection.</summary>
    private void RefreshLayerRow(int stackIndex)
    {
        int row = ToRow(stackIndex);
        if (row >= 0 && row < _layerRows.Count) _layerRows[row].Name.Text = _paint.Layers[stackIndex].Name;
    }

    /// <summary>Show the active layer's name and opacity, and enable what applies to it.</summary>
    private void ShowSelectedLayer()
    {
        _syncingLayers = true;

        var layer = _paint.ActiveLayer;
        LayerNameBox.Text = layer.Name;
        LayerOpacitySlider.Value = layer.Opacity * 100;
        LayerOpacityLabel.Text = $"{layer.Opacity * 100:F0}%";

        // Disabled rather than absent, so the panel does not change shape as the selection moves.
        DeleteLayerButton.IsEnabled = _paint.Layers.Count > 1;
        MergeLayerButton.IsEnabled = _paint.ActiveLayerIndex > 0;
        RaiseLayerButton.IsEnabled = _paint.ActiveLayerIndex < _paint.Layers.Count - 1;
        LowerLayerButton.IsEnabled = _paint.ActiveLayerIndex > 0;

        _syncingLayers = false;
    }

    private void AddLayer_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _paint.AddLayer();
        RebuildLayerList();
        PaintView.Invalidate();
    }

    private void DeleteLayer_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_paint.RemoveLayer(_paint.ActiveLayerIndex)) return;
        RebuildLayerList();
        PaintView.Invalidate();
    }

    private void MergeLayer_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_paint.MergeDown(_paint.ActiveLayerIndex)) return;
        RebuildLayerList();
        PaintView.Invalidate();
    }

    private void RaiseLayer_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => MoveActiveLayer(+1);

    private void LowerLayer_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => MoveActiveLayer(-1);

    private void MoveActiveLayer(int by)
    {
        int from = _paint.ActiveLayerIndex;
        if (!_paint.MoveLayer(from, from + by)) return;
        RebuildLayerList();
        PaintView.Invalidate();
    }

    /// <summary>
    /// Load a <c>.myb</c> file over the selected brush.
    /// </summary>
    /// <remarks>
    /// Over the selected brush rather than into a new one, so the smoothing and interpolation
    /// already set on it are kept: those belong to how the pen is read, and a brush file has
    /// nothing to say about them.
    /// </remarks>
    private async void LoadMyPaintBrush_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a MyPaint brush",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("MyPaint brush") { Patterns = ["*.myb"] },
            ],
        });

        if (files.Count == 0) return;

        var file = files[0];
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);

            string name = Path.GetFileNameWithoutExtension(file.Name);
            var brush = MyPaintBrush.Parse(await reader.ReadToEndAsync(), name);

            EditBrush(b => b with
            {
                Name = name,
                Engine = BrushEngineKind.MyPaint,
                MyPaint = brush,
            });

            BrushCombo.ItemsSource = _brushes.Select(b => b.Name).ToList();
            ShowBrush();

            StatusLabel.Text = brush.Ignored.Count == 0
                ? $"{name}: loaded"
                : $"{name}: loaded, {brush.Ignored.Count} setting(s) not acted on";
        }
        catch (Exception error) when (error is FormatException or IOException)
        {
            // Said out loud rather than swallowed: a brush that will not load is worth knowing
            // about, and the two reasons -- the old text format, and a file that cannot be read --
            // are both things the user can do something about.
            StatusLabel.Text = $"{file.Name}: {error.Message}";
        }
    }

    // -- The document on disk ------------------------------------

    /// <summary>Where the document came from, so Save can go back there without asking.</summary>
    /// <remarks>
    /// Cleared by nothing: exporting a PNG does not change it, because an export is not the
    /// document. Saving somewhere new moves it, which is what Save then follows.
    /// </remarks>
    private IStorageFile? _documentFile;

    private static readonly FilePickerFileType OpenRasterType =
        new("OpenRaster document") { Patterns = ["*.ora"], MimeTypes = ["image/openraster"] };

    private static readonly FilePickerFileType PngType =
        new("PNG image") { Patterns = ["*.png"], MimeTypes = ["image/png"] };

    private async void Open_Click(object? sender, RoutedEventArgs e) => await OpenDocument();

    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveDocument();

    private async void Export_Click(object? sender, RoutedEventArgs e) => await ExportImage();

    private async Task OpenDocument()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a document",
            AllowMultiple = false,
            FileTypeFilter = [OpenRasterType],
        });

        if (files.Count == 0) return;

        var file = files[0];
        try
        {
            OpenRaster.Loaded loaded;
            await using (var stream = await file.OpenReadAsync())
            {
                // Read into memory first. A zip is read by seeking to its directory at the end,
                // and a picker's stream need not be seekable -- on some backends it is a forward
                // pipe, where this fails on the file rather than on the format.
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                memory.Position = 0;

                loaded = OpenRaster.Load(memory);
            }

            AdoptDocument(loaded.Session, file);

            StatusLabel.Text = loaded.Ignored.Count == 0
                ? $"{file.Name}: opened"
                : $"{file.Name}: opened, {string.Join(", ", loaded.Ignored)} not honoured";
        }
        catch (Exception error) when (error is FormatException or IOException
                                        or InvalidDataException)
        {
            StatusLabel.Text = $"{file.Name}: {error.Message}";
        }
    }

    /// <summary>Put a freshly read document in front of the pen, and let the old one go.</summary>
    /// <remarks>
    /// The session is replaced rather than refilled, so everything holding the old one has to be
    /// pointed at the new one: the view, the layer list, and the compositing choice, which belongs
    /// to the application rather than to the file and so is re-applied rather than carried over.
    /// </remarks>
    private void AdoptDocument(PaintSession session, IStorageFile? from)
    {
        var old = _paint;

        _paint = session;
        _paint.Compositing = old.Compositing;

        PaintView.Session = _paint;
        _documentFile = from;

        RebuildLayerList();
        ShowSelectedLayer();
        PaintView.Invalidate();

        old.Dispose();
    }

    private async Task SaveDocument()
    {
        var file = _documentFile ?? await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the document",
            DefaultExtension = "ora",
            SuggestedFileName = "untitled.ora",
            FileTypeChoices = [OpenRasterType],
        });

        if (file is null) return;

        try
        {
            // Truncated explicitly. A picker hands back a stream positioned at the start but does
            // not shorten the file, so saving a smaller document over a larger one would leave the
            // tail of the old one behind -- and a zip read from the end would find that tail.
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);

            OpenRaster.Save(_paint, stream);

            _documentFile = file;
            StatusLabel.Text = $"{file.Name}: saved";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusLabel.Text = $"{file.Name}: {error.Message}";
        }
    }

    private async Task ExportImage()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export a flattened image",
            DefaultExtension = "png",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_documentFile?.Name ?? "untitled")
                                + ".png",
            FileTypeChoices = [PngType],
        });

        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);

            OpenRaster.ExportPng(_paint, stream);

            // Deliberately not _documentFile: a PNG is a picture of the document, not the document,
            // and letting Save follow it here would quietly throw the layers away on the next one.
            StatusLabel.Text = $"{file.Name}: exported";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusLabel.Text = $"{file.Name}: {error.Message}";
        }
    }

    // -- Keyboard -------------------------------------------------

    /// <summary>
    /// True while the caret is in a text box, where these keys mean what they always mean.
    /// </summary>
    /// <remarks>
    /// Without this, renaming a layer would clear it on the first keystroke that needed
    /// correcting, and Ctrl+Z in the name box would undo a brush stroke rather than the typing.
    /// Both are the sort of fault that only turns up once someone is using the thing.
    /// </remarks>
    private bool TypingSomewhere =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (TypingSomewhere) return;

        switch (e.Key)
        {
            // Space, because that is what every paint application uses for this, and because the
            // off hand can reach it while the pen stays on the tablet. Held rather than toggled.
            case Key.Space:
                PaintView.PanModifierHeld = true;
                e.Handled = true;
                break;

            case Key.O when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                _ = OpenDocument();
                break;

            case Key.S when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                _ = SaveDocument();
                break;

            case Key.E when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                _ = ExportImage();
                break;

            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _paint.Undo();
                PaintView.Invalidate();
                e.Handled = true;
                break;

            // The active layer, not the document. Clearing what you are working on is the common
            // case; clearing everything is still on the canvas menu for the other one.
            case Key.Delete:
            case Key.Back:
                _paint.ClearActiveLayer();
                PaintView.Invalidate();
                e.Handled = true;
                break;
        }
    }

    private void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;

        PaintView.PanModifierHeld = false;
        e.Handled = true;
    }

    // -- The brush panel ------------------------------------------

    /// <summary>
    /// Every control edits the selected brush and nothing else.
    /// </summary>
    /// <remarks>
    /// The pattern is the same throughout: read the control, write it back into
    /// <c>_brushes[_brushIndex]</c>, and show the result. A record means an edit is a replacement,
    /// so there is no partially updated brush for a stroke to start with.
    /// </remarks>
    private void WireBrushPanel()
    {
        BrushCombo.ItemsSource = _brushes.Select(b => b.Name).ToList();
        BrushCombo.SelectedIndex = 0;
        BrushCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingBrush || BrushCombo.SelectedIndex < 0) return;
            _brushIndex = BrushCombo.SelectedIndex;
            ShowBrush();
        };

        EngineCombo.ItemsSource = new[] { "Taper", "Dabs", "MyPaint" };
        EngineCombo.SelectionChanged += (_, _) => EditBrush(b => b with
        {
            Engine = (BrushEngineKind)Math.Max(0, EngineCombo.SelectedIndex),
        });

        InterpolationCombo.ItemsSource = new[] { "Straight", "Curved" };
        InterpolationCombo.SelectionChanged += (_, _) => EditBrush(b => b with
        {
            Interpolation = InterpolationCombo.SelectedIndex == 1
                ? StrokeInterpolation.Curved
                : StrokeInterpolation.Straight,
        });

        DrivesCombo.ItemsSource = new[] { "Size", "Opacity", "Both" };
        DrivesCombo.SelectionChanged += (_, _) => EditBrush(b => b with
        {
            PressureDrives = (PressureControl)Math.Max(0, DrivesCombo.SelectedIndex),
        });

        OnSlider(SizeSlider, () => EditBrush(b => b with { Size = SizeSlider.Value }));
        OnSlider(SpacingSlider, () => EditBrush(b => b with { Spacing = SpacingSlider.Value }));
        OnSlider(BrushOpacitySlider,
                 () => EditBrush(b => b with { Opacity = BrushOpacitySlider.Value / 100.0 }));

        OnSlider(CurveStartSlider, () => EditBrush(b => b with
        {
            Curve = b.Curve with { Start = CurveStartSlider.Value },
        }));
        OnSlider(CurveEndSlider, () => EditBrush(b => b with
        {
            Curve = b.Curve with { End = CurveEndSlider.Value },
        }));
        OnSlider(CurveExponentSlider, () => EditBrush(b => b with
        {
            Curve = b.Curve with { Exponent = CurveExponentSlider.Value },
        }));

        OnSlider(PositionSmoothingSlider, () => EditBrush(b => b with
        {
            Smoothing = b.Smoothing with { Position = PositionSmoothingSlider.Value },
        }));
        OnSlider(PressureSmoothingSlider, () => EditBrush(b => b with
        {
            Smoothing = b.Smoothing with { Pressure = PressureSmoothingSlider.Value },
        }));
        OnSlider(TailSlider, () => EditBrush(b => b with
        {
            Smoothing = b.Smoothing with { TailAggressiveness = TailSlider.Value },
        }));

        ShowBrush();
    }

    private static void OnSlider(Slider slider, Action changed)
        => slider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Value") changed();
        };

    /// <summary>Apply one edit to the selected brush, then show what it became.</summary>
    /// <remarks>
    /// Showing it afterwards is not redundant: the record clamps what it is given, so a slider can
    /// ask for a value the brush will not take and the panel has to end up displaying the brush
    /// rather than the request.
    /// </remarks>
    private void EditBrush(Func<BrushSettings, BrushSettings> edit)
    {
        if (_syncingBrush) return;

        _brushes[_brushIndex] = edit(_brushes[_brushIndex]);
        ShowBrush();
    }

    /// <summary>Put every control in step with the selected brush.</summary>
    private void ShowBrush()
    {
        _syncingBrush = true;

        var b = Brush;
        BrushCombo.SelectedIndex = _brushIndex;
        EngineCombo.SelectedIndex = (int)b.Engine;
        InterpolationCombo.SelectedIndex = b.Interpolation == StrokeInterpolation.Curved ? 1 : 0;
        DrivesCombo.SelectedIndex = (int)b.PressureDrives;

        SizeSlider.Value = b.Size;
        SizeLabel.Text = $"{b.Size:F0} px";

        bool mypaint = b.Engine == BrushEngineKind.MyPaint;

        // A MyPaint brush brings its own size, opacity, softness, spacing and pressure response,
        // all of them varying per dab. Leaving the sliders live would offer edits that the next
        // dab overwrites, so they are disabled and the brush file's name stands in their place.
        SizeRow.IsEnabled = OpacityRow.IsEnabled = DrivesRow.IsEnabled = !mypaint;
        CurveSection.IsEnabled = !mypaint;
        MyPaintRow.IsVisible = mypaint;
        MyPaintLabel.Text = b.MyPaint is { } file
            ? file.Ignored.Count == 0 ? file.Name : $"{file.Name} ({file.Ignored.Count} unused)"
            : "defaults";

        SpacingRow.IsVisible = b.Engine == BrushEngineKind.Dabs;
        SpacingSlider.Value = b.Spacing;
        SpacingLabel.Text = $"{b.Spacing:F2}";

        BrushOpacitySlider.Value = b.Opacity * 100;
        BrushOpacityLabel.Text = $"{b.Opacity * 100:F0}%";

        CurveStartSlider.Value = b.Curve.Start;
        CurveStartLabel.Text = $"{b.Curve.Start:F2}";
        CurveEndSlider.Value = b.Curve.End;
        CurveEndLabel.Text = $"{b.Curve.End:F2}";
        CurveExponentSlider.Value = b.Curve.Exponent;
        CurveExponentLabel.Text = $"{b.Curve.Exponent:F2}";

        PositionSmoothingSlider.Value = b.Smoothing.Position;
        PositionSmoothingLabel.Text = Reach(b.Smoothing.Position);
        PressureSmoothingSlider.Value = b.Smoothing.Pressure;
        PressureSmoothingLabel.Text = Reach(b.Smoothing.Pressure);
        TailSlider.Value = b.Smoothing.TailAggressiveness;
        TailLabel.Text = $"{b.Smoothing.TailAggressiveness:F2}";

        // Tail only changes how the filter lets go, so it has nothing to do when neither runs.
        TailRow.IsEnabled = b.Smoothing.IsEnabled;

        // On the headers, so a folded section still says what it holds. Without this, collapsing
        // them would trade height for having to open each one to see where it was set.
        CurveSummary.Text = $"{b.Curve.Start:F2} / {b.Curve.End:F2} / {b.Curve.Exponent:F2}";
        SmoothingSummary.Text = b.Smoothing.IsEnabled
            ? $"{Reach(b.Smoothing.Position)} / {Reach(b.Smoothing.Pressure)}"
            : "off";

        _syncingBrush = false;

        DrawCurve(b.Curve);
    }

    /// <summary>A reach in document units, or the word for not filtering at all.</summary>
    private static string Reach(double distance) => distance > 0 ? $"{distance:F0}" : "off";

    /// <summary>
    /// Plot what the brush makes of the pen, across the pen's whole range.
    /// </summary>
    /// <remarks>
    /// Sampled through <see cref="PressureCurve.Apply"/> rather than redrawn from the formula, so
    /// the picture cannot disagree with the brush. The flat run at the left is the dead zone
    /// <c>Start</c> removes and the flat run at the right is where <c>End</c> has saturated -- both
    /// are the point of those two numbers and neither is obvious from a percentage.
    /// </remarks>
    private void DrawCurve(PressureCurve curve)
    {
        double w = CurvePlot.Bounds.Width, h = CurvePlot.Bounds.Height;
        if (w <= 1 || h <= 1) return;   // before the first layout pass

        const double pad = 6;
        double plotW = w - 2 * pad, plotH = h - 2 * pad;

        // Where a linear curve would run, for the shape to be read against.
        CurveDiagonal.StartPoint = new Point(pad, h - pad);
        CurveDiagonal.EndPoint = new Point(w - pad, pad);

        var points = new List<Point>();
        const int steps = 64;
        for (int i = 0; i <= steps; i++)
        {
            double x = (double)i / steps;
            double y = curve.Apply(x);
            points.Add(new Point(pad + x * plotW, h - pad - y * plotH));
        }

        CurveLine.Points = points;
    }

    private void PopulateApis()
    {
        _apis = AvaloniaPenApis.GetAvailable();
        ApiCombo.ItemsSource = _apis.Select(a => a.Label()).ToList();

        // Wintab's digitizer context where it exists: it is the finest of the available clocks
        // and the one a tablet actually reports through. Measured in WinPenKit -- one timestamp
        // per point at 1 ms, where the framework paths vary by four orders of magnitude.
        int preferred = _apis.ToList().FindIndex(a => a == InputApi.WintabDigitizer);
        ApiCombo.SelectedIndex = preferred >= 0 ? preferred : (_apis.Count > 0 ? 0 : -1);
        ApiCombo.SelectionChanged += (_, _) => StartSession();
    }

    private void StartSession()
    {
        if (_apis.Count == 0 || ApiCombo.SelectedIndex < 0) return;

        _renderTimer.Stop();
        _penSession?.Stop();
        _penSession?.Dispose();
        _paint.EndStroke();

        var api = _apis[ApiCombo.SelectedIndex];
        _penSession = api == InputApi.AvaloniaPointer
            ? new AvaloniaPointerSession(PaintView.Host)
            : PenSessionFactory.Create(api);

        IntPtr hwnd = TryGetPlatformHandle() is { } handle ? handle.Handle : IntPtr.Zero;

        if (_penSession.Start(hwnd) is { } error)
        {
            StatusLabel.Text = error;
            _penSession.Dispose();
            _penSession = null;
            return;
        }

        StatusLabel.Text = api.Label();
        _renderTimer.Start();
    }

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        // Before the early return. The viewport repaints for reasons that have nothing to do with
        // the pen -- a zoom, a pan, the first layout pass -- and this method gives up as soon as
        // there is nothing to drain. It costs a comparison when nothing has changed.
        PaintView.PresentIfNeeded();

        if (_penSession is null) return;

        var points = _penSession.DrainPoints();
        if (points.Length == 0) return;

        int maxPressure = _penSession.MaxPressure;
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;

        // A pan is a gesture made with the pen down, and the pen reports through all of it --
        // under Wintab those samples never went near the viewport control, so dropping them has to
        // happen here. Without this a pan would leave a stroke across everything it crossed.
        if (PaintView.IsPanning)
        {
            _paint.EndStroke();
            return;
        }

        foreach (var pt in points)
        {
            // Converted by hand rather than through PointToClient, which takes a PixelPoint and so
            // forces the position onto the whole-pixel grid on the way in. Measured in the Lab:
            // quantising a hi-res stroke to whole pixels takes the median turn between segments
            // from 1.5 degrees to 11.3, because at the ~2px steps a tablet reports there are only
            // a handful of directions a segment on an integer grid can point in.
            Point clientPt;
            try
            {
                var origin = topLevel.PointToScreen(new Point(0, 0));
                double scale = topLevel.RenderScaling;
                clientPt = new Point((pt.DesktopX - origin.X) / scale,
                                     (pt.DesktopY - origin.Y) / scale);
            }
            catch
            {
                _paint.EndStroke();
                continue;
            }

            if (PaintView.Host.TranslatePoint(new Point(0, 0), topLevel) is not { } viewportOrigin)
            {
                _paint.EndStroke();
                continue;
            }

            var inViewport = new Point(clientPt.X - viewportOrigin.X, clientPt.Y - viewportOrigin.Y);
            if (!PaintView.HitTest(inViewport))
            {
                _paint.EndStroke();
                continue;
            }

            // Mapped here, at the edge, and read live rather than cached. Everything below works
            // in document coordinates and knows nothing about zoom or pan, which is what makes a
            // stroke the same set of document positions whatever the view was doing at the time.
            var (docX, docY) = PaintView.ToDocument(inViewport);

            double pressure = maxPressure > 0 ? (double)pt.Pressure / maxPressure : 0;

            // What the pen reported, untouched. The brush's own curve is applied inside the
            // session, which is what makes a stroke keep the response it was drawn with -- there is
            // no application-wide pipeline here for it to have come from instead.
            _paint.AddSample(docX, docY, pressure, Brush,
                new PenOrientation(pt.Azimuth, pt.Altitude, pt.Twist, pt.TiltX, pt.TiltY),
                pt.TimestampMicroseconds);
        }

        PaintView.PresentIfNeeded();
    }
}
