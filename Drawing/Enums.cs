namespace PenDynamicsPaint.Drawing;

/// <summary>How each new stroke picks its colour.</summary>
/// <remarks>
/// This and <see cref="PressureControl"/> used to live in <c>Curves/Enums.cs</c>, next to
/// curve types they have nothing to do with. They are brush concepts: what a mark looks like,
/// not how pressure is shaped on the way to it.
/// </remarks>
public enum ColorMode
{
    Black,
    Red,

    /// <summary>A colour chosen per stroke. The chosen colour is not recoverable from settings.</summary>
    Random,
}

/// <summary>Which of a sample's two pressures a mark is being drawn from.</summary>
/// <remarks>
/// <para>
/// One gesture draws two surfaces: the processed one takes the pipeline's output, the raw one
/// takes the untouched device reading, and comparing them is the point of the application. Both
/// values live on the same <see cref="StrokeSample"/>, so anything handed a sample has to be told
/// which of them applies.
/// </para>
/// <para>
/// This exists because <see cref="IBrushEngine"/> now receives samples rather than a width that
/// was computed for it. The choice could have been made by passing the selected pressure
/// alongside the sample, which would have put the reduction back one field later and left the
/// engine unable to see anything else the sample carries.
/// </para>
/// </remarks>
public enum PressureChannel
{
    /// <summary>The pipeline's output. Drives the processed surface.</summary>
    Processed,

    /// <summary>The device reading, before any curve or smoothing. Drives the raw surface.</summary>
    Raw,
}

/// <summary>How the marks within one stroke combine with each other.</summary>
/// <remarks>
/// <para>
/// Only visible with translucency. Opaque marks composite to the same colour however many of them
/// overlap, so with <see cref="PressureControl.Size"/> the two are indistinguishable.
/// </para>
/// </remarks>
public enum StrokeCompositing
{
    /// <summary>
    /// Each mark composites onto the document as it is drawn, so overlaps accumulate.
    /// </summary>
    /// <remarks>
    /// Kept for comparison rather than as a recommendation. Its rate is set by <b>how many samples
    /// arrived</b> rather than by distance travelled, so the same path drawn at two speeds gives
    /// different ink and a light stroke saturates: 15% pressure renders at 99% at real tablet
    /// sample rates. It is therefore not Krita's Build-up, which spaces its dabs by distance and is
    /// speed-independent; calling it that would present an artifact as a feature.
    /// </remarks>
    Direct,

    /// <summary>
    /// The stroke composites into a layer of its own, overlaps taking the greater alpha, and that
    /// layer merges onto the document once when the stroke ends.
    /// </summary>
    /// <remarks>
    /// Krita's Wash, and the same mechanism: <see cref="AlphaDarken"/> within the stroke, one merge
    /// at the end. Per-sample opacity survives -- each point shows the alpha its own pressure asked
    /// for -- and no amount of overlapping pushes it past that.
    /// </remarks>
    Wash,
}

/// <summary>Which property of the mark the pressure signal drives.</summary>
/// <remarks>
/// A single choice rather than a weight per target, which is the shape a general dynamics system
/// has -- libmypaint maps nine inputs onto a dozen outputs, each through its own curve. This is the
/// part of that worth having before the rest exists, and <see cref="Both"/> is here because an
/// exclusive choice cannot express an ordinary soft brush.
/// </remarks>
public enum PressureControl
{
    /// <summary>Pressure sets stroke width; opacity stays at the brush's own.</summary>
    Size,

    /// <summary>Pressure sets opacity; stroke width stays at the brush size.</summary>
    Opacity,

    /// <summary>Pressure sets both, which is what most real brushes do.</summary>
    Both,
}

/// <summary>What the ink does between two pen samples.</summary>
/// <remarks>
/// A second kind of smoothing, and a more visible one than filtering the samples themselves:
/// <see cref="PathSmoother"/> decides where the samples are, this decides the path between them.
/// A tablet reporting every few document units draws a polygon under
/// <see cref="Straight"/>, and the corners show on any curve drawn quickly.
/// </remarks>
public enum StrokeInterpolation
{
    /// <summary>A chord from one sample to the next. What the pen reported, and nothing more.</summary>
    Straight,

    /// <summary>
    /// A cubic through the samples, with tangents taken from their neighbours.
    /// </summary>
    /// <remarks>
    /// Costs a one-sample lag, because the tangent at a sample needs the one after it. See
    /// <see cref="CurveFitter"/>.
    /// </remarks>
    Curved,
}

/// <summary>Which engine lays down a brush's marks.</summary>
/// <remarks>
/// A choice on the brush rather than on the application, and named rather than held as an engine
/// instance, because <b>a stroke has to be able to record it</b>. An engine holds a paint, a path
/// and per-stroke accumulators; a stroke keeping one would be keeping a live object to describe
/// something already finished. The session maps the name back to an instance.
/// </remarks>
public enum BrushEngineKind
{
    /// <summary>An antialiased taper swept between the two round ends of each segment.</summary>
    Taper,

    /// <summary>Round marks stamped along the path at a distance interval.</summary>
    Dabs,

    /// <summary>
    /// Dabs whose size, opacity, softness and spacing are decided per dab by a MyPaint brush.
    /// </summary>
    /// <remarks>
    /// The same distance spacing as <see cref="Dabs"/>, with the numbers coming from
    /// <c>BrushSettings.MyPaint</c> rather than from the size and spacing sliders.
    /// </remarks>
    MyPaint,
}
