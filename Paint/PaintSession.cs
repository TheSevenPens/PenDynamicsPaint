using PenDynamicsPaint.Drawing;
using SkiaSharp;

namespace PenDynamicsPaint.Paint;

/// <summary>
/// The document: a stack of layers, the marks on them, and the composite the viewport draws.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <c>DrawingSession</c> in PenDynamicsLab, and deliberately not a reuse of it.
/// The difference is not a feature but the shape of the surface: <c>DrawSurface</c> sizes its bitmap
/// to the host control and grows it when the window grows, because in the stroke tabs the control
/// is the canvas. A document has a size of its own that no window can change, and the viewport
/// shows a region of it -- see <see cref="PaintViewport"/>.
/// </para>
/// <para>
/// <b><see cref="Bitmap"/> is a cache, not the document.</b> The document is <see cref="Layers"/>;
/// the bitmap is what they composite to, rebuilt on demand over the region that changed. Reading
/// the property is what brings it up to date, which is why the drawing code here uses the field.
/// </para>
/// <para>
/// One stack, not two. There is no raw comparison here, which is a decision rather than a
/// shortfall: with per-brush dynamics there would be no single processed stream for a raw one to
/// be compared against. That comparison is PenDynamicsLab's, and belongs there.
/// </para>
/// <para>
/// Its own history, so undo here and undo in the stroke tabs are separate stacks. Two renderers
/// with different notions of a stroke should not share one.
/// </para>
/// </remarks>
public sealed class PaintSession : IDisposable
{
    /// <summary>The paper. It is under the whole stack, and nothing can draw on it.</summary>
    /// <remarks>
    /// Not a layer, deliberately. A bottom layer that could be hidden, moved above another or
    /// painted on is a different thing from the ground the picture sits on, and making the paper
    /// one of the layers is how a stack ends up with a transparent hole nobody asked for.
    /// </remarks>
    private static readonly SKColor Paper = new(0xFF, 0xFF, 0xFF);

    /// <summary>Antialiasing puts ink just outside the geometry, so the stale region is grown.</summary>
    /// <remarks>
    /// One pixel would do for a round cap. Two costs nothing measurable and leaves room for a
    /// brush whose marks reach slightly further than its nominal width.
    /// </remarks>
    private const int DirtyMargin = 2;

    private readonly List<Layer> _layers = [];
    private int _activeIndex;
    private int _nextLayerId = 1;

    private IBrushEngine _engine;
    private readonly SKBitmap _bitmap;
    private readonly SKCanvas _canvas;
    private StrokeSample? _lastSample;
    private SKColor _strokeColor = new(0x1A, 0x1A, 0x2E);

    // The stroke in progress, when compositing is Wash. A layer like any other -- transparent,
    // document-sized -- except that it is transient and sits directly above the layer being drawn
    // on rather than in the stack.
    private Layer? _strokeLayer;
    private bool _layerActive;

    /// <summary>The region of the composite that no longer matches the stack.</summary>
    /// <remarks>
    /// Empty means it is current. This is what keeps a stroke cheap: recompositing a 1500 x 1000
    /// document over three layers is about 18 MB of blitting, which will not fit in a frame, while
    /// recompositing the hundred or so pixels the pen moved through since the last one is nothing.
    /// </remarks>
    private SKRectI _stale;

    /// <summary>How the marks within a stroke combine with each other.</summary>
    /// <remarks>
    /// Takes effect at the start of the next stroke rather than mid-stroke, since a stroke already
    /// half composited one way cannot finish the other.
    /// </remarks>
    public StrokeCompositing Compositing { get; set; } = StrokeCompositing.Wash;

    /// <summary>Document width in document units, which are its pixels at 100%.</summary>
    public int Width { get; }

    /// <inheritdoc cref="Width"/>
    public int Height { get; }

    /// <summary>Set when the picture has changed since the presenter last drew it.</summary>
    public bool IsDirty { get; private set; } = true;

    /// <summary>What has been drawn, as strokes, each recording the layer it went to.</summary>
    public StrokeHistory History { get; } = new();

    /// <summary>The stack, bottom first. Never empty.</summary>
    /// <remarks>
    /// Bottom first because that is composite order, and the order the code here iterates in. The
    /// panel shows the reverse, which is what every paint application does and what people expect
    /// -- the reversal lives in the UI, where it can be got wrong in one place instead of several.
    /// </remarks>
    public IReadOnlyList<Layer> Layers => _layers;

