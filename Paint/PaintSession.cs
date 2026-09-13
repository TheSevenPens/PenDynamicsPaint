using PenDynamicsPaint.Drawing;
using SkiaSharp;

namespace PenDynamicsPaint.Paint;

/// <summary>
/// The Paint tab's document and the marks on it.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="DrawingSession"/>, and deliberately not a reuse of it. The
/// difference is not a feature but the shape of the surface: <c>DrawSurface</c> sizes its bitmap to
/// the host control and grows it when the window grows, because in the stroke tabs the control is
/// the canvas. A document has a size of its own that no window can change, and the viewport shows a
/// region of it -- see <see cref="PaintViewport"/>.
/// </para>
/// <para>
/// One surface, not two. There is no raw comparison here, which is a decision rather than a
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
    /// <summary>The paper. Everything outside a mark stays this colour.</summary>
    private static readonly SKColor Paper = new(0xFF, 0xFF, 0xFF);

    private readonly IBrushEngine _engine;
    private SKBitmap _bitmap;
    private SKCanvas _canvas;
    private StrokeSample? _lastSample;
    private SKColor _strokeColor = new(0x1A, 0x1A, 0x2E);

    // The stroke in progress, when compositing is Wash. Document-sized and transparent: marks go
    // here with alpha-darken so overlaps take the greater alpha, and it merges onto the document
    // once when the stroke ends.
    private SKBitmap? _strokeLayer;
    private SKCanvas? _strokeCanvas;
    private bool _layerActive;

    /// <summary>How the marks within a stroke combine with each other.</summary>
    /// <remarks>
    /// Takes effect at the start of the next stroke rather than mid-stroke, since a stroke already
    /// half composited one way cannot finish the other.
    /// </remarks>
    public StrokeCompositing Compositing { get; set; } = StrokeCompositing.Wash;

    /// <summary>
    /// The stroke in progress, for the presenter to draw over the document, or null when there is
    /// none or it is being painted directly.
    /// </summary>
    /// <remarks>
    /// A Wash stroke does not reach the document until it ends, so without this the ink would
    /// appear only when the pen lifted. Krita has the same problem and solves it the same way: the
    /// temporary device is composited into what you see while the stroke is live.
    /// </remarks>
    public SKBitmap? ActiveStrokeLayer => _layerActive ? _strokeLayer : null;

    /// <summary>Document width in document units, which are its pixels at 100%.</summary>
    public int Width { get; private set; }

    /// <inheritdoc cref="Width"/>
    public int Height { get; private set; }

    /// <summary>Set when the document has changed since the presenter last drew it.</summary>
    public bool IsDirty { get; private set; } = true;

    /// <summary>What has been drawn, as strokes. Separate from the stroke tabs' history.</summary>
    public StrokeHistory History { get; } = new();

    public PaintSession(int width, int height, IBrushEngine? engine = null)
    {
        _engine = engine ?? new RoundBrushEngine();
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _canvas = new SKCanvas(_bitmap);
        _canvas.Clear(Paper);
    }

    /// <summary>The document's pixels, for the presenter to draw and for export.</summary>
    public SKBitmap Bitmap => _bitmap;

    /// <summary>Marks the document as presented. The presenter calls this after drawing it.</summary>
    public void ClearDirty() => IsDirty = false;

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

        if (_lastSample is null)
        {
            History.BeginStroke(brush, _strokeColor);
            BeginLayerIfWashing();
            _engine.BeginStroke();
        }

        var sample = new StrokeSample(new global::Avalonia.Point(documentX, documentY),
                                      rawPressure, orientation, processedPressure,
                                      timestampMicroseconds);
        History.AddSample(sample);

        if (_lastSample is { } from && (brush.DrawAtZeroPressure || processedPressure > 0))
        {
            var target = _layerActive ? _strokeCanvas! : _canvas;
            _engine.DrawSegment(target, from, sample, brush, _strokeColor, PressureChannel.Processed);
            IsDirty = true;
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
        MergeLayer();
    }

    /// <summary>Start a fresh stroke layer, if this stroke is being washed.</summary>
    private void BeginLayerIfWashing()
    {
        if (Compositing != StrokeCompositing.Wash) return;

        // No blender means this build of Skia would not compile it. Falling back to direct
        // painting keeps the application drawing, with the artifact Wash exists to remove.
        if (AlphaDarken.Blender is not { } blender) return;

        _strokeLayer ??= new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _strokeCanvas ??= new SKCanvas(_strokeLayer);
        _strokeCanvas.Clear(SKColors.Transparent);

        _engine.Blender = blender;
        _layerActive = true;
    }

    /// <summary>
    /// Composite the finished stroke onto the document, once.
    /// </summary>
    /// <remarks>
    /// The single blend is the whole point. Within the layer the marks took the greater alpha of
    /// any overlap, so each point carries the opacity its own pressure asked for; laying that down
    /// in one pass is what stops a hundred overlapping segments turning 15% into 99%.
    /// </remarks>
    private void MergeLayer()
    {
        if (!_layerActive || _strokeLayer is null) return;

        _canvas.DrawBitmap(_strokeLayer, 0, 0);

        _engine.Blender = null;
        _layerActive = false;
        IsDirty = true;
    }

    /// <summary>Remove the last stroke and redraw what remains.</summary>
    public bool Undo()
    {
        EndStroke();
        if (!History.RemoveLast()) return false;

        _canvas.Clear(Paper);
        foreach (var stroke in History.Strokes) Replay(stroke);

        IsDirty = true;
        return true;
    }

    /// <summary>Empty the document and forget its strokes.</summary>
    public void Clear()
    {
        EndStroke();
        History.Clear();
        _canvas.Clear(Paper);
        IsDirty = true;
    }

    /// <summary>Set the colour subsequent strokes are drawn in.</summary>
    public void SetStrokeColor(SKColor color) => _strokeColor = color;

    /// <summary>
    /// Redraw one recorded stroke onto the document, through the same compositing it was drawn
    /// with.
    /// </summary>
    /// <remarks>
    /// A washed stroke has to be replayed washed. Replaying it directly would let its overlaps
    /// accumulate, so an undo would change the appearance of every stroke that survived it --
    /// which is the kind of fault that looks like a rendering bug and is really a bookkeeping one.
    /// </remarks>
    private void Replay(Stroke stroke)
    {
        BeginLayerIfWashing();
        _engine.BeginStroke();

        var target = _layerActive ? _strokeCanvas! : _canvas;
        var samples = stroke.Samples;
        for (int i = 1; i < samples.Count; i++)
        {
            if (!stroke.Brush.DrawAtZeroPressure && samples[i].ProcessedPressure <= 0) continue;
            _engine.DrawSegment(target, samples[i - 1], samples[i],
                                stroke.Brush, stroke.Color, PressureChannel.Processed);
        }

        _engine.EndStroke();
        MergeLayer();
    }

    public void Dispose()
    {
        _strokeCanvas?.Dispose();
        _strokeLayer?.Dispose();
        _canvas.Dispose();
        _bitmap.Dispose();
        _engine.Dispose();
    }
}
