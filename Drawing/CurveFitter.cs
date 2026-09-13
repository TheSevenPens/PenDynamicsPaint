
namespace PenDynamicsPaint.Drawing;

/// <summary>
/// Fits a curve through the pen samples, so the ink between them follows an arc rather than a
/// chord.
/// </summary>
/// <remarks>
/// <para>
/// Krita's Bezier interpolation, ported from <c>KDE/krita</c> at <c>75315b18</c>
/// (<c>KisToolFreehandHelper::paintBezierSegment</c>). This is a <b>second</b> kind of smoothing
/// and a more visible one than <see cref="PathSmoother"/>: that decides where the samples are, this
/// decides the path between them. A tablet reporting every few document units draws a polygon
/// without it, and the corners show on any curve drawn quickly.
/// </para>
/// <para>
/// <b>It lags one sample.</b> The tangent at a sample is the central difference through its
/// neighbours, so the segment ending at a sample cannot be drawn until the next one arrives.
/// <see cref="Flush"/> paints the one still owed when the pen lifts. Krita does the same, and the
/// alternative -- a one-sided tangent -- gives a curve that does not meet its neighbour smoothly,
/// which is the artifact this exists to remove.
/// </para>
/// <para>
/// <b>Output is a flattened path, not a curve.</b> Each fitted segment is subdivided into short
/// straight pieces and handed on, so <see cref="IBrushEngine"/> keeps taking two samples at a time
/// and neither engine had to change. The dab engine in particular is indifferent: its spacing rule
/// carries an accumulator across calls and is invariant to how the path was cut up, so subdividing
/// moves no dabs. That property was pinned when the engine was written, and this is what it buys.
/// </para>
/// <para>
/// One adaptation. Krita divides each tangent by the elapsed time across it, making the magnitude a
/// speed; the magnitudes are then compared to decide how far the control points reach. Here they
/// are divided by the number of sample intervals instead, making them a distance per sample. Not
/// every backend supplies a usable clock -- the timestamp on a sample is zero when none was
/// reported -- and a divisor that silently collapsed to one would make the first tangent count
/// double.
/// </para>
/// </remarks>
public sealed class CurveFitter
{
    /// <summary>Longest straight piece the fitted curve is cut into, in document units.</summary>
    /// <remarks>
    /// Short enough that the flattening is invisible at any zoom this application reaches, and
    /// long enough that a fast stroke does not turn into thousands of pieces.
    /// </remarks>
    public const double FlatteningStep = 1.0;

    /// <summary>Most pieces one segment is cut into, whatever its length.</summary>
    private const int MaxPieces = 200;

    /// <summary>How near the control targets the handles reach. Krita's <c>coeff</c>.</summary>
    private const double ControlReach = 0.8;

    /// <summary>Beyond this a computed intersection is treated as degenerate. Krita's value.</summary>
    private const double MaxSanePoint = 1e6;

    private StrokeSample? _older;
    private StrokeSample? _previous;
    private DocumentPoint _previousTangent;
    private bool _haveTangent;

    /// <summary>Begin a new stroke. Nothing is carried over.</summary>
    public void Reset()
    {
        _older = null;
        _previous = null;
        _previousTangent = default;
        _haveTangent = false;
    }

    /// <summary>
    /// Take one sample, and give back the path that is now settled enough to draw.
    /// </summary>
    /// <remarks>
    /// Empty for the first two samples of a stroke, and for the first sample of all: there is no
    /// segment to draw until two samples have arrived, and no curve until three.
    /// </remarks>
    public IEnumerable<StrokeSample> Next(in StrokeSample sample, StrokeInterpolation interpolation)
    {
        if (interpolation == StrokeInterpolation.Straight)
        {
            _previous = sample;
            return [sample];
        }

        return Advance(sample);
    }

    private IEnumerable<StrokeSample> Advance(StrokeSample sample)
    {
        if (_previous is not { } previous)
        {
            _previous = sample;
            yield return sample;              // the stroke has to start somewhere
            yield break;
        }

        if (!_haveTangent)
        {
            // The first tangent is a forward difference, over one interval.
            _previousTangent = Difference(previous.Position, sample.Position, 1);
            _haveTangent = true;
            _older = previous;
            _previous = sample;
            yield break;                      // owed: the segment from older to previous
        }

        // A central difference through the neighbours, over two intervals.
        var newTangent = Difference(_older!.Value.Position, sample.Position, 2);

        foreach (var point in Fit(_older.Value, previous, _previousTangent, newTangent))
            yield return point;

        _previousTangent = newTangent;
        _older = previous;
        _previous = sample;
    }

