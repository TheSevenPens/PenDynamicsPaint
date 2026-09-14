using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Drawing.MyPaint;
using PenDynamicsPaint.Paint;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// Filtering the pen's orientation, which until now was passed through untouched.
/// </summary>
/// <remarks>
/// <para>
/// The same weighted window the path and the pressure use, over a signal that needs it more:
/// tablets quantise tilt coarsely, so a brush mapping it onto radius turns each step into a
/// visible jump.
/// </para>
/// <para>
/// The two angles that <b>wrap</b> are where this stops being an average and starts being its own
/// problem, and they are most of what is pinned here. Averaging 359 and 1 gives 180, which points
/// the opposite way -- a fault that is invisible anywhere the readings happen not to straddle the
/// wrap, and violent everywhere they do.
/// </para>
/// </remarks>
public class TiltSmoothingTests
{
    private const double Step = 3.0;

    private static StrokeSmoothing Filtering(double tilt) => new()
    {
        Tilt = tilt,
        TailAggressiveness = 0,
    };

    /// <summary>Run a straight stroke whose orientation is given per sample, and read the last one.</summary>
    private static PenOrientation Run(Func<int, PenOrientation> orientation,
                                      StrokeSmoothing smoothing, int samples = 120)
    {
        var filter = new PathSmoother();
        var last = PenOrientation.None;

        for (int i = 0; i < samples; i++)
        {
            var sample = new StrokeSample(new DocumentPoint(20 + i * Step, 100),
                                          1.0, orientation(i));
            last = filter.Next(sample, smoothing).Orientation;
        }

        return last;
    }

    /// <summary>How far apart two angles are, the short way round.</summary>
    private static double Apart(double a, double b)
    {
        double d = Math.Abs(a - b) % 360.0;
        return d > 180 ? 360 - d : d;
    }

    [Fact]
    public void Tilt_reaches_the_brush_steadied_rather_than_raw()
    {
        // A pen held at a steady lean, with the wrist rocking a couple of degrees each sample --
        // which is what a tablet quantising to whole degrees reports for a hand that is holding
        // still. A brush driving radius from declination turns each of those steps into a jump.
        PenOrientation Rocking(int i) =>
            new(Azimuth: 40, Altitude: 50 + (i % 2 == 0 ? -3 : 3), Twist: 0, TiltX: 0, TiltY: 0);

        var raw = Run(Rocking, StrokeSmoothing.None);
        var filtered = Run(Rocking, Filtering(StrokeSmoothing.DefaultDistance));

        // Unfiltered, the last sample is whatever the pen last said.
        Assert.Equal(Rocking(119).Altitude, raw.Altitude, precision: 9);

        // Filtered, it sits near the middle of the rocking rather than at one extreme of it.
        Assert.InRange(filtered.Altitude, 49, 51);
    }

    [Fact]
    public void An_azimuth_that_crosses_north_is_not_averaged_the_long_way_round()
    {
        // The fault worth having a test for at all. These readings sit either side of 0 degrees,
        // a couple of degrees apart, and a plain numeric average of 358 and 2 is 180 -- the pen
        // reported as leaning due south while it is leaning due north.
        //
        // Nothing about this shows up anywhere the readings stay away from the wrap, which is why
        // it needs its own case rather than being covered by a general jitter test.
        PenOrientation AcrossNorth(int i) =>
            new(Azimuth: i % 2 == 0 ? 358 : 2, Altitude: 30, Twist: 0, TiltX: 0, TiltY: 0);

        var filtered = Run(AcrossNorth, Filtering(StrokeSmoothing.DefaultDistance));

        Assert.True(Apart(filtered.Azimuth, 0) < 5,
            $"the azimuth came out at {filtered.Azimuth:F1} degrees, which is " +
            $"{Apart(filtered.Azimuth, 0):F0} degrees from where the pen was pointing");
    }

    [Fact]
    public void Twist_crosses_the_same_way()
    {
        // Barrel rotation wraps at 360 exactly as the azimuth does, and a brush reading it has the
        // same thing go wrong. Pinned separately because the two are smoothed by separate lines,
        // and a build that handled one and not the other would pass the test above.
        PenOrientation AcrossZero(int i) =>
            new(Azimuth: 0, Altitude: 30, Twist: i % 2 == 0 ? 357 : 3, TiltX: 0, TiltY: 0);

        var filtered = Run(AcrossZero, Filtering(StrokeSmoothing.DefaultDistance));

        Assert.True(Apart(filtered.Twist, 0) < 6,
            $"the twist came out at {filtered.Twist:F1} degrees rather than near 0");
    }

