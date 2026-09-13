using Avalonia;
using SkiaSharp;

namespace PenDynamicsPaint.Drawing;

/// <summary>
/// The strokes drawn so far, in order, each with the brush and layer it was made with.
/// </summary>
/// <remarks>
/// <para>
/// Still not a document model: no tools, no selection, no commands -- just the list a stroke can be
/// recorded into and replayed from, which is the minimum that makes undo possible without inventing
/// history after the fact. It knows layers only as far as <see cref="Stroke.LayerId"/>, which says
/// which surface to replay a stroke onto and nothing else. The stack itself is
/// <c>PaintSession.Layers</c>.
/// </para>
/// <para>
/// <b>Strokes are the unit of undo, not commands.</b> Adding, deleting, reordering or merging a
/// layer is not recorded here and cannot be stepped back through.
/// </para>
/// <para>
/// <b>Input is authoritative; what the brush made of it is a cache.</b> Each sample keeps the
/// pressure the pen reported as well as the value the brush's curve produced, so the reading stays
/// recoverable.
/// </para>
/// <para>
/// The cache cannot go stale, because a stroke keeps the brush that drew it. This used to need a
/// generation counter -- there was one global curve, so editing it invalidated every cached output
/// and a stroke had to record which generation it belonged to. With the curve on the brush there is
/// nothing global left to change: editing a brush produces a different brush, and the strokes
/// already drawn keep theirs.
/// </para>
/// </remarks>
public sealed class StrokeHistory
{
    /// <summary>
    /// How many completed strokes to keep. Beyond this the oldest is evicted.
    /// </summary>
    /// <remarks>
    /// Undo depth, in plain terms. Generous enough that ordinary use never reaches it, which is
    /// the point: the cap exists to bound a long session, not to ration undo.
    /// </remarks>
    public const int MaxStrokes = 500;

    /// <summary>
    /// How many recorded samples to keep across all strokes.
    /// </summary>
    /// <remarks>
    /// A stroke-count cap alone does not bound memory — one continuous stroke can run for
    /// minutes at tablet report rates. At roughly 70 bytes a sample this ceiling is a few tens of
    /// megabytes, which is the actual quantity worth bounding.
    /// </remarks>
    public const int MaxSamples = 400_000;

    private readonly List<Stroke> _strokes = [];
    private Stroke? _current;
    private int _totalSamples;

    /// <summary>Completed strokes, oldest first.</summary>
    public IReadOnlyList<Stroke> Strokes => _strokes;

    /// <summary>Whether there is a completed stroke to undo.</summary>
    public bool CanUndo => _strokes.Count > 0;

    /// <summary>Samples held across all completed strokes.</summary>
    public int TotalSamples => _totalSamples;

    /// <summary>Begin recording a stroke under the state currently in force.</summary>
    /// <param name="layerId">
    /// Which layer the stroke is going onto, as <c>Layer.Id</c>. Defaulted so a caller exercising
    /// the history on its own need not invent one; the paint session always passes a real id.
    /// </param>
    public void BeginStroke(BrushSettings brush, SKColor color, int layerId = 0,
                            StrokeSmoothing smoothing = default)
        => _current = new Stroke(brush, color, layerId, smoothing);

    /// <summary>Record one sample into the stroke in progress, if there is one.</summary>
    /// <param name="timestampMicroseconds">
    /// The pen's own clock. Defaulted so that callers with no pen point -- tests, and the test
    /// pattern -- need not invent one; zero then means the same thing it means everywhere else,
    /// which is that no clock was reported.
    /// </param>
    public void AddSample(Point position, double rawPressure, PenOrientation orientation,
                          double processedPressure, long timestampMicroseconds = 0)
        => AddSample(new StrokeSample(position, rawPressure, orientation, processedPressure,
                                      timestampMicroseconds));

    /// <summary>Record one sample into the stroke in progress, if there is one.</summary>
    /// <remarks>
    /// The overload a caller that already holds a sample should use. <c>DrawingSession</c> builds
    /// one per pen point to hand to the brush engine, and building a second here from the same
    /// values would be two objects that have to agree.
    /// </remarks>
    public void AddSample(in StrokeSample sample) => _current?.Add(sample);

    /// <summary>
    /// Finish the stroke in progress and keep it, unless it never got a sample.
    /// </summary>
    /// <remarks>
    /// A stroke with no samples draws nothing, so keeping one would make undo appear to do
    /// nothing — the user would press undo and watch an empty entry disappear.
    /// </remarks>
    public void EndStroke()
    {
        if (_current is { Samples.Count: > 0 })
        {
            _strokes.Add(_current);
            _totalSamples += _current.Samples.Count;
        }
        _current = null;
    }

    /// <summary>
    /// Evict the oldest stroke if either cap is exceeded, returning it, or null if nothing needed
    /// evicting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the stroke rather than discarding it because <b>the caller has to bake it into the
    /// replay baseline first</b>. Undo works by clearing the surfaces and replaying what is
    /// retained, so an evicted stroke that is not preserved somewhere would vanish from the canvas
    /// on the next undo — silently destroying work the user can still see.
    /// </para>
    /// <para>
    /// One at a time, so the caller can bake each one. Call until it returns null.
    /// </para>
    /// </remarks>
    public Stroke? EvictOldestIfOverCap()
    {
        if (_strokes.Count == 0) return null;
        if (_strokes.Count <= MaxStrokes && _totalSamples <= MaxSamples) return null;

        var evicted = _strokes[0];
        _strokes.RemoveAt(0);
        _totalSamples -= evicted.Samples.Count;
        return evicted;
    }

    /// <summary>Drop the most recent completed stroke. Returns false if there was none.</summary>
    public bool RemoveLast()
    {
        if (_strokes.Count == 0) return false;
        _totalSamples -= _strokes[^1].Samples.Count;
        _strokes.RemoveAt(_strokes.Count - 1);
        return true;
    }

    /// <summary>
    /// Drop every stroke belonging to one layer, returning how many went.
    /// </summary>
    /// <remarks>
    /// Called when a layer stops existing, by deletion or by being merged away. Leaving its strokes
    /// behind would make undo appear broken rather than merely limited: the next undo would remove
    /// a stroke whose pixels are already gone, so the user would press undo and watch nothing
    /// happen.
    /// </remarks>
    public int RemoveForLayer(int layerId)
    {
        int removed = 0;
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            if (_strokes[i].LayerId != layerId) continue;
            _totalSamples -= _strokes[i].Samples.Count;
            _strokes.RemoveAt(i);
            removed++;
        }
        return removed;
    }

    /// <summary>Forget everything, including any stroke in progress.</summary>
    public void Clear()
    {
        _strokes.Clear();
        _current = null;
        _totalSamples = 0;
    }
}
