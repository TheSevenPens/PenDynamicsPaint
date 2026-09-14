using PenDynamicsPaint.Drawing;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// What the ink does between two pen samples.
/// </summary>
/// <remarks>
/// <para>
/// A tablet reports every few document units, so a straight chord between samples draws a polygon.
/// The corners show on any curve drawn quickly, and no amount of filtering the samples removes them
/// -- that decides where the samples are, this decides the path between them.
/// </para>
/// <para>
/// The measurement throughout is a coarsely sampled circle, because a circle is a shape whose right
/// answer is known: the straight path is an inscribed polygon and its error is exactly
/// <c>r(1 - cos(pi/n))</c>, so a fitted path either beats that or it is not doing anything.
/// </para>
/// </remarks>
public class CurveFitterTests
{
    private const double Radius = 100;
    private static readonly DocumentPoint Centre = new(200, 200);

    /// <summary>Points around a circle, coarsely enough that a polygon through them is visible.</summary>
    private static List<DocumentPoint> Circle(int count, double sweep = 2 * Math.PI)
    {
        var points = new List<DocumentPoint>();
        for (int i = 0; i < count; i++)
        {
            double angle = sweep * i / (count - 1);
            points.Add(new DocumentPoint(Centre.X + Radius * Math.Cos(angle),
                                 Centre.Y + Radius * Math.Sin(angle)));
        }
        return points;
    }

    private static StrokeSample At(DocumentPoint p, double pressure = 1.0) =>
        new(p, pressure, PenOrientation.None);

    /// <summary>The whole painted path for a set of samples, flush included.</summary>
    private static List<DocumentPoint> Path(IEnumerable<DocumentPoint> samples, StrokeInterpolation how)
    {
        var fitter = new CurveFitter();
        var path = new List<DocumentPoint>();

        foreach (var p in samples)
            path.AddRange(fitter.Next(At(p), how).Select(s => s.Position));

        path.AddRange(fitter.Flush(how).Select(s => s.Position));
        return path;
    }

    /// <summary>How far the path strays from the circle it was sampled from.</summary>
    private static double RadialError(IEnumerable<DocumentPoint> path) =>
        path.Select(p => Math.Abs(Distance(p, Centre) - Radius)).Max();