    [Fact]
    public void An_azimuth_read_from_an_upright_pen_does_not_drag_a_leaning_one_around()
    {
        // Near vertical the azimuth is the pole of a spherical coordinate: it is whatever noise
        // the digitiser produces, and a millimetre of wobble swings it through tens of degrees.
        // Averaged in as though it meant something, a few upright samples pull the answer anywhere
        // they like.
        //
        // Here most of the stroke is upright and reading 270, and a few samples are laid over at
        // a consistent 90. The answer should follow the samples that mean something.
        //
        // The upright readings point one consistent way rather than being scattered at random,
        // and that is deliberate: uniform noise averages towards nothing on its own, so it cannot
        // tell a build that discounts those samples from one that weighs them equally. The first
        // version of this used Random and missed exactly that injection. A pen held near vertical
        // does not report a fresh random bearing each sample anyway -- it sits somewhere arbitrary
        // and stays there, which is this.
        PenOrientation MostlyUpright(int i) => i % 8 == 0
            ? new PenOrientation(Azimuth: 90, Altitude: 20, Twist: 0, TiltX: 0, TiltY: 0)
            : new PenOrientation(Azimuth: 270, Altitude: 89.5, Twist: 0, TiltX: 0, TiltY: 0);

        var filtered = Run(MostlyUpright, Filtering(StrokeSmoothing.DefaultDistance));

        Assert.True(Apart(filtered.Azimuth, 90) < 30,
            $"the azimuth came out at {filtered.Azimuth:F0} degrees, dragged away from the 90 " +
            "the pen reported whenever it was leaning far enough for the reading to mean anything");
    }

    [Fact]
    public void A_stroke_drawn_entirely_upright_still_reports_an_azimuth()
    {
        // The other end of that. If every sample is discounted for being upright, the weights all
        // come to nothing and there is no resultant to take an angle from -- so the pen's own
        // reading has to come back rather than a zero, which would be a confident answer made up
        // out of an empty sum.
        PenOrientation Upright(int i) =>
            new(Azimuth: 123, Altitude: 90, Twist: 0, TiltX: 0, TiltY: 0);

        var filtered = Run(Upright, Filtering(StrokeSmoothing.DefaultDistance));

        Assert.Equal(123, filtered.Azimuth, precision: 6);
    }

    [Fact]
    public void The_tilt_reach_is_its_own()
    {
        // The reason it is a third slider rather than riding on one of the others: a brush driven
        // by tilt wants it steadied whether or not the line did. Position off and tilt on has to
        // leave the path exactly as the pen drew it, and the reverse has to leave the tilt alone.
        PenOrientation Rocking(int i) =>
            new(Azimuth: 40, Altitude: 50 + (i % 2 == 0 ? -6 : 6), Twist: 0, TiltX: 0, TiltY: 0);

        var tiltOnly = new StrokeSmoothing
        {
            Tilt = StrokeSmoothing.DefaultDistance,
            TailAggressiveness = 0,
        };

        var pathOnly = new StrokeSmoothing
        {
            Position = StrokeSmoothing.DefaultDistance,
            TailAggressiveness = 0,
        };

        // A wobbling path, so there is something for the position filter to change.
        var filter = new PathSmoother();
        PathSmoother.Filtered Last(StrokeSmoothing smoothing)
        {
            filter.Reset();
            var last = default(PathSmoother.Filtered);

            for (int i = 0; i < 120; i++)
            {
                var at = new DocumentPoint(20 + i * Step, 100 + (i % 2 == 0 ? -4 : 4));
                last = filter.Next(new StrokeSample(at, 1.0, Rocking(i)), smoothing);
            }

            return last;
        }

        var withTilt = Last(tiltOnly);
        var withPath = Last(pathOnly);

        // Tilt on, position off: the path is untouched and the tilt is not.
        //
        // Stated as "pulled at least halfway back towards the middle of the rocking" rather than
        // as a window around 50. How far it gets depends on how much travel each sample covers --
        // the wobble here makes the steps nearly three times longer than a straight stroke's, so
        // fewer samples fall inside the reach and the newest weighs more. A window copied from the
        // straight case fails on arithmetic rather than on anything being wrong.
        Assert.Equal(100 + 4, withTilt.Position.Y, precision: 6);
        Assert.True(Math.Abs(withTilt.Orientation.Altitude - 50) < 3,
            $"the tilt was left at {withTilt.Orientation.Altitude:F1}, barely moved from the " +
            $"{Rocking(119).Altitude} the pen reported");

        // Position on, tilt off: the reverse.
        Assert.NotEqual(100 + 4, withPath.Position.Y, precision: 3);
        Assert.Equal(Rocking(119).Altitude, withPath.Orientation.Altitude, precision: 9);
    }

