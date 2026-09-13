namespace PenDynamicsPaint.Drawing;

/// <summary>
/// Where along a stroke the next mark falls, by distance travelled rather than by sample arrival.
/// </summary>
/// <remarks>
/// <para>
/// Krita's isotropic rule, verified against <c>KDE/krita</c> at <c>1e6586cb</c>. With <c>s</c> the
/// spacing wanted here and <c>a</c> the distance accumulated since the last mark, the next mark
/// falls <c>max(0.5, s) - a</c> further on. If that fits inside the segment it is placed there and
/// the accumulator resets; if the segment is shorter, its length is added to the accumulator and
/// nothing is drawn.
/// </para>
/// <para>
/// <b>The property this exists for is segmentation invariance.</b> One 10-unit segment and ten
/// 1-unit segments put marks in the same places, because the accumulator carries across segments.
/// That is what makes the ink depend on the path rather than on how often the tablet reported, and
/// it is the difference between a stroke that looks the same drawn slowly and one that does not.
/// </para>
/// <para>
/// Deliberately free of Skia and of any brush: distances in, distances out. The spacing itself is
/// a function of position because it depends on the mark's size, which depends on pressure -- in
/// Krita's Pixel Brush, pressure changes the dab's size and its spacing together.
/// </para>
/// </remarks>
public sealed class DabSpacing
{
    /// <summary>
    /// The floor under the spacing, matching Krita's <c>MIN_DISTANCE_SPACING</c>.
    /// </summary>
    /// <remarks>
    /// Without it a brush whose spacing went to zero -- a vanishing dab at the start of a stroke,
    /// say -- would place marks forever without advancing.
    /// </remarks>
    public const double MinSpacing = 0.5;

    private double _accumulated;

    /// <summary>Distance carried since the last mark. Survives across segments; that is the point.</summary>
    public double Accumulated => _accumulated;

    /// <summary>Begin a new stroke. Anything carried from the last one is discarded.</summary>
    public void Reset() => _accumulated = 0;

    /// <summary>
    /// The distances along this segment at which marks fall, in order.
    /// </summary>
    /// <param name="length">Segment length, in the same units as the spacing.</param>
    /// <param name="spacingAt">
    /// The spacing wanted at a given distance along this segment. Called per mark rather than once,
    /// because a brush whose size follows pressure has a spacing that changes along the segment.
    /// </param>
    /// <remarks>
    /// A zero-length segment places nothing and accumulates nothing. A stationary pen therefore
    /// deposits no ink, which is correct for spacing by distance and is exactly what an airbrush
    /// would need a separate, time-based rule for.
    /// </remarks>
    public IEnumerable<double> Walk(double length, Func<double, double> spacingAt)
    {
        if (length <= 0 || !double.IsFinite(length)) yield break;

        double at = 0;
        while (true)
        {
            double spacing = Math.Max(MinSpacing, spacingAt(at));
            if (!double.IsFinite(spacing)) yield break;

            double remaining = spacing - _accumulated;

            // Does not reach the end of this segment: carry what is left and draw nothing. This is
            // the branch that makes many short segments behave like one long one.
            if (remaining > length - at)
            {
                _accumulated += length - at;
                yield break;
            }

            at += remaining;
            _accumulated = 0;
            yield return at;
        }
    }
}
