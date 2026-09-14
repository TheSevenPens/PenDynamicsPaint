using PenDynamicsPaint.Drawing;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// Pins the property the filter exists for: <b>how far a path is moved depends on where the pen
/// went, not on how often the tablet reported.</b>
/// </summary>
/// <remarks>
/// <para>
/// This is the fault the dab engine's spacing rule avoids, in a different place. A window counted
/// in samples reaches twice as far along the path on a tablet reporting at 200 Hz as on one
/// reporting at 100, so the same gesture is filtered differently on different hardware and a slow
/// stroke is filtered harder than a fast one. Krita weights by distance travelled instead, which
/// is why the filter is worth porting rather than replacing with an average.
/// </para>
/// <para>
/// <b>The measurement is the displacement, not the residual wobble.</b> Attenuation turned out to
/// be the wrong thing to compare: this filter is self-limiting -- the step it measures runs from
/// the last smoothed position to the new raw one, so a large deviation inflates the step, shrinks
/// the window and softens the filtering. On a gentle signal it removes little, and a rate
/// comparison of what little it removed would pass just as well on a filter that did nothing. How
/// far the filter moves the path is substantial on every signal, and can be required to be.
/// </para>
/// </remarks>
public class PathSmootherTests
{
    private static readonly StrokeSmoothing Smoothing = new()
    {
        Position = StrokeSmoothing.DefaultDistance,
        TailAggressiveness = 0,
    };

    /// <summary>Where the pen starts, and the line the wobble runs along.</summary>
    private const double OriginX = 20, Baseline = 100;

    /// <summary>
    /// The filter needs roughly its own reach of path behind it before it has settled.
    /// </summary>
    /// <remarks>
    /// Everything before that is the start of a stroke, which passes through barely filtered on
    /// purpose. Measuring across it would mostly measure that.
    /// </remarks>
    private const double Settled = 160;

    /// <summary>Hand tremor: small and fast, the thing a filter is meant to remove outright.</summary>
    private static List<DocumentPoint> Tremor(double step, double length = 800) =>
        Wobble(step, length, amplitude: 3, wavelength: 7);

    private static List<DocumentPoint> Wobble(double step, double length, double amplitude, double wavelength)
    {
        var points = new List<DocumentPoint>();
        double k = 2 * Math.PI / wavelength;
        for (double d = 0; d <= length; d += step)
            points.Add(new DocumentPoint(OriginX + d, Baseline + amplitude * Math.Sin(d * k)));
        return points;
    }

    private static StrokeSample At(DocumentPoint p, double pressure = 1.0) =>
        new(p, pressure, PenOrientation.None);

    private static List<DocumentPoint> Smooth(IEnumerable<DocumentPoint> path, StrokeSmoothing smoothing)
    {
        var filter = new PathSmoother();
        return [.. path.Select(p => filter.Next(At(p), smoothing).Position)];
    }

    /// <summary>A moving average over the last <paramref name="window"/> samples: the naive filter.</summary>
    private static List<DocumentPoint> MovingAverage(IReadOnlyList<DocumentPoint> path, int window)
    {
        var output = new List<DocumentPoint>();
        for (int i = 0; i < path.Count; i++)
        {
            int from = Math.Max(0, i - window + 1);
            double x = 0, y = 0;
            for (int j = from; j <= i; j++) { x += path[j].X; y += path[j].Y; }
            int n = i - from + 1;
            output.Add(new DocumentPoint(x / n, y / n));
        }
        return output;
    }

    /// <summary>How far the path strays from the straight line, once the filter has settled.</summary>
    private static double Amplitude(IReadOnlyList<DocumentPoint> path, double step) =>
        path.Skip((int)(Settled / step)).Select(p => Math.Abs(p.Y - Baseline)).DefaultIfEmpty(0).Max();