    /// <summary>
    /// Give back the segment still owed, which is the one ending at the last sample.
    /// </summary>
    /// <remarks>
    /// Without this every stroke would stop one sample short of where the pen lifted, on top of
    /// whatever the smoothing filter already holds back.
    /// </remarks>
    public IEnumerable<StrokeSample> Flush(StrokeInterpolation interpolation)
    {
        if (interpolation == StrokeInterpolation.Straight) yield break;
        if (!_haveTangent || _older is not { } older || _previous is not { } previous) yield break;

        var closing = Difference(older.Position, previous.Position, 1);
        foreach (var point in Fit(older, previous, _previousTangent, closing))
            yield return point;

        _haveTangent = false;
        _older = null;
    }

    private static DocumentPoint Difference(DocumentPoint from, DocumentPoint to, int intervals) =>
        new((to.X - from.X) / intervals, (to.Y - from.Y) / intervals);

    /// <summary>
    /// The cubic through two samples with the given end tangents, cut into straight pieces.
    /// </summary>
    /// <remarks>
    /// The construction is Krita's, and its shape is worth stating because the arithmetic hides it:
    /// the control handles reach toward where the two tangent lines would meet, but are pulled back
    /// when the tangents are similar in length, because a symmetric pair overshoots into a corner
    /// rather than a curve.
    /// </remarks>
    private static IEnumerable<StrokeSample> Fit(StrokeSample from, StrokeSample to,
                                                 DocumentPoint tangentFrom, DocumentPoint tangentTo)
    {
        DocumentPoint p1 = from.Position, p2 = to.Position;

        // A zero tangent carries no direction, so there is no curve to fit. A straight piece is
        // the honest answer, and it is what Krita falls back to.
        if (IsZero(tangentFrom) || IsZero(tangentTo))
        {
            yield return to;
            yield break;
        }

        var direction1 = new DocumentPoint(p1.X + tangentFrom.X, p1.Y + tangentFrom.Y);
        var direction2 = new DocumentPoint(p2.X - tangentTo.X, p2.Y - tangentTo.Y);

        DocumentPoint target1, target2;

        // When the segment between the two control directions crosses the chord, the curve turns
        // back on itself: the handles are on opposite sides and must not be pulled to a common
        // point, so each keeps its own direction at half the chord's length.
        if (Crosses(direction1, direction2, p1, p2))
        {
            double reach = Length(new DocumentPoint(p2.X - p1.X, p2.Y - p1.Y)) / 2;
            target1 = Along(p1, direction1, reach);
            target2 = Along(p2, direction2, reach);
        }
        else if (Meet(p1, direction1, p2, direction2, out var meeting) &&
                 Math.Abs(meeting.X) + Math.Abs(meeting.Y) <= MaxSanePoint)
        {
            target1 = target2 = meeting;
        }
        else
        {
            // Parallel tangents, or a meeting point so far away it says nothing useful.
            target1 = target2 = new DocumentPoint((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
        }

        double speed1 = Length(tangentFrom);
        double speed2 = Length(tangentTo);
        if (speed1 <= 0 || speed2 <= 0) { yield return to; yield break; }

        double similarity = Math.Max(0.5, Math.Min(speed1 / speed2, speed2 / speed1));

        // Symmetric handles overshoot into a corner, so shorten them as the two speeds converge.
        double reachCoefficient = ControlReach * (1 - Math.Max(0, similarity - 0.8));

        DocumentPoint control1, control2;
        if (speed1 > speed2)
        {
            control1 = Lerp(p1, target1, reachCoefficient);
            control2 = Lerp(p2, target2, reachCoefficient * similarity);
        }
        else
        {
            control2 = Lerp(p2, target2, reachCoefficient);
            control1 = Lerp(p1, target1, reachCoefficient * similarity);
        }

        int pieces = PieceCount(p1, control1, control2, p2);
        for (int i = 1; i <= pieces; i++)
        {
            double t = (double)i / pieces;
            yield return Blend(from, to, t) with { Position = Cubic(p1, control1, control2, p2, t) };
        }
    }

    /// <summary>Enough pieces that no piece is longer than <see cref="FlatteningStep"/>.</summary>
    /// <remarks>
    /// Measured on the control polygon, which is never shorter than the curve, so the estimate
    /// errs toward more pieces rather than fewer.
    /// </remarks>
    private static int PieceCount(DocumentPoint p1, DocumentPoint c1, DocumentPoint c2, DocumentPoint p2)
    {
        double polygon = Length(new DocumentPoint(c1.X - p1.X, c1.Y - p1.Y))
                       + Length(new DocumentPoint(c2.X - c1.X, c2.Y - c1.Y))
                       + Length(new DocumentPoint(p2.X - c2.X, p2.Y - c2.Y));

        return Math.Clamp((int)Math.Ceiling(polygon / FlatteningStep), 1, MaxPieces);
    }

    /// <summary>
    /// The pen's readings a fraction of the way along the segment.
    /// </summary>
    /// <remarks>
    /// Linear in the curve parameter rather than in arc length. The two differ only where the
    /// control handles are very uneven, and by less than the pen's own resolution.
    /// </remarks>
    private static StrokeSample Blend(StrokeSample from, StrokeSample to, double t) =>
        to with
        {
            RawPressure = from.RawPressure + (to.RawPressure - from.RawPressure) * t,
            ProcessedPressure = from.ProcessedPressure +
                                (to.ProcessedPressure - from.ProcessedPressure) * t,
        };

    private static DocumentPoint Cubic(DocumentPoint p1, DocumentPoint c1, DocumentPoint c2, DocumentPoint p2, double t)
    {
        double u = 1 - t;
        double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;

        return new DocumentPoint(a * p1.X + b * c1.X + c * c2.X + d * p2.X,
                         a * p1.Y + b * c1.Y + c * c2.Y + d * p2.Y);
    }

    private static bool IsZero(DocumentPoint p) => p.X == 0 && p.Y == 0;

    private static double Length(DocumentPoint p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);

    private static DocumentPoint Lerp(DocumentPoint from, DocumentPoint to, double t) =>
        new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);

    /// <summary>A point <paramref name="distance"/> from <paramref name="origin"/> toward <paramref name="toward"/>.</summary>
    private static DocumentPoint Along(DocumentPoint origin, DocumentPoint toward, double distance)
    {
        var delta = new DocumentPoint(toward.X - origin.X, toward.Y - origin.Y);
        double length = Length(delta);
        if (length <= 0) return origin;

        return new DocumentPoint(origin.X + delta.X / length * distance,
                         origin.Y + delta.Y / length * distance);
    }

    /// <summary>True when the two segments cross within both of their spans.</summary>
    private static bool Crosses(DocumentPoint a1, DocumentPoint a2, DocumentPoint b1, DocumentPoint b2)
    {
        if (!Parameters(a1, a2, b1, b2, out double ta, out double tb)) return false;
        return ta is >= 0 and <= 1 && tb is >= 0 and <= 1;
    }

    /// <summary>Where two infinite lines meet, or false when they are parallel.</summary>
    private static bool Meet(DocumentPoint a1, DocumentPoint a2, DocumentPoint b1, DocumentPoint b2, out DocumentPoint meeting)
    {
        meeting = default;
        if (!Parameters(a1, a2, b1, b2, out double ta, out _)) return false;

        meeting = new DocumentPoint(a1.X + (a2.X - a1.X) * ta, a1.Y + (a2.Y - a1.Y) * ta);
        return true;
    }

    /// <summary>How far along each line the two of them meet.</summary>
    private static bool Parameters(DocumentPoint a1, DocumentPoint a2, DocumentPoint b1, DocumentPoint b2,
                                   out double ta, out double tb)
    {
        ta = tb = 0;

        double ax = a2.X - a1.X, ay = a2.Y - a1.Y;
        double bx = b2.X - b1.X, by = b2.Y - b1.Y;

        double denominator = ax * by - ay * bx;
        if (Math.Abs(denominator) < 1e-12) return false;      // parallel, or a zero-length line

        double dx = b1.X - a1.X, dy = b1.Y - a1.Y;
        ta = (dx * by - dy * bx) / denominator;
        tb = (dx * ay - dy * ax) / denominator;
        return true;
    }
}