    private static double Distance(DocumentPoint a, DocumentPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    [Fact]
    public void A_straight_path_is_a_polygon_through_the_samples()
    {
        // The control, and the thing being improved on. Twelve points around a circle, joined by
        // chords: the midpoint of each chord falls short of the arc by r(1 - cos(pi/n)).
        var samples = Circle(12);
        var path = Path(samples, StrokeInterpolation.Straight);

        Assert.Equal(samples, path);
    }

    [Fact]
    public void A_fitted_path_follows_the_arc_the_samples_came_from()
    {
        // Twelve points around a circle. In the interior the fit is all but exact -- the tangent
        // at a sample is a central difference through its neighbours, which for a circle points
        // along the true tangent, and Krita's handle length works out within a percent of the
        // exact one for an arc of this angle.
        var samples = Circle(12);
        var path = Path(samples, StrokeInterpolation.Curved);

        double polygon = RadialError(Chords(samples));
        Assert.True(polygon > 3, $"the polygon should cut the corner, it strayed {polygon:F2}");

        Assert.True(Interior(path, samples) < polygon / 10,
            $"the interior strayed {Interior(path, samples):F3} against the polygon's {polygon:F2}");
    }

    [Fact]
    public void The_two_end_segments_are_only_approximated()
    {
        // And the limit of it, stated rather than hidden by an average. The first and last
        // segments have no neighbour outside the stroke to take a tangent from, so they use the
        // single chord they have, which points a half-angle off the true tangent. Krita has the
        // same shortfall for the same reason.
        //
        // It is still better than a chord, which is the only claim this needs to support. Fitting
        // the ends properly would mean guessing a tangent for a direction the pen never went.
        var samples = Circle(12);
        var path = Path(samples, StrokeInterpolation.Curved);

        double polygon = RadialError(Chords(samples));
        double ends = Ends(path, samples);
        double interior = Interior(path, samples);

        Assert.True(ends > interior * 10,
            $"the ends are meant to be the loose part: {ends:F3} against {interior:F3} inside");
        Assert.True(ends < polygon,
            $"but still better than a chord: {ends:F3} against {polygon:F2}");
    }

    /// <summary>Worst radial error away from the first and last segments.</summary>
    private static double Interior(List<DocumentPoint> path, IReadOnlyList<DocumentPoint> samples)
    {
        var (first, last) = InnerSpan(path, samples);
        return path.Skip(first).Take(last - first + 1)
                   .Select(p => Math.Abs(Distance(p, Centre) - Radius)).Max();
    }

    /// <summary>Worst radial error within the first and last segments.</summary>
    private static double Ends(List<DocumentPoint> path, IReadOnlyList<DocumentPoint> samples)
    {
        var (first, last) = InnerSpan(path, samples);
        return Math.Max(path.Take(first + 1).Select(p => Math.Abs(Distance(p, Centre) - Radius)).Max(),
                        path.Skip(last).Select(p => Math.Abs(Distance(p, Centre) - Radius)).Max());
    }

    private static (int First, int Last) InnerSpan(List<DocumentPoint> path, IReadOnlyList<DocumentPoint> samples) =>
        (path.FindIndex(p => Distance(p, samples[1]) < 1e-6),
         path.FindIndex(p => Distance(p, samples[^2]) < 1e-6));

    /// <summary>The straight path, sampled along its chords, for a fair comparison.</summary>
    /// <remarks>
    /// Comparing the chord endpoints alone would say the polygon is perfect: its error is entirely
    /// between the samples, which is the whole point. So the chords are walked at the same
    /// resolution the fitted path is flattened to.
    /// </remarks>
    private static List<DocumentPoint> Chords(IReadOnlyList<DocumentPoint> samples)
    {
        var path = new List<DocumentPoint>();
        for (int i = 1; i < samples.Count; i++)
        {
            double length = Distance(samples[i - 1], samples[i]);
            int pieces = Math.Max(1, (int)Math.Ceiling(length / CurveFitter.FlatteningStep));
            for (int j = 1; j <= pieces; j++)
            {
                double t = (double)j / pieces;
                path.Add(new DocumentPoint(samples[i - 1].X + (samples[i].X - samples[i - 1].X) * t,
                                   samples[i - 1].Y + (samples[i].Y - samples[i - 1].Y) * t));
            }
        }
        return path;
    }

    [Fact]
    public void A_path_that_doubles_back_does_not_fly_off()
    {
        // Every other shape here is smooth, and a smooth path only ever needs the simple
        // construction: both control handles pull toward the point where the end tangents meet.
        // Where the path reverses, those two tangent lines meet a long way away -- measured at up
        // to 434 units out on this input -- and a handle dragged toward it takes the curve off the
        // page. Krita tests whether the handles fall on opposite sides of the chord and gives each
        // its own direction at half the chord's length instead.
        //
        // Disabling that test takes the worst departure below from 7.8 units to 145.
        var samples = Scribble();
        var path = Path(samples, StrokeInterpolation.Curved);

        double worst = path.Max(q => ToPolyline(q, samples));
        Assert.True(worst < 20,
            $"the fitted path left the samples by {worst:F2} units");
    }

    /// <summary>Samples that reverse sharply, as a fast scribble gives.</summary>
    private static List<DocumentPoint> Scribble()
    {
        var rnd = new Random(7);
        var samples = new List<DocumentPoint>();
        for (int i = 0; i < 60; i++)
            samples.Add(new DocumentPoint(20 + i * 9 + rnd.NextDouble() * 8, 150 + rnd.NextDouble() * 90));
        return samples;
    }

    /// <summary>How far a point lies from the polyline through the samples.</summary>
    private static double ToPolyline(DocumentPoint p, IReadOnlyList<DocumentPoint> samples)
    {
        double best = double.MaxValue;
        for (int i = 1; i < samples.Count; i++) best = Math.Min(best, ToSegment(p, samples[i - 1], samples[i]));
        return best;
    }

    private static double ToSegment(DocumentPoint p, DocumentPoint a, DocumentPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0) return Distance(p, a);

        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return Distance(p, new DocumentPoint(a.X + dx * t, a.Y + dy * t));
    }

    [Fact]
    public void The_fitted_path_still_passes_through_every_sample()
    {
        // Interpolating, not approximating. A curve that merely went near the samples would be
        // moving ink away from where the pen was, which is the filter's job and not this one's.
        var samples = Circle(8);
        var path = Path(samples, StrokeInterpolation.Curved);

        foreach (var sample in samples)
            Assert.True(path.Any(p => Distance(p, sample) < 1e-6),
                        $"the path misses the sample at {sample}");
    }

