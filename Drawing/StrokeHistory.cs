using Avalonia;
using SkiaSharp;

namespace PenDynamicsPaint.Drawing;

/// <summary>
/// The strokes drawn so far, in order, and the parameter generation they were drawn under.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> a document model. There are no layers, no tools, and no selection —
/// just the list a stroke can be recorded into and replayed from, which is the minimum that makes
/// undo possible without inventing history after the fact.
/// </para>
/// <para>
/// <b>Input is authoritative; the pipeline output on each sample is a cache.</b> That is the
/// decision this type exists to make real. Replaying a stroke whose
/// <see cref="Stroke.ParamsVersion"/> still matches <see cref="ParamsVersion"/> can use the cached
/// values; a stroke from an older generation has to be re-run from its raw pressures, or the
/// canvas would show two curve generations at once with nothing to say which is which.
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

    /// <summary>The current parameter generation. Bumped whenever the curve params change.</summary>
    public int ParamsVersion { get; private set; }

    /// <summary>Completed strokes, oldest first.</summary>
    public IReadOnlyList<Stroke> Strokes => _strokes;

    /// <summary>Whether there is a completed stroke to undo.</summary>
    public bool CanUndo => _strokes.Count > 0;

    /// <summary>Samples held across all completed strokes.</summary>
    public int TotalSamples => _totalSamples;

    /// <summary>
    /// Note that the curve parameters have changed, invalidating every cached output.
    /// </summary>
    /// <remarks>
    /// Cheap and unconditional — it does not walk the strokes. Staleness is discovered per stroke
    /// at replay time by comparing versions, so changing a curve a hundred times costs a hundred
    /// increments rather than a hundred passes over the history.
    /// </remarks>
    public void NoteParamsChanged() => ParamsVersion++;

    /// <summary>Begin recording a stroke under the state currently in force.</summary>
    public void BeginStroke(BrushSettings brush, SKColor color)
        => _current = new Stroke(brush, color, ParamsVersion);

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

    /// <summary>Forget everything, including any stroke in progress.</summary>
    public void Clear()
    {
        _strokes.Clear();
        _current = null;
        _totalSamples = 0;
    }
}