    [Fact]
    public void The_steadied_tilt_is_what_the_brush_draws_with()
    {
        // Everything above drives the filter directly, which says nothing about whether the
        // session hands the result on. It did not: PaintSession.Filter rebuilt the sample with a
        // smoothed position and pressure and copied the orientation across untouched, so tilt
        // arrived exactly as the tablet reported it however the filter was set. That is the whole
        // fault this change exists to fix, and no test of the filter alone can see it.
        //
        // A MyPaint brush whose radius follows declination, drawn with the pen rocking a few
        // degrees each sample -- so the stroke's own width is the readout.
        var brush = MyPaintBrush.Parse("""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 2.0,
                                        "inputs": { "tilt_declination": [[0.0, -0.7], [90.0, 0.9]] } },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 1.0 },
                "opaque_linearize": { "base_value": 0.0 },
                "hardness": { "base_value": 1.0 },
                "dabs_per_actual_radius": { "base_value": 6.0 }
              }
            }
            """, "tilt");

        int Ripple(double tiltReach)
        {
            var settings = new BrushSettings
            {
                Name = "tilt",
                Engine = BrushEngineKind.MyPaint,
                MyPaint = brush,
                Interpolation = StrokeInterpolation.Straight,
                Smoothing = new StrokeSmoothing { Tilt = tiltReach, TailAggressiveness = 0 },
                Compositing = StrokeCompositing.Direct,
            };

            using var session = new PaintSession(900, 400);

            // A steady hand at a steady lean, with the wrist rocking: the pen alternates between
            // 42 and 58 degrees of altitude, which is a tablet quantising a held position.
            for (int i = 0; i < 130; i++)
            {
                var at = new PenOrientation(Azimuth: 0, Altitude: i % 2 == 0 ? 42 : 58,
                                            Twist: 0, TiltX: 0, TiltY: 0);
                session.AddSample(40 + i * 6, 200, 0.8, settings, at, (long)(i * 10_000));
            }

            session.EndStroke();

            var widths = new List<int>();
            for (int x = 300; x < 600; x++)
            {
                int n = 0;
                for (int y = 0; y < 400; y++)
                    if (session.Bitmap.GetPixel(x, y) != SKColors.White) n++;
                widths.Add(n);
            }

            return widths.Max() - widths.Min();
        }

        int raw = Ripple(0);
        int steadied = Ripple(StrokeSmoothing.DefaultDistance);

        Assert.True(steadied < raw * 0.6,
            $"the stroke's width still swings by {steadied} px with the tilt filtered, " +
            $"against {raw} px unfiltered");
    }

    [Fact]
    public void A_reach_of_zero_leaves_the_orientation_exactly_as_the_pen_gave_it()
    {
        PenOrientation Varying(int i) =>
            new(Azimuth: 10 + i, Altitude: 40 + (i % 3), Twist: i * 2, TiltX: i, TiltY: -i);

        var filtered = Run(Varying, Filtering(0));
        var expected = Varying(119);

        Assert.Equal(expected.Azimuth, filtered.Azimuth, precision: 9);
        Assert.Equal(expected.Altitude, filtered.Altitude, precision: 9);
        Assert.Equal(expected.Twist, filtered.Twist, precision: 9);
        Assert.Equal(expected.TiltX, filtered.TiltX, precision: 9);
        Assert.Equal(expected.TiltY, filtered.TiltY, precision: 9);
    }

    [Fact]
    public void The_raw_tilt_axes_are_steadied_too()
    {
        // TiltX and TiltY are what a backend reports before any of it becomes an altitude, and a
        // brush can be driven from them directly. Leaving them raw while steadying the angles
        // derived from them would be the same fault one level down.
        PenOrientation Jumpy(int i) =>
            new(Azimuth: 0, Altitude: 45, Twist: 0,
                TiltX: i % 2 == 0 ? -20 : 20, TiltY: i % 2 == 0 ? 30 : -30);

        var filtered = Run(Jumpy, Filtering(StrokeSmoothing.DefaultDistance));

        Assert.InRange(filtered.TiltX, -6, 6);
        Assert.InRange(filtered.TiltY, -6, 6);
    }
}