    [Fact]
    public void A_straight_line_does_not_acquire_a_wobble()
    {
        // The failure a naive tangent scheme gives: control handles that overshoot turn a straight
        // run into a slack rope. Krita shortens them as the two end tangents converge in length,
        // which is exactly the case a straight line at a steady speed presents.
        var samples = new List<DocumentPoint>();
        for (double x = 20; x <= 320; x += 25) samples.Add(new DocumentPoint(x, 150));

        var path = Path(samples, StrokeInterpolation.Curved);

        double wobble = path.Select(p => Math.Abs(p.Y - 150)).Max();
        Assert.True(wobble < 1e-6, $"the line bowed by {wobble:F6}");
    }

    [Fact]
    public void The_last_segment_arrives_only_on_flush()
    {
        // The fitter is a sample behind, because the tangent at a sample needs the one after it.
        // Without the flush every curved stroke would stop short of where the pen lifted.
        var samples = Circle(8);
        var fitter = new CurveFitter();

        var live = new List<DocumentPoint>();
        foreach (var p in samples)
            live.AddRange(fitter.Next(At(p), StrokeInterpolation.Curved).Select(s => s.Position));

        Assert.True(Distance(live[^1], samples[^2]) < 1e-6,
            "before the flush the path should end one sample back");

        var flushed = fitter.Flush(StrokeInterpolation.Curved).Select(s => s.Position).ToList();
        Assert.NotEmpty(flushed);
        Assert.True(Distance(flushed[^1], samples[^1]) < 1e-6,
            "the flush should carry the path to the last sample");
    }

    [Fact]
    public void Fitting_is_deterministic_so_a_replay_lands_where_the_stroke_did()
    {
        var samples = Circle(10);

        Assert.Equal(Path(samples, StrokeInterpolation.Curved),
                     Path(samples, StrokeInterpolation.Curved));
    }

    [Fact]
    public void A_reset_stops_one_stroke_bending_into_the_next()
    {
        var fitter = new CurveFitter();
        foreach (var p in Circle(8)) fitter.Next(At(p), StrokeInterpolation.Curved).ToList();

        fitter.Reset();

        // Nothing owed, and the next stroke starts where it was put rather than curving back
        // toward wherever the last one finished.
        Assert.Empty(fitter.Flush(StrokeInterpolation.Curved));

        var elsewhere = new DocumentPoint(900, 700);
        var first = fitter.Next(At(elsewhere), StrokeInterpolation.Curved).ToList();
        Assert.Single(first);
        Assert.Equal(elsewhere, first[0].Position);
    }

    [Fact]
    public void Pressure_is_carried_along_the_fitted_path()
    {
        // The flattened points are not pen samples, so each needs readings of its own. Without
        // them a pressure ramp would step at the samples rather than run through the segment.
        var samples = Circle(6);
        var fitter = new CurveFitter();
        var drawn = new List<StrokeSample>();

        for (int i = 0; i < samples.Count; i++)
            drawn.AddRange(fitter.Next(At(samples[i], 0.1 + 0.15 * i), StrokeInterpolation.Curved));
        drawn.AddRange(fitter.Flush(StrokeInterpolation.Curved));

        Assert.True(drawn.Count > samples.Count * 4, "the path should be flattened finely");

        double previous = -1;
        foreach (var point in drawn)
        {
            Assert.InRange(point.RawPressure, 0.1, 0.85);
            Assert.True(point.RawPressure >= previous - 1e-9, "the ramp should not step back");
            previous = point.RawPressure;
        }

        // Monotonic and in range is not enough: handing every flattened point the pressure of the
        // sample ahead of it satisfies both, and turns the ramp into a staircase with one step per
        // sample. What says it is a ramp is that the value changes between the steps.
        int distinct = drawn.Select(q => Math.Round(q.RawPressure, 9)).Distinct().Count();
        Assert.True(distinct > drawn.Count / 2,
            $"only {distinct} distinct pressures over {drawn.Count} points: a staircase, not a ramp");
    }

    [Fact]
    public void A_stationary_pen_does_not_break_the_fit()
    {
        // Repeated samples give a zero tangent, which has no direction to build a handle from.
        // Krita falls back to a straight piece; the arithmetic would otherwise divide by zero.
        var samples = new List<DocumentPoint>
        {
            new(100, 100), new(100, 100), new(100, 100), new(160, 100), new(160, 100),
        };

        var path = Path(samples, StrokeInterpolation.Curved);

        Assert.All(path, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y),
                                          $"the fit produced {p}"));
        Assert.True(Distance(path[^1], samples[^1]) < 1e-6);
    }
}
