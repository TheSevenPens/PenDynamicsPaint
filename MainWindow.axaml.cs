using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PenDynamicsPaint.Drawing.MyPaint;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Paint;
using SkiaSharp;
using WinPenKit;
using WinPenKit.Avalonia;
using WinPenKit.Diagnostics;

// Aliased rather than imported whole: Avalonia.Controls.Shapes also holds a Path, and this file
// works with file paths.
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

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

    /// <summary>The backend in use, chosen in Tools &gt; Options.</summary>
    /// <remarks>
    /// A field rather than the selection of a combo box, because there is no combo box on the
    /// window any more: this is which driver the tablet is read through and it is set once.
    /// </remarks>
    private InputApi? _api;

    /// <summary>The last pressure the pen reported, for the dot on the curve.</summary>
    private double _livePressure;

    /// <summary>Which node of the pressure curve the pointer has hold of, if any.</summary>
    private CurveNode _curveDrag;

    /// <summary>When that reading arrived, so a stale one can be let go of.</summary>
    /// <remarks>
    /// A pen held still sends nothing, so the reading cannot simply be cleared on a tick that
    /// drains no points -- the dot would blink out whenever the hand paused. A pen lifted out of
    /// range also sends nothing, and then the dot would stick at whatever it last read. A timeout
    /// tells the two apart: longer than a pause between packets, shorter than anyone would notice.
    /// </remarks>
    private readonly System.Diagnostics.Stopwatch _pressureAge = System.Diagnostics.Stopwatch.StartNew();
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

    /// <summary>What the window is drawing on, for a test that drives the window itself.</summary>
    /// <remarks>
    /// The seam the window tests need and the only one they need. Everything else they touch is a
    /// real control: the point is to exercise what the window does with a click rather than to
    /// reach past it -- see TheSevenPens/PenDynamicsPaint#16 for the faults that went unseen
    /// without it.
    /// </remarks>
    internal PaintSession Session => _paint;

    /// <inheritdoc cref="Brush"/>
    internal BrushSettings CurrentBrush => Brush;

    /// <summary>The ink swatches, in the order they appear.</summary>
    internal IReadOnlyList<Border> Swatches => _swatches;

    /// <inheritdoc cref="Inks"/>
    internal static IReadOnlyList<(string Name, SKColor Colour)> InkChoices => Inks;

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

            // The last row is the paper, which is not a layer and cannot be the active one. Put the
            // selection back where it was rather than letting it sit on something undrawable.
            if (LayerList.SelectedIndex >= _layerRows.Count)
            {
                _syncingLayers = true;
                LayerList.SelectedIndex = ToRow(_paint.ActiveLayerIndex);
                _syncingLayers = false;
                return;
            }

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

        RebuildLayerList();



        // Tunnelling, not bubbling. A ComboBox swallows Space to open itself and a ListBox
        // swallows Delete, so by the time a bubbling handler saw either, the shortcut would
        // already have been eaten by whatever the user last clicked on.
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, Window_KeyUp, RoutingStrategies.Tunnel);

        // The default. Assigned here rather than as a property initialiser because it closes
        // over this window, which a field initialiser cannot do before the constructor runs.
        AskAboutPen = async (api, error) =>
        {
            // The driver is asked here rather than inside the dialog, so that what the dialog
            // says is a function of what it was handed and a test can hand it either answer.
            var dialog = new PenProblemWindow(api, error,
                                              _apis.Contains(InputApi.AvaloniaPointer),
                                              WintabDiagnostics.ContextTable());
            await dialog.ShowDialog(this);
            return dialog.Choice;
        };

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
    private sealed record LayerRow(Control Root, CheckBox Visible, TextBlock Name, TextBox Editor);

    /// <summary>Panel row to stack index. The panel shows the stack upside down.</summary>
    private int ToStackIndex(int row) => _paint.Layers.Count - 1 - row;

    /// <inheritdoc cref="ToStackIndex" />
    private int ToRow(int stackIndex) => _paint.Layers.Count - 1 - stackIndex;

    // -- Ink -----------------------------------------------------

    /// <summary>The colours a stroke can be drawn in.</summary>
    /// <remarks>
    /// <para>
    /// A fixed set rather than a picker, because the point is to have more than one colour at all.
    /// Until this there was exactly one: <c>PaintSession</c> held a dark navy and nothing ever
    /// called <c>SetStrokeColor</c>, so every mark in the application was the same colour.
    /// </para>
    /// <para>
    /// That made the smudge brush impossible to judge. It worked -- it picked colour up off the
    /// canvas and carried it -- but the only colour there to pick up was the one it would have
    /// painted anyway, so it drew what looked like an ordinary stroke. A feature can be correct and
    /// still be untestable by the person using it.
    /// </para>
    /// <para>
    /// A picker with the full space is the obvious next thing and is not this: choosing a colour
    /// properly wants a wheel, a value slider and somewhere to keep the recent ones.
    /// </para>
    /// </remarks>
    private static readonly (string Name, SKColor Colour)[] Inks =
    [
        ("Ink",     new SKColor(0x1A, 0x1A, 0x2E)),
        ("Red",     new SKColor(0xD9, 0x32, 0x2F)),
        ("Orange",  new SKColor(0xE8, 0x7A, 0x1E)),
        ("Yellow",  new SKColor(0xF0, 0xC0, 0x20)),
        ("Green",   new SKColor(0x2E, 0xA0, 0x4E)),
        ("Blue",    new SKColor(0x22, 0x77, 0xCC)),
        ("Violet",  new SKColor(0x7B, 0x3F, 0xB8)),
        ("Brown",   new SKColor(0x8A, 0x5A, 0x2B)),
        ("White",   new SKColor(0xFF, 0xFF, 0xFF)),
    ];

    private readonly List<Border> _swatches = [];
    private int _inkIndex;

    /// <summary>Build the swatch row, and put the first colour in front of the pen.</summary>
    private void BuildSwatches()
    {
        for (int i = 0; i < Inks.Length; i++)
        {
            int index = i;

            var swatch = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(
                    Color.FromArgb(255, Inks[i].Colour.Red, Inks[i].Colour.Green, Inks[i].Colour.Blue)),
                BorderThickness = new Thickness(2),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };

            ToolTip.SetTip(swatch, Inks[i].Name);

            swatch.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                ChooseInk(index);
            };

            _swatches.Add(swatch);
            SwatchRow.Children.Add(swatch);
        }

        ChooseInk(0);
    }

    private void ChooseInk(int index)
    {
        _inkIndex = index;
        _paint.SetStrokeColor(Inks[index].Colour);

        // Outlined rather than grown or moved: a swatch that changes size shifts the ones beside
        // it, and picking a colour twice in a row should not move the target under the pen.
        //
        // Every swatch keeps an outline, not just the chosen one. Without it the white swatch is a
        // white square on a pale bar and cannot be seen at all, which is a strange way to offer
        // somebody a colour.
        for (int i = 0; i < _swatches.Count; i++)
        {
            _swatches[i].BorderBrush = i == index
                ? new SolidColorBrush(Colors.Black)
                : new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00));
        }

        StatusLabel.Text = $"Ink: {Inks[index].Name}";
    }

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

            // Sits in the same place as the label and takes over from it. Renaming used to be a
            // text box further down the panel, which meant the name being edited and the name in
            // the list were two controls showing the same thing in different places.
            var editor = new TextBox
            {
                Text = layer.Name,
                FontSize = 12,
                Padding = new Thickness(2, 0),
                MinHeight = 0,
                Height = 20,
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var root = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { visible, name, editor },
            };

            var row = new LayerRow(root, visible, name, editor);

            root.DoubleTapped += (_, e) =>
            {
                e.Handled = true;
                BeginRename(index);
            };

            editor.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) CommitRename(index, keep: true);
                else if (e.Key == Key.Escape) CommitRename(index, keep: false);
                else return;

                e.Handled = true;
            };

            // Clicking elsewhere is a way of saying the editing is finished, and the least
            // surprising reading of it is to keep what was typed rather than throw it away.
            editor.LostFocus += (_, _) =>
            {
                if (editor.IsVisible) CommitRename(index, keep: true);
            };

            root.ContextMenu = LayerMenu(index);

            _layerRows.Add(row);
        }

        // The paper, under everything, where the bottom of the stack is. It is not a layer and
        // has no row in _layerRows: nothing selects it, nothing draws on it, and the commands that
        // act on a layer would all have to refuse. It is here because this is where someone looks
        // for the colour behind their painting.
        var rows = _layerRows.Select(r => r.Root).ToList();
        rows.Add(PaperRow());

        LayerList.ItemsSource = rows;
        LayerList.SelectedIndex = ToRow(_paint.ActiveLayerIndex);
        _syncingLayers = false;

        ShowSelectedLayer();
    }

    /// <summary>The row standing for the paper, at the bottom of the stack.</summary>
    /// <remarks>
    /// Deliberately not a <c>Layer</c>. A layer holds pixels that can be drawn on, undone, merged
    /// and reordered; the paper is one colour filling the document, and making it a real layer
    /// would mean a full-size bitmap of a single colour and four commands that have to refuse to
    /// work on it.
    /// </remarks>
    private Control PaperRow()
    {
        var swatch = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00)),
            Background = new SolidColorBrush(
                Color.FromArgb(255, _paint.Paper.Red, _paint.Paper.Green, _paint.Paper.Blue)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // No explicit colour. The layer rows above take the list's own foreground and this has to
        // match them; naming one here means picking a colour for a surface whose colour is the
        // theme's business, and getting it wrong makes the row invisible rather than merely wrong.
        var label = new TextBlock
        {
            Text = "Paper",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
        };

        var root = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { swatch, label },
        };

        var menu = new ContextMenu();

        foreach (var (name, colour) in Papers)
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => ChoosePaper(colour);
            menu.Items.Add(item);
        }

        root.ContextMenu = menu;
        root.DoubleTapped += (_, e) => { e.Handled = true; menu.Open(root); };

        ToolTip.SetTip(root, "The colour behind every layer. Right-click to change it.");

        return root;
    }

    /// <summary>The papers on offer.</summary>
    /// <remarks>
    /// Shades rather than colours, because this is the ground a painting sits on rather than
    /// something drawn with. The same argument as the ink palette: a picker is its own piece of
    /// work, and having more than one is the thing that matters first.
    /// </remarks>
    private static readonly (string Name, SKColor Colour)[] Papers =
    [
        ("White",      new SKColor(0xFF, 0xFF, 0xFF)),
        ("Off white",  new SKColor(0xF7, 0xF3, 0xEA)),
        ("Cream",      new SKColor(0xF2, 0xE8, 0xCF)),
        ("Grey",       new SKColor(0xC8, 0xC8, 0xC8)),
        ("Slate",      new SKColor(0x3A, 0x3F, 0x4A)),
        ("Black",      new SKColor(0x12, 0x12, 0x12)),
    ];

    private void ChoosePaper(SKColor colour)
    {
        _paint.Paper = colour;

        RebuildLayerList();
        PaintView.Invalidate();

        StatusLabel.Text = $"Paper: {Papers.First(p => p.Colour == colour).Name}";
    }

    /// <summary>What can be done to one particular layer.</summary>
    /// <remarks>
    /// Built per row and closed over that row's index, so every item acts on the layer it was
    /// opened from rather than on whichever one happens to be selected. That is the difference
    /// between a context menu and a toolbar, and it is the whole reason these moved.
    /// </remarks>
    private ContextMenu LayerMenu(int stackIndex)
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => act();
            return item;
        }

        // Right-clicking a layer selects it first. Otherwise the menu acts on the row it was
        // opened from while the panel below goes on showing a different one.
        menu.Opened += (_, _) =>
        {
            _paint.SetActiveLayer(stackIndex);
            LayerList.SelectedIndex = ToRow(stackIndex);
            ShowSelectedLayer();
        };

        menu.Items.Add(Item("Rename", () => BeginRename(stackIndex)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", () => { _paint.RemoveLayer(stackIndex); AfterLayerChange(); },
                            _paint.Layers.Count > 1));
        menu.Items.Add(Item("Merge down", () => { _paint.MergeDown(stackIndex); AfterLayerChange(); },
                            stackIndex > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Raise", () => { _paint.MoveLayer(stackIndex, stackIndex + 1); AfterLayerChange(); },
                            stackIndex < _paint.Layers.Count - 1));
        menu.Items.Add(Item("Lower", () => { _paint.MoveLayer(stackIndex, stackIndex - 1); AfterLayerChange(); },
                            stackIndex > 0));

        return menu;
    }

    private void AfterLayerChange()
    {
        RebuildLayerList();
        PaintView.Invalidate();
    }

    /// <summary>Turn a layer's name in the list into something that can be typed in.</summary>
    private void BeginRename(int stackIndex)
    {
        int row = ToRow(stackIndex);
        if (row < 0 || row >= _layerRows.Count) return;

        var entry = _layerRows[row];

        entry.Editor.Text = _paint.Layers[stackIndex].Name;
        entry.Name.IsVisible = false;
        entry.Editor.IsVisible = true;

        entry.Editor.Focus();
        entry.Editor.SelectAll();
    }

    /// <summary>Finish renaming, keeping what was typed or dropping it.</summary>
    private void CommitRename(int stackIndex, bool keep)
    {
        int row = ToRow(stackIndex);
        if (row < 0 || row >= _layerRows.Count) return;

        var entry = _layerRows[row];

        entry.Editor.IsVisible = false;
        entry.Name.IsVisible = true;

        if (!keep) return;

        // A layer with no name at all is a row with nothing in it, so an empty box is treated as
        // having changed nothing rather than as a name.
        string typed = entry.Editor.Text ?? "";
        if (typed.Trim().Length == 0) return;

        if (_paint.RenameLayer(stackIndex, typed)) entry.Name.Text = typed;
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
        LayerOpacitySlider.Value = layer.Opacity * 100;
        LayerOpacityLabel.Text = $"{layer.Opacity * 100:F0}%";



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
    /// <summary>Ask for a <c>.myb</c> and read it, or null if there is nothing to read.</summary>
    /// <remarks>
    /// Shared by the two things that want one, which are not the same thing: changing which file a
    /// MyPaint brush is, and making a new brush out of a file.
    /// </remarks>
    private async Task<MyPaintBrush?> PickMyPaintBrush()
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

        if (files.Count == 0) return null;

        var file = files[0];
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);

            string name = Path.GetFileNameWithoutExtension(file.Name);
            var brush = MyPaintBrush.Parse(await reader.ReadToEndAsync(), name);

            StatusLabel.Text = brush.Ignored.Count == 0
                ? $"{name}: loaded"
                : $"{name}: loaded, {brush.Ignored.Count} setting(s) not acted on";

            return brush;
        }
        catch (Exception error) when (error is FormatException or IOException)
        {
            // Said out loud rather than swallowed: a brush that will not load is worth knowing
            // about, and the two reasons -- the old text format, and a file that cannot be read --
            // are both things the user can do something about.
            StatusLabel.Text = $"{file.Name}: {error.Message}";
            return null;
        }
    }

    /// <summary>Put a different file behind the MyPaint brush that is selected.</summary>
    private async void LoadMyPaintBrush_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickMyPaintBrush() is not { } loaded) return;

        EditBrush(b => b with { Name = loaded.Name, MyPaint = loaded });

        RefreshBrushList();
        ShowBrush();
    }

    /// <summary>How a brush is named in the picker: its own name and what kind it is.</summary>
    /// <remarks>
    /// The kind decides which settings a brush even has, and it used to be readable only by opening
    /// the panel and noticing which controls had gone. A list of bare names says nothing about why
    /// two entries offer different things.
    /// </remarks>
    private static string Describe(BrushSettings brush) => $"{brush.Name}  ({Kind(brush.Engine)})";

    private static string Kind(BrushEngineKind engine) => engine switch
    {
        BrushEngineKind.Dabs => "Dabs",
        BrushEngineKind.MyPaint => "MyPaint",
        _ => "Taper",
    };

    /// <summary>Rebuild the picker without letting it change which brush is selected.</summary>
    /// <remarks>
    /// Assigning ItemsSource resets the selection, which raises SelectionChanged, which sets
    /// _brushIndex from whatever the combo has just decided -- so refreshing the list while
    /// pointing at a new brush landed back on the first one, and adding a brush selected something
    /// else. The flag is the same one the rest of the panel uses to tell its own writes from a
    /// person's.
    /// </remarks>
    private void RefreshBrushList()
    {
        _syncingBrush = true;

        BrushCombo.ItemsSource = _brushes.Select(Describe).ToList();
        BrushCombo.SelectedIndex = _brushIndex;

        _syncingBrush = false;
    }

    /// <summary>Add a brush to the set and put it in front of the pen.</summary>
    private void AddBrush(BrushSettings brush)
    {
        _brushes.Add(brush);
        _brushIndex = _brushes.Count - 1;

        RefreshBrushList();
        ShowBrush();

        StatusLabel.Text = $"New brush: {brush.Name}";
    }

    /// <summary>Make a brush of the given kind, for a test that needs one added.</summary>
    /// <remarks>
    /// The menu items are what a person uses; this is the same call without the menu, so a test
    /// can check that adding a brush adds one rather than replacing what was selected.
    /// </remarks>
    internal void NewBrushForTest(BrushEngineKind engine) =>
        AddBrush(BrushSettings.Default with
        {
            Name = UnusedName($"{Kind(engine)} brush"),
            Engine = engine,
            Size = 24,
        });

    /// <summary>A name not already taken, so two brushes are never the same row twice.</summary>
    private string UnusedName(string stem)
    {
        if (_brushes.All(b => b.Name != stem)) return stem;

        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} {n}";
            if (_brushes.All(b => b.Name != candidate)) return candidate;
        }
    }

    private void NewTaperBrush_Click(object? sender, RoutedEventArgs e) =>
        AddBrush(BrushSettings.Default with
        {
            Name = UnusedName("Taper brush"),
            Engine = BrushEngineKind.Taper,
            Size = 24,
            Interpolation = StrokeInterpolation.Curved,
        });

    private void NewDabsBrush_Click(object? sender, RoutedEventArgs e) =>
        AddBrush(BrushSettings.Default with
        {
            Name = UnusedName("Dabs brush"),
            Engine = BrushEngineKind.Dabs,
            Size = 24,
            Spacing = 0.25,
        });

    /// <summary>Make a new brush out of a <c>.myb</c>, rather than turning a brush into one.</summary>
    private async void NewMyPaintBrush_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickMyPaintBrush() is not { } loaded) return;

        AddBrush(BrushSettings.Default with
        {
            Name = UnusedName(loaded.Name),
            Engine = BrushEngineKind.MyPaint,
            MyPaint = loaded,
            Interpolation = StrokeInterpolation.Curved,
        });
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
    internal void AdoptDocument(PaintSession session, IStorageFile? from)
    {
        var old = _paint;

        _paint = session;

        // The ink belongs to the application rather than to the file, so it is re-applied to the
        // session that replaced the old one -- which would otherwise start on its own default.
        _paint.SetStrokeColor(Inks[_inkIndex].Colour);

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

    // -- Menu -----------------------------------------------------

    private async void New_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewDocumentWindow();
        await dialog.ShowDialog(this);

        if (dialog.Chosen is not { } size) return;

        AdoptDocument(new PaintSession(size.Width, size.Height), from: null);
        StatusLabel.Text = $"New document, {size.Width} x {size.Height}";
    }

    private async void SaveAs_Click(object? sender, RoutedEventArgs e)
    {
        // Forget where it came from, so Save asks again and then follows the answer.
        _documentFile = null;
        await SaveDocument();
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private void Undo_Click(object? sender, RoutedEventArgs e)
    {
        _paint.Undo();
        PaintView.Invalidate();
    }

    private void ClearLayer_Click(object? sender, RoutedEventArgs e)
    {
        _paint.ClearActiveLayer();
        PaintView.Invalidate();
    }

    private void ClearDocument_Click(object? sender, RoutedEventArgs e)
    {
        _paint.Clear();
        PaintView.Invalidate();
    }

    private async void Options_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new OptionsWindow(_apis, _api);
        await dialog.ShowDialog(this);

        // Nothing to do if it was cancelled, or if the answer is what is already running:
        // restarting a pen session drops whatever is in flight for no reason.
        if (dialog.Chosen is not { } chosen || chosen == _api) return;

        _api = chosen;
        StartSession();
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
        BrushCombo.ItemsSource = _brushes.Select(Describe).ToList();
        BrushCombo.SelectedIndex = 0;
        BrushCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingBrush || BrushCombo.SelectedIndex < 0) return;
            _brushIndex = BrushCombo.SelectedIndex;
            ShowBrush();
        };

        CompositingCombo.ItemsSource = new[] { "Wash", "Direct" };
        CompositingCombo.SelectionChanged += (_, _) => EditBrush(b => b with
        {
            Compositing = CompositingCombo.SelectedIndex == 1
                ? StrokeCompositing.Direct
                : StrokeCompositing.Wash,
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

        // The curve is edited on the plot rather than by three sliders beside it. A slider for
        // Start and a slider for End describe two ends of a range without showing that they are
        // two ends of the same range, and the exponent slider is the worst of the three: a number
        // between 0.1 and 4 that has to be solved for to get a shape anyone can picture.
        CurvePlot.PointerPressed += CurvePlot_PointerPressed;
        CurvePlot.PointerMoved += CurvePlot_PointerMoved;
        CurvePlot.PointerReleased += (_, e) =>
        {
            _curveDrag = CurveNode.None;
            e.Pointer.Capture(null);
        };

        OnNumberBox(CurveStartBox, (v, b) => b with { Curve = b.Curve with { Start = v } });
        OnNumberBox(CurveEndBox, (v, b) => b with { Curve = b.Curve with { End = v } });
        OnNumberBox(CurveExponentBox, (v, b) => b with { Curve = b.Curve with { Exponent = v } });

        OnSlider(PositionSmoothingSlider, () => EditBrush(b => b with
        {
            Smoothing = b.Smoothing with { Position = PositionSmoothingSlider.Value },
        }));
        OnSlider(PressureSmoothingSlider, () => EditBrush(b => b with
        {
            Smoothing = b.Smoothing with { Pressure = PressureSmoothingSlider.Value },
        }));
        OnSlider(TiltSmoothingSlider, () => EditBrush(b => b with
        {
            Smoothing = b.Smoothing with { Tilt = TiltSmoothingSlider.Value },
        }));
        OnSlider(TailSlider, () => EditBrush(b => b with
        {
            Smoothing = b.Smoothing with { TailAggressiveness = TailSlider.Value },
        }));

        ShowBrush();
        BuildSwatches();
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
        InterpolationCombo.SelectedIndex = b.Interpolation == StrokeInterpolation.Curved ? 1 : 0;
        CompositingCombo.SelectedIndex = b.Compositing == StrokeCompositing.Direct ? 1 : 0;
        DrivesCombo.SelectedIndex = (int)b.PressureDrives;

        SizeSlider.Value = b.Size;
        SizeLabel.Text = $"{b.Size:F0} px";

        bool mypaint = b.Engine == BrushEngineKind.MyPaint;

        // A MyPaint brush brings its own size, opacity, softness, spacing and pressure response,
        // all of them varying per dab, so none of those controls applies to one.
        //
        // Hidden rather than disabled. Four greyed-out rows read as something broken, or as
        // settings that would work if only the right thing were selected; absent ones read as not
        // applicable, which is what they are. It also makes the panel shorter for exactly the
        // brushes that need the room.
        SizeRow.IsVisible = OpacityRow.IsVisible = DrivesRow.IsVisible = !mypaint;
        CurveSection.IsVisible = !mypaint;
        MyPaintRow.IsVisible = mypaint;
        MyPaintLabel.Text = b.MyPaint is { } file
            ? file.Ignored.Count == 0 ? file.Name : $"{file.Name} ({file.Ignored.Count} unused)"
            : "defaults";

        SpacingRow.IsVisible = b.Engine == BrushEngineKind.Dabs;
        SpacingSlider.Value = b.Spacing;
        SpacingLabel.Text = $"{b.Spacing:F2}";

        BrushOpacitySlider.Value = b.Opacity * 100;
        BrushOpacityLabel.Text = $"{b.Opacity * 100:F0}%";

        CurveStartBox.Text = $"{b.Curve.Start:F2}";
        CurveEndBox.Text = $"{b.Curve.End:F2}";
        CurveExponentBox.Text = $"{b.Curve.Exponent:F2}";

        PositionSmoothingSlider.Value = b.Smoothing.Position;
        PositionSmoothingLabel.Text = Reach(b.Smoothing.Position);
        PressureSmoothingSlider.Value = b.Smoothing.Pressure;
        PressureSmoothingLabel.Text = Reach(b.Smoothing.Pressure);
        TiltSmoothingSlider.Value = b.Smoothing.Tilt;
        TiltSmoothingLabel.Text = Reach(b.Smoothing.Tilt);
        TailSlider.Value = b.Smoothing.TailAggressiveness;
        TailLabel.Text = $"{b.Smoothing.TailAggressiveness:F2}";

        // Tail only changes how the filter lets go, so it has nothing to do when neither runs.
        TailRow.IsEnabled = b.Smoothing.IsEnabled;

        // On the headers, so a folded section still says what it holds. Without this, collapsing
        // them would trade height for having to open each one to see where it was set.
        CurveSummary.Text = $"{b.Curve.Start:F2} / {b.Curve.End:F2} / {b.Curve.Exponent:F2}";
        SmoothingSummary.Text = b.Smoothing.IsEnabled
            ? $"{Reach(b.Smoothing.Position)} / {Reach(b.Smoothing.Pressure)} / " +
              $"{Reach(b.Smoothing.Tilt)}"
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
    /// <summary>The curves on the three preset buttons.</summary>
    /// <remarks>
    /// <para>
    /// Soft reaches full width early, so a light hand still lays a full mark and the brush feels
    /// eager. Hard holds off, so width arrives only when the pen is genuinely leaned on and the
    /// stroke stays fine until then. Default is the straight line between them: what the pen
    /// reports is what the brush does.
    /// </para>
    /// <para>
    /// <b>A preset sets the whole curve</b>, range included: the full range and one exponent. The
    /// first version moved only the exponent, on the reasoning that Start and End are a tablet's
    /// calibration -- where its reading becomes usable, where it saturates -- and not a statement
    /// about how a brush should feel.
    /// </para>
    /// <para>
    /// That is wrong about what a preset is for. Left over the top of some other range, the button
    /// is a modifier rather than a preset: pressing Soft gives a different curve depending on what
    /// the brush happened to be set to, and pressing it twice from different starting points gives
    /// two different brushes. A preset has to be somewhere you can get back to.
    /// </para>
    /// </remarks>
    private void ApplyCurvePreset(double exponent)
    {
        EditBrush(b => b with { Curve = new PressureCurve(0.0, 1.0, exponent) });
    }

    private void CurveSoft_Click(object? sender, RoutedEventArgs e) => ApplyCurvePreset(0.55);

    private void CurveDefault_Click(object? sender, RoutedEventArgs e) => ApplyCurvePreset(1.0);

    private void CurveHard_Click(object? sender, RoutedEventArgs e) => ApplyCurvePreset(2.2);

    /// <summary>
    /// Put the dot where the pen is on the curve, or take it away when the pen is off the tablet.
    /// </summary>
    /// <remarks>
    /// Drawn from the raw reading, because that is the axis the curve is drawn against: the dot
    /// sits at the pressure the pen reported, at the height the brush will use. Reading the
    /// processed value back would put the dot on the diagonal whatever the curve was doing.
    /// </remarks>
    private void ShowCurveDot(double rawPressure)
    {
        if (rawPressure <= 0 || !PlotIsLaidOut)
        {
            CurveDot.IsVisible = false;
            CurveDotDrop.IsVisible = false;
            return;
        }

        double x = Math.Clamp(rawPressure, 0, 1);
        double y = Math.Clamp(Brush.Curve.Apply(x), 0, 1);
        var p = CurvePoint(x, y);

        Canvas.SetLeft(CurveDot, p.X - CurveDot.Width / 2);
        Canvas.SetTop(CurveDot, p.Y - CurveDot.Height / 2);
        CurveDot.IsVisible = true;

        // Down to the axis, so the reading can be read off the bottom as well as seen on the curve.
        CurveDotDrop.StartPoint = p;
        CurveDotDrop.EndPoint = new Point(p.X, CurvePlot.Bounds.Height - CurvePad);
        CurveDotDrop.IsVisible = true;
    }

    /// <summary>Which node of the pressure curve something refers to.</summary>
    private enum CurveNode { None, Start, Bend, End }

    /// <summary>The margin between the edge of the plot and where 0 and 1 sit.</summary>
    /// <remarks>
    /// Room for a node sitting at either end. Without it, half of the node at the origin would be
    /// outside the border, and the half left inside is the half that cannot be grabbed.
    /// </remarks>
    private const double CurvePad = 6;

    /// <summary>False until the flyout has opened and the plot has been given a size.</summary>
    private bool PlotIsLaidOut => CurvePlot.Bounds.Width > 1 && CurvePlot.Bounds.Height > 1;

    /// <summary>Where a point on the unit square lands in the plot.</summary>
    private Point CurvePoint(double x, double y)
    {
        double w = CurvePlot.Bounds.Width, h = CurvePlot.Bounds.Height;

        return new Point(CurvePad + x * (w - 2 * CurvePad),
                         h - CurvePad - y * (h - 2 * CurvePad));
    }

    /// <summary>The reverse: where a point in the plot sits on the unit square.</summary>
    private (double X, double Y) CurveValue(Point at)
    {
        double w = CurvePlot.Bounds.Width, h = CurvePlot.Bounds.Height;

        return (Math.Clamp((at.X - CurvePad) / Math.Max(1, w - 2 * CurvePad), 0, 1),
                Math.Clamp((h - CurvePad - at.Y) / Math.Max(1, h - 2 * CurvePad), 0, 1));
    }

    /// <summary>Where a node sits on the unit square.</summary>
    /// <remarks>
    /// All three are points the curve itself passes through, which is the whole idea: the response
    /// starts at Start, reaches full at End, and halfway between the two it is at a half raised to
    /// the exponent. None of them is a handle floating beside the line.
    /// </remarks>
    private static (double X, double Y) NodeAt(PressureCurve curve, CurveNode node) => node switch
    {
        CurveNode.Start => (curve.Start, 0),
        CurveNode.End => (curve.End, 1),
        _ => ((curve.Start + curve.End) / 2, Math.Pow(0.5, curve.Exponent)),
    };

    /// <summary>What dragging one node to a point does to the curve.</summary>
    /// <remarks>
    /// <para>
    /// Start and End take the horizontal position and ignore the vertical, because that is what
    /// they are: where along the range of the pen the response begins, and where it tops out. They
    /// are kept from crossing. Meeting is allowed, and is the threshold brush the curve already
    /// defines: nothing below the point, full strength at it.
    /// </para>
    /// <para>
    /// The bend takes the vertical and ignores the horizontal. It rides the middle of the active
    /// range, where the output is a half raised to the exponent whatever that range is, so the
    /// exponent that puts it at a given height is the log of the height over the log of a half.
    /// Pulled up, the brush comes on early; pushed down, it holds off until the pen is leaned on.
    /// </para>
    /// </remarks>
    private static PressureCurve Dragged(PressureCurve curve, CurveNode node, double x, double y) =>
        node switch
        {
            CurveNode.Start => curve with { Start = Math.Min(x, curve.End) },
            CurveNode.End => curve with { End = Math.Max(x, curve.Start) },
            _ => curve with { Exponent = Math.Log(Math.Clamp(y, 0.02, 0.98)) / Math.Log(0.5) },
        };

    /// <summary>The node near enough to a point to have been meant by it, if any.</summary>
    /// <remarks>
    /// A press on empty plot takes hold of nothing. There is no reading of "somewhere in the
    /// middle" that is not a guess at which of three nodes was wanted, and a plot that jumps when
    /// it is clicked is worse than one that waits to be aimed at.
    /// </remarks>
    private CurveNode NodeNear(Point at)
    {
        var curve = Brush.Curve;
        var best = CurveNode.None;
        double nearest = 22;    // generous: the node is 13px across and a pen is not a mouse

        foreach (var node in new[] { CurveNode.Start, CurveNode.Bend, CurveNode.End })
        {
            // The bend rides the middle of the active range, and a range of no width has no
            // middle: that curve is a step, and the exponent does nothing to it.
            if (node == CurveNode.Bend && curve.End <= curve.Start) continue;

            var (x, y) = NodeAt(curve, node);
            var p = CurvePoint(x, y);
            double distance = Math.Sqrt((p.X - at.X) * (p.X - at.X) + (p.Y - at.Y) * (p.Y - at.Y));

            if (distance >= nearest) continue;

            nearest = distance;
            best = node;
        }

        return best;
    }

    private void CurvePlot_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!PlotIsLaidOut) return;

        _curveDrag = NodeNear(e.GetPosition(CurvePlot));
        if (_curveDrag == CurveNode.None) return;

        // Captured, so a drag that leaves the plot keeps going rather than stopping at the border
        // and stranding the node wherever the pointer happened to cross it.
        e.Pointer.Capture(CurvePlot);
        e.Handled = true;
    }

    private void CurvePlot_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_curveDrag == CurveNode.None || !PlotIsLaidOut) return;

        var (x, y) = CurveValue(e.GetPosition(CurvePlot));

        EditBrush(b => b with { Curve = Dragged(b.Curve, _curveDrag, x, y) });
        e.Handled = true;
    }

    /// <summary>Read a number out of a text box once the user has finished typing it.</summary>
    /// <remarks>
    /// On Enter and on losing focus, not on every keystroke: a box read as it is typed turns 0.8
    /// into 0 the moment the point is typed, and then fights the user for the rest of the number.
    /// Anything that will not parse is not an edit, and the box goes back to saying what the brush
    /// says, which is also what Escape does.
    /// </remarks>
    private void OnNumberBox(TextBox box, Func<double, BrushSettings, BrushSettings> apply)
    {
        void Commit()
        {
            if (double.TryParse(box.Text, out double value)) EditBrush(b => apply(value, b));
            else ShowBrush();
        }

        box.LostFocus += (_, _) => Commit();

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit();
            else if (e.Key == Key.Escape) ShowBrush();
            else return;

            e.Handled = true;
        };
    }

    private void DrawCurve(PressureCurve curve)
    {
        if (!PlotIsLaidOut) return;   // before the first layout pass

        double w = CurvePlot.Bounds.Width, h = CurvePlot.Bounds.Height;

        // Where a linear curve would run, for the shape to be read against.
        CurveDiagonal.StartPoint = new Point(CurvePad, h - CurvePad);
        CurveDiagonal.EndPoint = new Point(w - CurvePad, CurvePad);

        var points = new List<Point>();
        const int steps = 64;
        for (int i = 0; i <= steps; i++)
        {
            double x = (double)i / steps;
            points.Add(CurvePoint(x, curve.Apply(x)));
        }

        CurveLine.Points = points;

        Place(CurveStartNode, CurveNode.Start);
        Place(CurveEndNode, CurveNode.End);

        // No middle to a range of no width, so there is nothing there to grab and nothing it
        // would do if there were.
        CurveBendNode.IsVisible = curve.End > curve.Start;
        if (CurveBendNode.IsVisible) Place(CurveBendNode, CurveNode.Bend);

        void Place(Ellipse node, CurveNode which)
        {
            var (x, y) = NodeAt(curve, which);
            var p = CurvePoint(x, y);

            Canvas.SetLeft(node, p.X - node.Width / 2);
            Canvas.SetTop(node, p.Y - node.Height / 2);
        }
    }

    private void PopulateApis()
    {
        _apis = AvaloniaPenApis.GetAvailable();

        // Wintab's digitizer context where it exists: it is the finest of the available clocks
        // and the one a tablet actually reports through. Measured in WinPenKit -- one timestamp
        // per point at 1 ms, where the framework paths vary by four orders of magnitude.
        _api = _apis.FirstOrDefault(a => a == InputApi.WintabDigitizer,
                                    _apis.Count > 0 ? _apis[0] : default);

        if (_apis.Count == 0) _api = null;
    }

    /// <summary>Whether the frame loop is running.</summary>
    /// <remarks>
    /// The loop presents the document as well as draining the pen, so this is also the answer to
    /// "is the canvas being drawn at all".
    /// </remarks>
    internal bool IsPresenting => _renderTimer.IsEnabled;