    /// <summary>Where marks go. Never null.</summary>
    public Layer ActiveLayer => _layers[_activeIndex];

    /// <summary>Index of <see cref="ActiveLayer"/> in <see cref="Layers"/>.</summary>
    public int ActiveLayerIndex => _activeIndex;

    public PaintSession(int width, int height, IBrushEngine? engine = null)
    {
        _engine = engine ?? new RoundBrushEngine();
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        _bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _canvas = new SKCanvas(_bitmap);

        _layers.Add(new Layer(_nextLayerId++, "Layer 1", Width, Height));
        InvalidateComposite();
    }

    /// <summary>
    /// The composited picture, for the presenter to draw and for export.
    /// </summary>
    /// <remarks>
    /// Reading this recomposites whatever has gone stale since the last read, so a caller that
    /// draws a stroke and then reads a pixel sees the stroke. The work is proportional to what
    /// changed, not to the document.
    /// </remarks>
    public SKBitmap Bitmap
    {
        get
        {
            Composite();
            return _bitmap;
        }
    }

    /// <summary>Marks the picture as presented. The presenter calls this after drawing it.</summary>
    public void ClearDirty() => IsDirty = false;

    /// <summary>
    /// Declare the whole composite out of date.
    /// </summary>
    /// <remarks>
    /// For changes this class cannot attribute to a region -- a layer appearing, moving, being
    /// hidden or changing opacity. Every one of those can alter any pixel of the document.
    /// </remarks>
    public void InvalidateComposite() => MarkStale(SKRectI.Create(Width, Height));

    // -- Layers ---------------------------------------------------

    /// <summary>Add a transparent layer directly above the active one, and make it active.</summary>
    /// <remarks>
    /// Above the active layer rather than on top of the stack, which is where a paint application
    /// puts it and what makes adding one mid-picture useful.
    /// </remarks>
    public Layer AddLayer(string? name = null)
    {
        EndStroke();

        var layer = new Layer(_nextLayerId, name ?? $"Layer {_nextLayerId}", Width, Height);
        _nextLayerId++;

        _layers.Insert(_activeIndex + 1, layer);
        _activeIndex++;
        InvalidateComposite();
        return layer;
    }

    /// <summary>
    /// Remove a layer and everything on it. Refuses to remove the last one.
    /// </summary>
    /// <remarks>
    /// <b>Not undoable</b>, and its strokes go with it. Undo here steps back through strokes, not
    /// through every command; making layer operations reversible means a command stack, which is
    /// its own piece of work. Dropping the strokes is what keeps undo honest in the meantime --
    /// see <see cref="StrokeHistory.RemoveForLayer"/>.
    /// </remarks>
    public bool RemoveLayer(int index)
    {
        if (_layers.Count <= 1 || index < 0 || index >= _layers.Count) return false;

        EndStroke();

        var layer = _layers[index];
        History.RemoveForLayer(layer.Id);
        _layers.RemoveAt(index);
        layer.Dispose();

        if (_activeIndex >= _layers.Count) _activeIndex = _layers.Count - 1;
        InvalidateComposite();
        return true;
    }

    /// <summary>Move a layer to another position in the stack, keeping it active.</summary>
    public bool MoveLayer(int from, int to)
    {
        if (from < 0 || from >= _layers.Count) return false;
        if (to < 0 || to >= _layers.Count || to == from) return false;

        EndStroke();

        var layer = _layers[from];
        _layers.RemoveAt(from);
        _layers.Insert(to, layer);
        _activeIndex = _layers.IndexOf(layer);
        InvalidateComposite();
        return true;
    }

    /// <summary>
    /// Composite a layer down onto the one beneath it and remove it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Undo cannot cross this.</b> The merged pixels become the lower layer's replay baseline
    /// and both layers' strokes are dropped, because a replay that interleaved them in the order
    /// they were drawn would not reproduce the merge -- all of the upper layer goes over all of the
    /// lower one, whatever order the marks were made in. Reproducing that through history would
    /// mean reordering strokes into a sequence the user never performed, and an undo stepping
    /// through it would step somewhere they had never been.
    /// </para>
    /// <para>
    /// Destructive, in other words, which is what merge down is in most applications that have it.
    /// A non-destructive version is a different feature: a group, not a merge.
    /// </para>
    /// </remarks>
    public bool MergeDown(int index)
    {
        if (index <= 0 || index >= _layers.Count) return false;

        EndStroke();

        var upper = _layers[index];
        var lower = _layers[index - 1];

        // Through DrawOnto, so a hidden or translucent layer merges down as it looked.
        upper.DrawOnto(lower.Canvas);

        History.RemoveForLayer(upper.Id);
        History.RemoveForLayer(lower.Id);
        lower.BakeAsBaseline();

        _layers.RemoveAt(index);
        upper.Dispose();

        _activeIndex = index - 1;
        InvalidateComposite();
        return true;
    }

