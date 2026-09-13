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
            _engine.BeginStroke();
        }

        var sample = new StrokeSample(new global::Avalonia.Point(documentX, documentY),
                                      rawPressure, orientation, processedPressure,
                                      timestampMicroseconds);
        History.AddSample(sample);

        if (_lastSample is { } from && (brush.DrawAtZeroPressure || processedPressure > 0))
        {
            _engine.DrawSegment(_canvas, from, sample, brush, _strokeColor, PressureChannel.Processed);
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

    private void Replay(Stroke stroke)
    {
        _engine.BeginStroke();

        var samples = stroke.Samples;
        for (int i = 1; i < samples.Count; i++)
        {
            if (!stroke.Brush.DrawAtZeroPressure && samples[i].ProcessedPressure <= 0) continue;
            _engine.DrawSegment(_canvas, samples[i - 1], samples[i],
                                stroke.Brush, stroke.Color, PressureChannel.Processed);
        }

        _engine.EndStroke();
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _bitmap.Dispose();
        _engine.Dispose();
    }
}