/// <summary>What to pretend the tablet did, for a test.</summary>
    /// <remarks>
    /// There is no other way to reach the two failing paths from a test. Whether a driver exists
    /// and whether it hands over a context are both properties of the machine -- on the one this
    /// was written on, the second depends on whether another application happens to have the
    /// tablet open. What is being checked is the window's response, which is a decision in this
    /// file rather than a property of any driver.
    /// </remarks>
    internal enum PenForTest
    {
        /// <summary>Ask the machine, which is what the application does.</summary>
        Real,

        /// <summary>No driver at all.</summary>
        NoDriver,

        /// <summary>A driver that will not hand over a context.</summary>
        Refused,
    }

    internal PenForTest PenOutcomeForTest { get; set; } = PenForTest.Real;

    /// <summary>Which driver is being read, for a test that changes it.</summary>
    internal InputApi? ApiForTest => _api;

    /// <summary>
    /// How the user is told the tablet could not be opened, and asked what to do about it.
    /// </summary>
    /// <remarks>
    /// A seam because a test must not open a window that waits to be dismissed. The default is
    /// <see cref="PenProblemWindow"/>; what is worth testing is what the window does with each
    /// answer, which is a decision in this file.
    /// </remarks>
    internal Func<InputApi, string, Task<PenProblemChoice>> AskAboutPen { get; set; }

    /// <summary>True while the dialog is up, so a restart behind it does not stack a second.</summary>
    private bool _askingAboutPen;

    /// <summary>
    /// Open the pen session for the chosen API, or say why it could not be opened.
    /// </summary>
    /// <remarks>
    /// <b>The frame loop starts on every path out of here, including the ones that fail.</b> It
    /// used to start only on the last line, after two early returns -- one for having no driver at
    /// all and one for a driver that would not open -- and it is the loop that presents the
    /// document. So a tablet that could not be opened did not leave the application penless: it
    /// left the canvas blank, with the document never drawn and the failure explained in a status
    /// line at the bottom of an empty window. Found when Wintab refused a context because another
    /// application was holding the tablet.
    /// </remarks>
    private void StartSession()
    {
        // Stopped for the swap, so that no tick lands on a session being disposed.
        _renderTimer.Stop();

        _penSession?.Stop();
        _penSession?.Dispose();
        _penSession = null;
        _paint.EndStroke();

        // The forced outcomes are asked about first, and the refusal supplies its own driver.
        // Ordered the other way round, with the refusal reading _api, a machine that has no
        // tablet driver at all could not reach the refusal path: the "no driver" branch answered
        // for it and produced a different message. That is every build machine, which is where
        // this was found -- the test passed on a developer's machine and failed on CI, which is
        // the wrong way round for a test to behave.
        if (PenOutcomeForTest == PenForTest.Refused)
        {
            RefusePen(_api ?? InputApi.WintabDigitizer, "The pen session was refused.");
        }
        else if (PenOutcomeForTest == PenForTest.NoDriver || _api is not { } api)
        {
            RefusePen(InputApi.AvaloniaPointer, "No pen driver was found.");
        }
        else
        {
            var session = api == InputApi.AvaloniaPointer
                ? new AvaloniaPointerSession(PaintView.Host)
                : PenSessionFactory.Create(api);

            IntPtr hwnd = TryGetPlatformHandle() is { } handle ? handle.Handle : IntPtr.Zero;

            if (session.Start(hwnd) is { } error)
            {
                session.Dispose();
                RefusePen(api, error);
            }
            else
            {
                _penSession = session;
                StatusLabel.Text = api.Label();
            }
        }

        _renderTimer.Start();
    }

    /// <summary>What the status line last said about the pen, so it is written only on a change.</summary>
    private bool _penWasRunning = true;

    /// <summary>
    /// Say when the pen has gone, and say when it comes back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A context can be taken away underneath a running application -- restarting the tablet
    /// service does it -- and until this was added, nothing showed. The application stayed up with
    /// its status line still naming the driver and the pen simply stopped working, which reads as
    /// a broken canvas. The session recovers on its own within a few seconds, but a few seconds of
    /// a pen that does nothing is long enough to go looking in the wrong place.
    /// </para>
    /// <para>
    /// Not a dialog. The usual case is a blip that heals itself before anyone has finished reading
    /// a sentence, and interrupting the document for that would be worse than the silence it
    /// replaces. A dialog is for a session that never started at all.
    /// </para>
    /// <para>
    /// Written only when the answer changes, since this is asked on every frame -- and the line is
    /// shared with everything else the application says, so rewriting it sixty times a second
    /// would be the last word anybody ever saw.
    /// </para>
    /// </remarks>
    internal void ShowPenState(bool running)
    {
        if (running == _penWasRunning) return;

        _penWasRunning = running;

        StatusLabel.Text = running
            ? $"{_api?.Label() ?? "Pen"}: working again"
            : "The tablet driver took the pen away. Trying to get it back...";
    }

    /// <summary>Say that the tablet could not be opened, and what can be done about it.</summary>
    /// <remarks>
    /// The driver's own words plus a way out, because the words on their own are not actionable:
    /// what Wintab says is "Fallback context also failed to open", which names no cause and
    /// suggests no remedy. The usual cause is another application holding the tablet -- Clip
    /// Studio and Photoshop both take a Wintab context and keep it for as long as they are open --
    /// and the way out is to close that application or to read the tablet through Windows instead.
    /// Only for Wintab: Avalonia Pointer is the fallback, so pointing at it would be a loop.
    /// </remarks>
    private void RefusePen(InputApi api, string error)
    {
        // Kept, as the reminder after the dialog has been dismissed.
        StatusLabel.Text = api == InputApi.AvaloniaPointer
            ? error
            : $"{error}  Another application may be holding the tablet. " +
              "Tools > Options can read it through Avalonia Pointer instead.";

        // Posted rather than called. This runs from the window's Opened handler on the way up,
        // where there is not yet a window for a dialog to be modal to.
        Dispatcher.UIThread.Post(() => ReportPenProblem(api, error), DispatcherPriority.Background);
    }

    /// <summary>Say that the pen is not working, and do whatever is chosen about it.</summary>
    /// <remarks>
    /// <para>
    /// In a dialog rather than the status line, which is where this used to be said. A pen session
    /// that fails to open leaves a canvas that will not take a stroke, and that is
    /// indistinguishable from a broken renderer -- it was read as one both times it happened. A
    /// line at the bottom of the window is not where anyone looks when the thing in the middle
    /// appears not to work.
    /// </para>
    /// <para>
    /// The guard covers only the time the dialog is up. Choosing to try again restarts the
    /// session, and a restart that fails has to be able to say so.
    /// </para>
    /// </remarks>
    private async void ReportPenProblem(InputApi api, string error)
    {
        if (_askingAboutPen) return;
        _askingAboutPen = true;

        PenProblemChoice choice;
        try
        {
            choice = await AskAboutPen(api, error);
        }
        finally
        {
            _askingAboutPen = false;
        }

        switch (choice)
        {
            case PenProblemChoice.Retry:
                StartSession();
                break;

            case PenProblemChoice.UseFallback when _apis.Contains(InputApi.AvaloniaPointer):
                _api = InputApi.AvaloniaPointer;
                StartSession();
                break;
        }
    }

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        // Before the early return. The viewport repaints for reasons that have nothing to do with
        // the pen -- a zoom, a pan, the first layout pass -- and this method gives up as soon as
        // there is nothing to drain. It costs a comparison when nothing has changed.
        PaintView.PresentIfNeeded();

        // Also before it, and for the same reason: the dot has to go out when the pen leaves,
        // which is a tick with nothing on it.
        if (_pressureAge.ElapsedMilliseconds > 150) _livePressure = 0;
        ShowCurveDot(_livePressure);

        if (_penSession is null) return;

        var points = _penSession.DrainPoints();

        // After the drain, because the drain is what looks. Before the early return, because a
        // session whose context has been taken away delivers no points at all -- checking after
        // that return would be checking only while the pen was working.
        ShowPenState(_penSession.IsRunning);

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

            // Kept for the curve plot, which shows where the pen is on it. Recorded here, where
            // the reading is raw: this is the axis the curve is drawn against.
            _livePressure = pressure;
            _pressureAge.Restart();

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