    /// <summary>Choose which layer marks go to.</summary>
    public bool SetActiveLayer(int index)
    {
        if (index < 0 || index >= _layers.Count || index == _activeIndex) return false;

        // A stroke belongs to the layer it started on; letting it finish elsewhere would put half
        // of it on each and leave history describing neither.
        EndStroke();

        _activeIndex = index;
        return true;
    }

    /// <summary>Show or hide a layer.</summary>
    public bool SetLayerVisible(int index, bool visible)
    {
        if (index < 0 || index >= _layers.Count) return false;
        if (_layers[index].IsVisible == visible) return false;

        EndStroke();
        _layers[index].IsVisible = visible;
        InvalidateComposite();
        return true;
    }

    /// <summary>Set how much of a layer reaches the composite, 0 to 1.</summary>
    public bool SetLayerOpacity(int index, double opacity)
    {
        if (index < 0 || index >= _layers.Count) return false;

        opacity = Math.Clamp(opacity, 0, 1);
        if (_layers[index].Opacity == opacity) return false;

        _layers[index].Opacity = opacity;
        InvalidateComposite();
        return true;
    }

    /// <summary>Rename a layer. Nothing but the panel reads the name.</summary>
    public bool RenameLayer(int index, string name)
    {
        if (index < 0 || index >= _layers.Count || string.IsNullOrWhiteSpace(name)) return false;
        _layers[index].Name = name.Trim();
        return true;
    }

    // -- Drawing --------------------------------------------------

    /// <summary>
    /// Add one pen sample, positioned in <b>document</b> coordinates.
    /// </summary>
    /// <remarks>
    /// The caller has already been through <see cref="PaintViewport.ToDocument"/>. Nothing below
    /// this point knows about zoom or pan, which is the point of doing the mapping at the edge:
    /// a stroke is a set of document positions whatever the view was doing while it was drawn.
    /// </remarks>
    public void AddSample(double documentX, double documentY, double rawPressure,
                          double processedPressure, BrushSettings brush,
                          PenOrientation orientation = default, long timestampMicroseconds = 0)
    {
        if (rawPressure <= 0)
        {
            EndStroke();
            return;
        }

        // A hidden layer cannot be drawn on. Letting the marks land invisibly would be worse than
        // refusing them: the ink really would be there, and it would appear the moment the layer
        // was shown.
        if (!ActiveLayer.IsVisible) return;

        if (_lastSample is null)
        {
            History.BeginStroke(brush, _strokeColor, ActiveLayer.Id);
            BeginLayerIfWashing();
            _engine.BeginStroke();
        }

        var sample = new StrokeSample(new global::Avalonia.Point(documentX, documentY),
                                      rawPressure, orientation, processedPressure,
                                      timestampMicroseconds);
        History.AddSample(sample);

        if (_lastSample is { } from && (brush.DrawAtZeroPressure || processedPressure > 0))
        {
            var target = _layerActive ? _strokeLayer!.Canvas : ActiveLayer.Canvas;
            _engine.DrawSegment(target, from, sample, brush, _strokeColor, PressureChannel.Processed);
            MarkStale(SegmentBounds(from, sample, brush));
        }

        _lastSample = sample;
    }

    /// <summary>End the stroke in progress, leaving what it drew.</summary>
    public void EndStroke()
    {
        if (_lastSample is null) return;   // guarded, so the engine's brackets stay in pairs
        _lastSample = null;
        _engine.EndStroke();
        History.EndStroke();
        MergeStrokeLayer();
    }

    /// <summary>Start a fresh stroke layer, if this stroke is being washed.</summary>
    private void BeginLayerIfWashing()
    {
        if (Compositing != StrokeCompositing.Wash) return;

        // No blender means this build of Skia would not compile it. Falling back to direct
        // painting keeps the application drawing, with the artifact Wash exists to remove.
        if (AlphaDarken.Blender is not { } blender) return;

        // Id 0: it is never in the stack, so nothing looks it up and no stroke records it.
        _strokeLayer ??= new Layer(0, "stroke", Width, Height);
        _strokeLayer.Canvas.Clear(SKColors.Transparent);

        _engine.Blender = blender;
        _layerActive = true;
    }

