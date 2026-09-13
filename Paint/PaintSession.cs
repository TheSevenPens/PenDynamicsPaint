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

    /// <summary>One engine per kind, kept for the life of the session.</summary>
    /// <remarks>
    /// An engine is a renderer, not a setting: it holds a paint, a path and per-stroke
    /// accumulators, and every brush of a given kind can share one because only one stroke is ever
    /// being drawn or replayed at a time. What differs between brushes lives on
    /// <see cref="BrushSettings"/> and arrives per segment.
    /// </remarks>
    private readonly Dictionary<BrushEngineKind, IBrushEngine> _engines = [];

    private readonly SKBitmap _bitmap;
    private readonly SKCanvas _canvas;
    private StrokeSample? _lastSample;
    private SKColor _strokeColor = new(0x1A, 0x1A, 0x2E);

    /// <summary>The brush the stroke in progress is committed to, or null between strokes.</summary>
    /// <remarks>
    /// Captured at the first sample and used for the rest of the stroke, so changing the brush
    /// while the pen is down does not change the stroke under it. Without this the marks would
    /// follow the picker while the recorded stroke kept the brush it started with, and an undo
    /// would redraw something that had never been on the canvas.
    /// </remarks>
    private BrushSettings? _strokeBrush;

    /// <summary>The engine drawing the stroke in progress. Follows <see cref="_strokeBrush"/>.</summary>
    private IBrushEngine? _strokeEngine;

    /// <summary>
    /// The path filter, used both live and on replay.
    /// </summary>
    /// <remarks>
    /// One instance, reset per stroke. Replay runs it again from the recorded raw samples rather
    /// than storing what it produced, so a stroke keeps the pen's own path and still redraws
    /// exactly: the filter is deterministic and the stroke records the settings it ran under.
    /// </remarks>
    private readonly PathSmoother _smoother = new();

    /// <summary>
    /// Fits a curve through the filtered samples, used both live and on replay.
    /// </summary>
    /// <remarks>
    /// Downstream of the filter: smoothing decides where the samples are, this decides the path
    /// between them. Reset per stroke, like the filter, and deterministic for the same reason --
    /// a replay has to land where the stroke did.
    /// </remarks>
    private readonly CurveFitter _fitter = new();

    /// <summary>The last position the filter produced, which is where ink actually went.</summary>
    private StrokeSample? _lastDrawn;

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

    public PaintSession(int width, int height)
    {
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

    /// <summary>The engine for a brush, made on first use and kept.</summary>
    private IBrushEngine EngineFor(BrushSettings brush)
    {
        if (_engines.TryGetValue(brush.Engine, out var engine)) return engine;

        engine = brush.Engine switch
        {
            BrushEngineKind.Dabs => new DabBrushEngine(),
            _ => new RoundBrushEngine(),
        };

        _engines[brush.Engine] = engine;
        return engine;
    }

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
    /// <param name="brush">
    /// The brush to start a stroke with. Read only at the first sample: the rest of the stroke uses
    /// the copy taken then, so changing brush mid-gesture takes effect on the next stroke.
    /// </param>
    public void AddSample(double documentX, double documentY, double pressure, BrushSettings brush,
                          PenOrientation orientation = default, long timestampMicroseconds = 0)
    {
        if (pressure <= 0)
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
            _strokeBrush = brush;
            _strokeEngine = EngineFor(brush);
            _smoother.Reset();
            _fitter.Reset();
            History.BeginStroke(brush, _strokeColor, ActiveLayer.Id);
            BeginLayerIfWashing(_strokeEngine);
            _strokeEngine.BeginStroke();
        }

        var active = _strokeBrush!;

        // Recorded as the pen reported it. The filtered form is worked out below and not kept:
        // the document holds the pen's path, and replay runs the filter again.
        var sample = new StrokeSample(new global::Avalonia.Point(documentX, documentY),
                                      pressure, orientation, active.Process(pressure),
                                      timestampMicroseconds);
        History.AddSample(sample);

        var target = _layerActive ? _strokeLayer!.Canvas : ActiveLayer.Canvas;
        foreach (var point in _fitter.Next(Filter(sample, active), active.Interpolation))
            DrawTo(target, point, active);

        _lastSample = sample;
    }

    /// <summary>
    /// Draw from wherever the ink last reached to one more point along the path.
    /// </summary>
    /// <remarks>
    /// The one place a mark is made while a stroke is live. What arrives here has been through the
    /// filter and the curve fitter, so it is a point on the painted path rather than a pen sample:
    /// there may be many of them between two samples, or none at all.
    /// </remarks>
    private void DrawTo(SKCanvas target, in StrokeSample point, BrushSettings brush)
    {
        if (_lastDrawn is { } from && (brush.DrawAtZeroPressure || point.ProcessedPressure > 0))
        {
            _strokeEngine!.DrawSegment(target, from, point, brush, _strokeColor,
                                       PressureChannel.Processed);
            MarkStale(SegmentBounds(from, point, brush));
        }

        _lastDrawn = point;
    }

    /// <summary>
    /// The sample the engine should draw: the filtered path, then the brush's reading of it.
    /// </summary>
    /// <remarks>
    /// The order matters and is Krita's. Filtering steadies what the pen reported; the curve is
    /// the brush's response to it. Curving first and filtering after would smooth the brush's
    /// output rather than the hand's input, so a brush with a steep curve would be filtered harder
    /// than a gentle one holding the same pen.
    /// </remarks>
    private StrokeSample Filter(in StrokeSample sample, BrushSettings brush)
    {
        if (!brush.Smoothing.IsEnabled) return sample;

        var filtered = _smoother.Next(sample, brush.Smoothing);
        return sample with
        {
            Position = filtered.Position,
            RawPressure = filtered.RawPressure,
            ProcessedPressure = brush.Process(filtered.RawPressure),
        };
    }

    /// <summary>End the stroke in progress, leaving what it drew.</summary>
    public void EndStroke()
    {
        if (_lastSample is null) return;   // guarded, so the engine's brackets stay in pairs
        _lastSample = null;

        // The fitter holds the last segment back until it can see a tangent for it, so without
        // this every curved stroke would stop one sample short of where the pen lifted.
        if (_strokeBrush is { } finishing && _strokeEngine is not null)
        {
            var target = _layerActive ? _strokeLayer!.Canvas : ActiveLayer.Canvas;
            foreach (var point in _fitter.Flush(finishing.Interpolation))
                DrawTo(target, point, finishing);
        }

        _strokeEngine?.EndStroke();
        History.EndStroke();
        MergeStrokeLayer(_strokeEngine);
        BakeStrokesOverCap();

        _strokeBrush = null;
        _strokeEngine = null;
        _lastDrawn = null;
    }

    /// <summary>Start a fresh stroke layer, if this stroke is being washed.</summary>
    private void BeginLayerIfWashing(IBrushEngine engine)
    {
        if (Compositing != StrokeCompositing.Wash) return;

        // No blender means this build of Skia would not compile it. Falling back to direct
        // painting keeps the application drawing, with the artifact Wash exists to remove.
        if (AlphaDarken.Blender is not { } blender) return;

        // Id 0: it is never in the stack, so nothing looks it up and no stroke records it.
        _strokeLayer ??= new Layer(0, "stroke", Width, Height);
        _strokeLayer.Canvas.Clear(SKColors.Transparent);

        engine.Blender = blender;
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
    private void MergeStrokeLayer(IBrushEngine? engine)
    {
        if (!_layerActive || _strokeLayer is null) return;

        _strokeLayer.DrawContentOnto(ActiveLayer.Canvas);

        if (engine is not null) engine.Blender = null;
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

        // Both the pixels and the baseline. Wiping only what is on screen leaves the baseline
        // holding whatever was baked into it, and the next undo resets to that -- so work the user
        // cleared reappears on its own.
        foreach (var layer in _layers) layer.ClearEverything();

        InvalidateComposite();
    }

    /// <summary>Set the colour subsequent strokes are drawn in.</summary>
    public void SetStrokeColor(SKColor color) => _strokeColor = color;

    /// <summary>
    /// Drop the oldest strokes once the history is over its caps, keeping what they drew.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caps bound a long session: a stroke count alone does not, since one continuous stroke
    /// can run for minutes at tablet report rates. Until this was wired up they were dead, and the
    /// history grew for the life of the session.
    /// </para>
    /// <para>
    /// <b>An evicted stroke is drawn onto its layer's baseline, not merely forgotten.</b> Undo
    /// works by clearing a layer to its baseline and replaying what is retained, so a stroke
    /// dropped from the history without that would vanish from the canvas on the next undo --
    /// silently destroying work the user can still see. <c>EvictOldestIfOverCap</c> hands the
    /// stroke back rather than discarding it for exactly this reason.
    /// </para>
    /// <para>
    /// Drawn onto the baseline rather than baking the whole layer, which would also bake the
    /// strokes still in the history and leave replay drawing them a second time.
    /// </para>
    /// </remarks>
    private void BakeStrokesOverCap()
    {
        while (History.EvictOldestIfOverCap() is { } evicted)
        {
            var layer = _layers.FirstOrDefault(l => l.Id == evicted.LayerId);

            // Its layer is gone, so its pixels went with it and there is nothing to preserve.
            if (layer is null) continue;

            Replay(evicted, layer.BaselineCanvas);
        }
    }

    /// <summary>Clear one layer to its baseline and replay the strokes that belong to it.</summary>
    private void RepaintLayer(int layerId)
    {
        var layer = _layers.FirstOrDefault(l => l.Id == layerId);
        if (layer is null) return;   // its layer is gone, and its strokes went with it

        layer.ResetToBaseline();

        foreach (var stroke in History.Strokes)
            if (stroke.LayerId == layerId)
                Replay(stroke, layer.Canvas);
    }

    /// <summary>
    /// Redraw one recorded stroke onto its layer, through the same compositing it was drawn with.
    /// </summary>
    /// <remarks>
    /// A washed stroke has to be replayed washed. Replaying it directly would let its overlaps
    /// accumulate, so an undo would change the appearance of every stroke that survived it --
    /// which is the kind of fault that looks like a rendering bug and is really a bookkeeping one.
    /// </remarks>
    private void Replay(Stroke stroke, SKCanvas destination)
    {
        // The stroke's own brush, so its engine, size, spacing and curve are the ones it was drawn
        // with. Replaying through whatever is selected now is how an undo used to redraw older
        // strokes in a brush they were never made with.
        var engine = EngineFor(stroke.Brush);

        BeginLayerIfWashing(engine);
        engine.BeginStroke();
        _smoother.Reset();
        _fitter.Reset();

        var target = _layerActive ? _strokeLayer!.Canvas : destination;

        // Filtered and fitted again from the raw samples, through this stroke's own brush. Both
        // stages are deterministic, so the ink lands exactly where it did the first time; running
        // either with whatever is selected now would move ink already on the canvas.
        StrokeSample? previous = null;

        void Draw(StrokeSample point)
        {
            if (previous is { } from &&
                (stroke.Brush.DrawAtZeroPressure || point.ProcessedPressure > 0))
            {
                engine.DrawSegment(target, from, point,
                                   stroke.Brush, stroke.Color, PressureChannel.Processed);
            }

            previous = point;
        }

        foreach (var sample in stroke.Samples)
            foreach (var point in _fitter.Next(Filter(sample, stroke.Brush),
                                               stroke.Brush.Interpolation))
                Draw(point);

        foreach (var point in _fitter.Flush(stroke.Brush.Interpolation)) Draw(point);

        engine.EndStroke();

        // Not MergeStrokeLayer: that merges onto the active layer, and a replay is redrawing
        // whichever surface was asked for, which need not be the one in front of the pen.
        if (_layerActive && _strokeLayer is not null)
        {
            _strokeLayer.DrawContentOnto(destination);
            engine.Blender = null;
            _layerActive = false;
        }
    }

    public void Dispose()
    {
        _strokeLayer?.Dispose();
        foreach (var layer in _layers) layer.Dispose();
        _canvas.Dispose();
        _bitmap.Dispose();
        foreach (var engine in _engines.Values) engine.Dispose();
    }
}
