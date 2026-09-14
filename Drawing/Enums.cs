namespace PenDynamicsPaint.Drawing;

/// <summary>How each new stroke picks its colour.</summary>
/// <remarks>
/// Used to live in <c>Curves/Enums.cs</c>, next to curve types it has nothing to do with. It is a
/// brush concept: what a mark looks like, not how pressure is shaped on the way to it.
/// </remarks>
public enum ColorMode
{
    Black,
    Red,

    /// <summary>A colour chosen per stroke. The chosen colour is not recoverable from settings.</summary>
    Random,
}

/// <summary>How the marks within one stroke combine with each other.</summary>
/// <remarks>
/// <para>
/// Only visible with translucency. Opaque marks composite to the same colour however many of
/// them overlap, so on a brush whose opacity nothing drives the two are indistinguishable.
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
