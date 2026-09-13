using Avalonia;
using SkiaSharp;

namespace PenDynamicsPaint.Drawing;

/// <summary>
/// The orientation half of a pen sample: tilt and barrel rotation, in degrees.
/// </summary>
/// <remarks>
/// Nothing renders these yet. They are recorded from day one because they already arrive on every
/// <c>PenPoint</c> and already show in the telemetry ribbon — and because adding a field to a
/// stroke format after strokes exist is a migration, while recording one the renderer ignores is
/// nearly free.
/// </remarks>
public readonly record struct PenOrientation(
    double Azimuth,
    double Altitude,
    double Twist,
    double TiltX,
    double TiltY)
{
    public static readonly PenOrientation None = default;
}

/// <summary>
/// One recorded pen sample: what the pen reported, plus what the pipeline made of it.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="RawPressure"/> and <see cref="Orientation"/> are authoritative.</b> They are what
/// the pen actually did, and re-running them through a changed curve is the point of recording
/// strokes at all.
/// </para>
/// <para>
/// <b><see cref="ProcessedPressure"/> is a cache.</b> It is only valid for the
/// <c>PressureCurveParams</c> that produced it — see <see cref="Stroke.ParamsVersion"/>. Width and
/// opacity are deliberately not stored: they follow from this value and the stroke's brush by
/// arithmetic, so keeping them would be a second cache to invalidate for no gain.
/// </para>
/// </remarks>
/// <param name="Position">Canvas-local position in DIPs.</param>
/// <param name="TimestampMicroseconds">
/// The pen's own clock, as <c>PenPoint.TimestampMicroseconds</c> reported it. Differences are the
/// contract; the origin is unspecified and differs by backend. Zero when the backend supplied no
/// clock, which means none was reported rather than that no time passed.
/// </param>
/// <remarks>
/// Nothing consumes the timestamp yet. It is carried because a brush engine that places marks by
/// elapsed time -- libmypaint's <c>stroke_to</c> takes a <c>dtime</c>, and three of its nine
/// dynamic inputs are derived from it -- cannot be given one after the fact if the sample never
/// held it. See TheSevenPens/PenDynamicsLab#64.
/// </remarks>
public readonly record struct StrokeSample(
    Point Position,
    double RawPressure,
    PenOrientation Orientation,
    double ProcessedPressure,
    long TimestampMicroseconds = 0)
{
    /// <summary>Whichever of the two pressures <paramref name="channel"/> names.</summary>
    public double PressureFor(PressureChannel channel) =>
        channel == PressureChannel.Raw ? RawPressure : ProcessedPressure;
}

/// <summary>
/// One stroke: the samples, and the state that was in force while it was drawn.
/// </summary>
/// <remarks>
/// <para>
/// A stroke covers <b>both</b> surfaces. One gesture draws the processed mark and the raw
/// comparison at the same time, so replaying a stroke redraws both — there is no per-canvas
/// stroke.
/// </para>
/// <para>
/// <see cref="Brush"/> and <see cref="Color"/> are snapshots, not references to current state.
/// The colour especially: under <see cref="ColorMode.Random"/> the colour a stroke got cannot be
/// recovered from settings afterwards, so it has to be captured at stroke start or it is lost.
/// </para>
/// </remarks>
public sealed class Stroke
{
    private readonly List<StrokeSample> _samples = [];

    public Stroke(BrushSettings brush, SKColor color, int paramsVersion)
    {
        Brush = brush;
        Color = color;
        ParamsVersion = paramsVersion;
    }

    /// <summary>The brush configuration in force when this stroke was drawn.</summary>
    public BrushSettings Brush { get; }

    /// <summary>The resolved colour this stroke was drawn in.</summary>
    public SKColor Color { get; }

    /// <summary>
    /// Which generation of <c>PressureCurveParams</c> produced this stroke's cached
    /// <see cref="StrokeSample.ProcessedPressure"/> values.
    /// </summary>
    /// <remarks>
    /// Compared against the history's current version to decide whether the cache can be trusted.
    /// Recorded from day one even though the first implementation mostly hits the valid case:
    /// adding it once strokes exist without one is the awkward migration this avoids.
    /// </remarks>
    public int ParamsVersion { get; private set; }

    public IReadOnlyList<StrokeSample> Samples => _samples;

    public void Add(StrokeSample sample) => _samples.Add(sample);

    /// <summary>
    /// Replace the cached pipeline outputs with ones computed under <paramref name="version"/>.
    /// </summary>
    /// <remarks>
    /// Re-curving rewrites the cache rather than keeping a second view. Two generations of output
    /// for one stroke would mean the canvas could show a mix, and nothing would say which.
    /// </remarks>
    public void RecacheOutputs(IReadOnlyList<double> processedPressures, int version)
    {
        if (processedPressures.Count != _samples.Count)
            throw new ArgumentException(
                $"expected {_samples.Count} values for this stroke, got {processedPressures.Count}",
                nameof(processedPressures));

        for (int i = 0; i < _samples.Count; i++)
            _samples[i] = _samples[i] with { ProcessedPressure = processedPressures[i] };

        ParamsVersion = version;
    }
}
