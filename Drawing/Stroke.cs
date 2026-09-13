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
/// the pen actually did, and keeping them is what makes a recorded stroke worth more than the
/// pixels it left.
/// </para>
/// <para>
/// <b><see cref="ProcessedPressure"/> is what the brush made of it</b>, through
/// <see cref="BrushSettings.Process"/>. It cannot disagree with the brush, since the stroke keeps
/// the brush too. Width and opacity are deliberately not stored: they follow from this value and
/// that brush by arithmetic, so keeping them would be a second copy to keep in step for no gain.
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

    public Stroke(BrushSettings brush, SKColor color, int layerId = 0)
    {
        Brush = brush;
        Color = color;
        LayerId = layerId;
    }

    /// <summary>
    /// Which layer this stroke was drawn on, as <c>Layer.Id</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Undo works by clearing one layer and replaying the strokes that belong to it, so a stroke
    /// that did not say where it went would have to be replayed onto every layer or onto none.
    /// </para>
    /// <para>
    /// The <b>id</b>, not the index: adding or removing a layer renumbers every index above it,
    /// and strokes that recorded an index would quietly move to a different surface.
    /// </para>
    /// <para>
    /// Zero when no layer was recorded, which is what a caller exercising the history on its own
    /// gets. Nothing in the paint session leaves it unset.
    /// </para>
    /// </remarks>
    public int LayerId { get; }

    /// <summary>
    /// The brush that drew this stroke, in full.
    /// </summary>
    /// <remarks>
    /// Engine, size, spacing, opacity, pressure target and curve, all of it. A replay uses this
    /// rather than whatever is selected now, which is what stops an undo redrawing older strokes in
    /// a brush they were never made with.
    /// </remarks>
    public BrushSettings Brush { get; }

    /// <summary>The resolved colour this stroke was drawn in.</summary>
    public SKColor Color { get; }

    public IReadOnlyList<StrokeSample> Samples => _samples;

    public void Add(StrokeSample sample) => _samples.Add(sample);
}