    private static double Distance(DocumentPoint a, DocumentPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// Run one path through <paramref name="filter"/> at two sample rates and compare.
    /// </summary>
    /// <returns>
    /// How far the filter moved the path, and how far the two rates ended up apart. The dense path
    /// is sampled twice as often, so its every second point sits exactly where a sparse point does.
    /// </returns>
    private static (double Moved, double Disagreement) AcrossRates(
        Func<IReadOnlyList<DocumentPoint>, List<DocumentPoint>> filter)
    {
        var denseIn = Tremor(1.0);
        var sparseIn = Tremor(2.0);
        var dense = filter(denseIn);
        var sparse = filter(sparseIn);

        double moved = 0, apart = 0;
        for (int i = (int)(Settled / 2.0); i < sparse.Count && i * 2 < dense.Count; i++)
        {
            moved = Math.Max(moved, Distance(dense[i * 2], denseIn[i * 2]));
            moved = Math.Max(moved, Distance(sparse[i], sparseIn[i]));
            apart = Math.Max(apart, Distance(dense[i * 2], sparse[i]));
        }

        return (moved, apart);
    }

    [Fact]
    public void Smoothing_flattens_a_tremor()
    {
        // The plain claim. If the filter did nothing, every comparison below would be comparing
        // two copies of the same path.
        var raw = Tremor(2.0);
        var smoothed = Smooth(raw, Smoothing);

        double before = Amplitude(raw, 2.0);
        double after = Amplitude(smoothed, 2.0);

        Assert.True(before > 2.5, $"the input should wobble, got {before:F2}");
        Assert.True(after < before * 0.5, $"expected the tremor halved at least, {before:F2} became {after:F2}");
    }

    [Fact]
    public void The_filter_moves_a_path_the_same_way_at_two_sample_rates()
    {
        // The headline. One physical gesture; one tablet reporting twice as often as the other.
        var (moved, disagreement) = AcrossRates(p => Smooth(p, Smoothing));

        // Non-vacuous first: a filter that did nothing would agree perfectly across rates.
        Assert.True(moved > 4, $"the filter barely moved the path ({moved:F3}), so agreeing proves nothing");

        Assert.True(disagreement < moved * 0.2,
            $"the two rates disagreed by {disagreement:F3} on a move of {moved:F3}");
    }

    [Fact]
    public void A_window_counted_in_samples_does_not_manage_that()
    {
        // The control, and the reason this is a port rather than an average. Twenty-five samples
        // reach 25 units along one path and 50 along the other, so the filter lags by a different
        // distance at each rate. Without this the test above could pass on any gentle filter.
        var (moved, disagreement) = AcrossRates(p => MovingAverage(p, 25));

        Assert.True(disagreement > moved * 0.4,
            $"expected a sample-counted window to disagree across rates: {disagreement:F3} on a move of {moved:F3}");
    }

    [Fact]
    public void The_lag_is_the_same_however_hard_the_hand_shakes()
    {
        // The observable consequence of the filter being recursive: what goes back into the
        // history is the smoothed position, so the filter chases its own output and settles at a
        // steady distance behind the pen. Feed it a wobble three times as large and it still
        // trails by the same amount.
        //
        // Put raw positions in the history instead and that stops being true -- the window then
        // reaches much further back on a steady hand than on a shaky one, so the lag swings with
        // the input. This is the test that tells the two apart, since both smooth.
        double gentle = SettledLag(Wobble(2.0, 800, amplitude: 1, wavelength: 7));
        double shaky = SettledLag(Wobble(2.0, 800, amplitude: 3, wavelength: 7));

        Assert.True(gentle > 2, $"expected a real lag, got {gentle:F2}");
        Assert.True(Math.Abs(gentle - shaky) < gentle * 0.2,
            $"the lag moved with the input: {gentle:F2} on a gentle hand, {shaky:F2} on a shaky one");
    }

    [Fact]
    public void A_longer_reach_lags_further_behind()
    {
        // And the lag is the filter's reach, not an accident: asking it to look further back puts
        // it further behind. This is the cost of smoothing, and it is worth being able to see.
        var path = Wobble(2.0, 800, amplitude: 3, wavelength: 7);

        double near = SettledLag(path, Smoothing with { Position = 20 });
        double far = SettledLag(path, Smoothing with { Position = 120 });

        Assert.True(far > near * 2, $"20 units lagged {near:F2}, 120 units lagged {far:F2}");
    }

    /// <summary>How far the filtered path trails the pen, once it has settled.</summary>
    private static double SettledLag(IReadOnlyList<DocumentPoint> raw, StrokeSmoothing? smoothing = null)
    {
        var smoothed = Smooth(raw, smoothing ?? Smoothing);
        int from = (int)(Settled / 2.0);

        double total = 0;
        int n = 0;
        for (int i = from; i < raw.Count; i++, n++) total += Distance(smoothed[i], raw[i]);

        return n == 0 ? 0 : total / n;
    }

    [Fact]
    public void A_larger_distance_smooths_harder()
    {
        var raw = Tremor(2.0);

        double light = Amplitude(Smooth(raw, Smoothing with { Position = 8 }), 2.0);
        double heavy = Amplitude(Smooth(raw, Smoothing with { Position = 80 }), 2.0);

        Assert.True(heavy < light, $"80 units gave {heavy:F3}, 8 units gave {light:F3}");
    }

    [Fact]
    public void Zero_distance_leaves_the_path_alone()
    {
        var raw = Tremor(2.0);

        Assert.Equal(raw, Smooth(raw, StrokeSmoothing.None));
    }

    [Fact]
    public void The_first_samples_pass_through_unfiltered()
    {
        // With almost no path behind it the mean is dominated by wherever the pen happened to
        // land, which would drag the start of a stroke somewhere the pen never was.
        var raw = Tremor(2.0);
        var smoothed = Smooth(raw, Smoothing);

        // Krita filters once it holds more than three samples, so the fourth is the first to move.
        for (int i = 0; i < PathSmoother.MinimumHistory; i++)
            Assert.Equal(raw[i], smoothed[i]);

        Assert.NotEqual(raw[PathSmoother.MinimumHistory], smoothed[PathSmoother.MinimumHistory]);
    }

    [Fact]
    public void A_reset_stops_one_stroke_bleeding_into_the_next()
    {
        // Without it the first marks of a stroke are averaged with the end of the previous one,
        // and get pulled toward wherever that finished.
        var filter = new PathSmoother();
        foreach (var p in Tremor(2.0)) filter.Next(At(p), Smoothing);

        filter.Reset();
        Assert.Equal(0, filter.Count);

        // Somewhere else entirely. The first samples must land where they were put.
        var elsewhere = new DocumentPoint(900, 700);
        Assert.Equal(elsewhere, filter.Next(At(elsewhere), Smoothing).Position);
    }

    [Fact]
    public void The_filter_is_deterministic_so_a_replay_lands_where_the_stroke_did()
    {
        // What lets a stroke record the pen's own path rather than the filtered one: replaying
        // means running this again, and it has to produce the same answer both times.
        var raw = Tremor(1.5);

        Assert.Equal(Smooth(raw, Smoothing), Smooth(raw, Smoothing));
    }

    [Fact]
    public void Pressure_is_left_alone_unless_it_is_asked_for()
    {
        var filter = new PathSmoother();
        var quiet = Smoothing with { Pressure = 0 };
        var loud = Smoothing with { Pressure = StrokeSmoothing.DefaultDistance };

        var path = Tremor(2.0);
        double Pressure(int i) => i % 2 == 0 ? 0.2 : 0.9;

        double lastUnsmoothed = 0;
        for (int i = 0; i < path.Count; i++)
            lastUnsmoothed = filter.Next(At(path[i], Pressure(i)), quiet).RawPressure;

        filter.Reset();
        double lastSmoothed = 0;
        for (int i = 0; i < path.Count; i++)
            lastSmoothed = filter.Next(At(path[i], Pressure(i)), loud).RawPressure;

        Assert.Equal(Pressure(path.Count - 1), lastUnsmoothed, precision: 9);
        Assert.NotEqual(Pressure(path.Count - 1), lastSmoothed, precision: 3);
        Assert.InRange(lastSmoothed, 0.2, 0.9);
    }

    [Fact]
    public void Each_channel_is_filtered_with_its_own_reach()
    {
        // The two reaches are deliberately far apart. Wherever they are equal, a build that used
        // one of them for both channels is indistinguishable from a correct one, which is exactly
        // how an earlier version of this check passed while proving nothing.
        //
        // Tail aggressiveness is off here, and has to be: it widens the step wherever pressure is
        // falling, and a pressure that alternates every sample makes it let go on every other one,
        // which collapses both windows and hides the reaches this is trying to measure.
        var path = Tremor(2.0);
        double Jumpy(int i) => i % 2 == 0 ? 0.2 : 0.9;

        (DocumentPoint Position, double Pressure) Run(StrokeSmoothing s)
        {
            var filter = new PathSmoother();
            PathSmoother.Filtered last = default;
            for (int i = 0; i < path.Count; i++) last = filter.Next(At(path[i], Jumpy(i)), s);
            return (last.Position, last.RawPressure);
        }

        // A long path reach and a negligible pressure one: the line is pulled well behind the pen
        // while the pressure still reports whatever the last sample said.
        var steadyPath = Run(Smoothing with { Position = 200, Pressure = 2 });
        Assert.True(Distance(steadyPath.Position, path[^1]) > 10,
            $"the path should lag a long way at reach 200, it lagged {Distance(steadyPath.Position, path[^1]):F2}");
        Assert.Equal(Jumpy(path.Count - 1), steadyPath.Pressure, precision: 1);

        // And the other way round.
        var steadyPressure = Run(Smoothing with { Position = 2, Pressure = 200 });
        Assert.True(Distance(steadyPressure.Position, path[^1]) < 4,
            $"the path should barely move at reach 2, it moved {Distance(steadyPressure.Position, path[^1]):F2}");
        Assert.InRange(steadyPressure.Pressure, 0.45, 0.65);   // flattened toward the mean of 0.55
    }

    [Fact]
    public void The_stroke_ends_a_little_short_of_where_the_pen_lifted()
    {
        // Krita's behaviour, kept on purpose. The filter lags and nothing runs it out to the last
        // raw position; doing so would put an unfiltered hook on the end of every stroke. Pinned
        // so the shortfall is a decision on record rather than something discovered later.
        var raw = new List<DocumentPoint>();
        for (double d = 0; d <= 200; d += 2) raw.Add(new DocumentPoint(OriginX + d, Baseline));
        for (double d = 2; d <= 60; d += 2) raw.Add(new DocumentPoint(OriginX + 200, Baseline + d));

        var smoothed = Smooth(raw, Smoothing);

        double shortfall = Math.Abs(raw[^1].Y - smoothed[^1].Y);
        Assert.True(shortfall > 1, $"expected the filter to lag, it ended {shortfall:F2} short");
        Assert.True(shortfall < 40, $"but not by this much: {shortfall:F2}");
    }
}