    /// <summary>
    /// Composite the finished stroke onto the layer it was drawn for, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single blend is the whole point. Within the stroke layer the marks took the greater
    /// alpha of any overlap, so each point carries the opacity its own pressure asked for; laying
    /// that down in one pass is what stops a hundred overlapping segments turning 15% into 99%.
    /// </para>
    /// <para>
    /// Nothing is marked stale. The composite has been drawing the stroke layer directly above
    /// this layer with ordinary source-over, and source-over is associative, so the picture after
    /// the merge is the picture that was already on screen.
    /// </para>
    /// </remarks>
    private void MergeStrokeLayer()
    {
        if (!_layerActive || _strokeLayer is null) return;

        _strokeLayer.DrawContentOnto(ActiveLayer.Canvas);

        _engine.Blender = null;
        _layerActive = false;
    }

    /// <summary>
    /// What region one segment can put ink in, as a bound rather than an exact answer.
    /// </summary>
    /// <remarks>
    /// The two endpoints, grown by the larger of the two half-widths and the antialiasing margin.
    /// Every engine here places marks between the endpoints at a pressure between the two, so the
    /// larger half-width bounds all of them. <b>Understating this leaves stale pixels in the
    /// composite</b>, which is why <c>LayerCompositingTests</c> compares incremental compositing
    /// against a full recomposite rather than trusting the arithmetic.
    /// </remarks>
    private static SKRect SegmentBounds(in StrokeSample from, in StrokeSample to, BrushSettings brush)
    {
        float radius = Math.Max(brush.StrokeWidthFor(from.ProcessedPressure),
                                brush.StrokeWidthFor(to.ProcessedPressure)) / 2f;

        return new SKRect(
            (float)Math.Min(from.Position.X, to.Position.X) - radius,
            (float)Math.Min(from.Position.Y, to.Position.Y) - radius,
            (float)Math.Max(from.Position.X, to.Position.X) + radius,
            (float)Math.Max(from.Position.Y, to.Position.Y) + radius);
    }

    private void MarkStale(SKRect region)
    {
        var grown = SKRectI.Round(region);
        grown.Inflate(DirtyMargin, DirtyMargin);
        MarkStale(grown);
    }

    private void MarkStale(SKRectI region)
    {
        if (!region.IntersectsWith(SKRectI.Create(Width, Height))) return;

        _stale = _stale.IsEmpty ? region : SKRectI.Union(_stale, region);
        IsDirty = true;
    }

    /// <summary>
    /// Bring the stale region of the composite back in line with the stack.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clipped to the stale region, so the cost follows what changed. <c>SKCanvas.Clear</c> fills
    /// the current clip rather than the whole surface, which is what makes repainting the paper
    /// correct here -- <c>LayerCompositingTests</c> pins that rather than assuming it.
    /// </para>
    /// <para>
    /// The stroke in progress is drawn <b>inside</b> its layer's group rather than over the whole
    /// stack. It belongs to that layer, so the layer's opacity applies to the two together and
    /// anything above still covers it. Drawing it last, over everything, is the easy version and
    /// is wrong in both ways: a stroke on a 50% layer would look full strength until the pen
    /// lifted, and a stroke on the bottom layer would appear to be on the top one.
    /// </para>
    /// </remarks>
    private void Composite()
    {
        if (_stale.IsEmpty) return;

        _canvas.Save();
        _canvas.ClipRect(SKRect.Create(_stale.Left, _stale.Top, _stale.Width, _stale.Height));
        _canvas.Clear(Paper);

        foreach (var layer in _layers)
        {
            bool washing = _layerActive && _strokeLayer is not null
                                        && ReferenceEquals(layer, ActiveLayer);
            if (!washing)
            {
                layer.DrawOnto(_canvas);
                continue;
            }

            if (!layer.IsVisible || layer.Opacity <= 0) continue;

            if (layer.Opacity >= 1)
            {
                layer.DrawContentOnto(_canvas);
                _strokeLayer!.DrawContentOnto(_canvas);
                continue;
            }

            // One group, one opacity: the layer and the stroke it is receiving fade together.
            using var group = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)Math.Clamp(layer.Opacity * 255, 0, 255)),
            };
            _canvas.SaveLayer(group);
            layer.DrawContentOnto(_canvas);
            _strokeLayer!.DrawContentOnto(_canvas);
            _canvas.Restore();
        }

        _canvas.Restore();
        _stale = SKRectI.Empty;
    }

    // -- History --------------------------------------------------

    /// <summary>Remove the last stroke and redraw the layer it was on.</summary>
    /// <remarks>
    /// Only that layer. Undo is chronological across the document -- the last stroke drawn is the
    /// one that goes, wherever it was drawn -- but nothing on the other layers changed, so
    /// replaying them would be work with no effect.
    /// </remarks>
    public bool Undo()
    {
        EndStroke();

        if (History.Strokes.Count == 0) return false;

        int layerId = History.Strokes[^1].LayerId;
        if (!History.RemoveLast()) return false;

        RepaintLayer(layerId);
        InvalidateComposite();
        return true;
    }

    /// <summary>Empty every layer and forget the strokes. The stack itself is kept.</summary>
    public void Clear()
    {
        EndStroke();
        History.Clear();

        // Not ResetToBaseline: clearing the document means clearing it, including whatever a merge
        // baked in. Keeping the baseline would leave marks behind that nothing could then remove.
        foreach (var layer in _layers) layer.Canvas.Clear(SKColors.Transparent);

        InvalidateComposite();
    }

    /// <summary>Set the colour subsequent strokes are drawn in.</summary>
    public void SetStrokeColor(SKColor color) => _strokeColor = color;

    /// <summary>
    /// Draw subsequent strokes with a different engine. The document is kept; the previous engine
    /// is disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any stroke in progress is ended first, since one already half drawn by a swept taper cannot
    /// be finished by stamped dabs.
    /// </para>
    /// <para>
    /// <b>A stroke does not record which engine drew it</b>, so an undo replays everything with
    /// whichever engine is current. Switching engine and then undoing therefore redraws the older
    /// strokes in the new engine's style. That is a real limitation rather than an oversight: it
    /// waits on strokes carrying their own brush definition, which is where per-brush settings are
    /// heading anyway.
    /// </para>
    /// </remarks>
    public void UseEngine(IBrushEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (ReferenceEquals(engine, _engine)) return;

        EndStroke();

        var old = _engine;
        _engine = engine;
        old.Dispose();
    }

    /// <summary>Clear one layer to its baseline and replay the strokes that belong to it.</summary>
    private void RepaintLayer(int layerId)
    {
        var layer = _layers.FirstOrDefault(l => l.Id == layerId);
        if (layer is null) return;   // its layer is gone, and its strokes went with it

        layer.ResetToBaseline();

        foreach (var stroke in History.Strokes)
            if (stroke.LayerId == layerId)
                Replay(stroke, layer);
    }

    /// <summary>
    /// Redraw one recorded stroke onto its layer, through the same compositing it was drawn with.
    /// </summary>
    /// <remarks>
    /// A washed stroke has to be replayed washed. Replaying it directly would let its overlaps
    /// accumulate, so an undo would change the appearance of every stroke that survived it --
    /// which is the kind of fault that looks like a rendering bug and is really a bookkeeping one.
    /// </remarks>
    private void Replay(Stroke stroke, Layer layer)
    {
        BeginLayerIfWashing();
        _engine.BeginStroke();

        var target = _layerActive ? _strokeLayer!.Canvas : layer.Canvas;
        var samples = stroke.Samples;
        for (int i = 1; i < samples.Count; i++)
        {
            if (!stroke.Brush.DrawAtZeroPressure && samples[i].ProcessedPressure <= 0) continue;
            _engine.DrawSegment(target, samples[i - 1], samples[i],
                                stroke.Brush, stroke.Color, PressureChannel.Processed);
        }

        _engine.EndStroke();

        // Not MergeStrokeLayer: that merges onto the active layer, and a replay is redrawing
        // whichever layer the stroke belonged to, which need not be the one in front of the pen.
        if (_layerActive && _strokeLayer is not null)
        {
            _strokeLayer.DrawContentOnto(layer.Canvas);
            _engine.Blender = null;
            _layerActive = false;
        }
    }

    public void Dispose()
    {
        _strokeLayer?.Dispose();
        foreach (var layer in _layers) layer.Dispose();
        _canvas.Dispose();
        _bitmap.Dispose();
        _engine.Dispose();
    }
}
