using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Drawing.MyPaint;
using PenDynamicsPaint.Paint;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// That a MyPaint brush's inputs reach the ink: tilt, speed, direction and the rest.
/// </summary>
/// <remarks>
/// <para>
/// The point of the model is that a brush can respond to more than pressure. Every test here
/// therefore holds pressure fixed and varies something else, because a brush that only ever
/// answered to pressure would pass any test that let pressure move.
/// </para>
/// <para>
/// Tilt in particular is worth stating: the orientation on a sample has been recorded since the
/// first version of the stroke model and until now nothing read it.
/// </para>
/// </remarks>
public class MyPaintEngineTests
{
    /// <summary>A brush whose radius is driven by one input and nothing else.</summary>
    private static MyPaintBrush DrivenBy(string input, double low, double high,
                                         double from = 0, double to = 1) =>
        MyPaintBrush.Parse($$"""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 2.0,
                                        "inputs": { "{{input}}": [[{{from}}, {{low}}],
                                                                  [{{to}}, {{high}}]] } },
                "opaque":          { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 1.0 },
                "hardness":        { "base_value": 1.0 },
                "dabs_per_actual_radius": { "base_value": 6.0 }
              }
            }
            """, input);

    private static BrushSettings Using(MyPaintBrush brush) => new()
    {
        Name = "mypaint",
        Engine = BrushEngineKind.MyPaint,
        MyPaint = brush,
    };

    /// <summary>Draw a straight stroke, with control over what the pen reports along it.</summary>
    private static PaintSession Stroke(MyPaintBrush brush, int samples = 60, double step = 6,
                                       double microsecondsPerSample = 10_000,
                                       Func<int, PenOrientation>? orientation = null,
                                       double pressure = 0.6)
    {
        var session = new PaintSession(700, 300) { Compositing = StrokeCompositing.Direct };
        var settings = Using(brush);

        for (int i = 0; i < samples; i++)
        {
            session.AddSample(40 + i * step, 150, pressure, settings,
                              orientation?.Invoke(i) ?? default,
                              (long)(i * microsecondsPerSample));
        }

        session.EndStroke();
        return session;
    }

    /// <summary>How tall the ink is at a column: the mark's width, in other words.</summary>
    private static int InkHeight(PaintSession session, int x)
    {
        int n = 0;
        for (int y = 0; y < session.Height; y++)
            if (session.Bitmap.GetPixel(x, y) != SKColors.White) n++;
        return n;
    }

    [Fact]
    public void A_brush_with_no_curves_lays_down_an_even_stroke()
    {
        // The control for everything below. If the engine drew nothing, or drew something that
        // wandered on its own, every comparison after this would be measuring noise.
        using var session = Stroke(DrivenBy("pressure", 0, 0));

        int left = InkHeight(session, 120);
        int right = InkHeight(session, 300);

        Assert.True(left > 8, $"the stroke should be visible, it was {left} px tall");
        Assert.True(Math.Abs(left - right) <= 2, $"and even: {left} px against {right} px");
    }

    [Fact]
    public void Tilt_reaches_the_mark()
    {
        // Declination runs 0 to 90 degrees. The brush is told to grow with it, and the pen is
        // tilted further over as the stroke goes on. Nothing else changes.
        using var session = Stroke(
            DrivenBy("tilt_declination", 0, 1.2, from: 0, to: 90),
            orientation: i => new PenOrientation(
                Azimuth: 0,
                Altitude: 90 - i * 1.4,     // upright at the start, leaning over by the end
                Twist: 0, TiltX: 0, TiltY: 0));

        int upright = InkHeight(session, 120);
        int leaning = InkHeight(session, 300);

        Assert.True(leaning > upright * 1.5,
            $"the brush should widen as the pen leans: {upright} px then {leaning} px");
    }

    [Fact]
    public void Barrel_rotation_reaches_the_mark()
    {
        using var session = Stroke(
            DrivenBy("barrel_rotation", 0, 1.2, from: 0, to: 360),
            orientation: i => new PenOrientation(0, 90, Twist: i * 6.0, 0, 0));

        Assert.True(InkHeight(session, 300) > InkHeight(session, 120) * 1.5,
            "the brush should widen as the barrel turns");
    }

    [Fact]
    public void Speed_reaches_the_mark()
    {
        // The same path at two report rates a decade apart, so the pen covers the same ground at
        // very different speeds. The brush is told to shrink as speed rises.
        var brush = DrivenBy("speed1", 0.8, -0.8, from: 0, to: 1);

        using var slow = Stroke(brush, microsecondsPerSample: 40_000);
        using var fast = Stroke(brush, microsecondsPerSample: 2_000);

        int slowWidth = InkHeight(slow, 300);
        int fastWidth = InkHeight(fast, 300);

        Assert.True(slowWidth > fastWidth,
            $"a faster stroke should come out thinner: slow {slowWidth} px, fast {fastWidth} px");
    }

    [Fact]
    public void The_stroke_input_runs_along_the_stroke()
    {
        // Zero at the start and climbing with travel, so a brush can taper over its whole length
        // without pressure moving at all.
        //
        // stroke_duration_logarithmic has to be set here, and that is the point rather than a
        // detail: at the default of 4 the input completes a full cycle in a few hundred units, so
        // over a stroke this long it wraps and the two ends read almost the same. The first
        // version of this test did not set it and measured exactly that.
        var brush = MyPaintBrush.Parse(
            """
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 2.0,
                                        "inputs": { "stroke": [[0.0, -1.0], [1.0, 0.6]] } },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 1.0 },
                "hardness": { "base_value": 1.0 },
                "dabs_per_actual_radius": { "base_value": 6.0 },
                "stroke_duration_logarithmic": { "base_value": 4.6 }
              }
            }
            """, "stroke");

        using var session = Stroke(brush, samples: 90);

        int start = InkHeight(session, 80);
        int end = InkHeight(session, 500);

        Assert.True(end > start * 1.5, $"expected a taper along the stroke: {start} px then {end} px");
    }

    [Fact]
    public void The_stroke_input_wraps_rather_than_stopping()
    {
        // libmypaint's behaviour, and what lets the input drive a pattern that repeats along a
        // long stroke rather than only its first part. Worth pinning because it is surprising, and
        // because the obvious alternative -- holding at 1 -- looks identical on a short stroke.
        var tracker = new BrushInputTracker(seed: 1);
        var brush = MyPaintBrush.Default;
        double baseRadius = Math.Exp(brush[MyPaintSetting.RadiusLogarithmic].BaseValue);

        var previous = DocumentPoint.Origin;
        var seen = new List<float>();

        for (int i = 1; i <= 800; i++)
        {
            var at = new DocumentPoint(i * 0.7, 0);
            var sample = new StrokeSample(at, 0.6, PenOrientation.None, 0.6);
            seen.Add(tracker.Next(at, previous, sample, 0.01, brush, baseRadius)[BrushInput.Stroke]);
            previous = at;
        }

        Assert.True(seen.Max() > 0.9, $"the input should climb to the top, it reached {seen.Max():F2}");

        // Somewhere it falls back: that is the wrap. An input held at 1 would never decrease.
        Assert.Contains(Enumerable.Range(1, seen.Count - 1), i => seen[i] < seen[i - 1] - 0.5);
    }

    [Fact]
    public void Hardness_decides_how_far_the_dab_fades()
    {
        // A hard dab is solid to its rim; a soft one fades from its centre. Read as the ink laid
        // down over the same path by the same radius.
        string Brush(double hardness) => $$"""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 2.4 },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 1.0 },
                "hardness": { "base_value": {{hardness}} },
                "dabs_per_actual_radius": { "base_value": 6.0 }
              }
            }
            """;

        double Ink(double hardness)
        {
            using var session = Stroke(MyPaintBrush.Parse(Brush(hardness), "h"), samples: 30);

            double total = 0;
            for (int y = 0; y < session.Height; y++)
                for (int x = 0; x < session.Width; x++)
                {
                    var c = session.Bitmap.GetPixel(x, y);
                    total += (255 - c.Red) / 255.0;      // how much ink, not how many pixels
                }
            return total;
        }

        double hard = Ink(0.99);
        double soft = Ink(0.15);

        Assert.True(hard > 500, $"the hard brush should lay down real ink, it laid {hard:F0}");
        Assert.True(soft < hard * 0.8,
            $"a soft dab fades, so it should lay down less: {soft:F0} against {hard:F0}");
    }

    [Fact]
    public void The_dab_fades_by_the_square_of_the_radius()
    {
        // libmypaint's profile is two straight lines in rr, the *square* of the normalised
        // distance from the centre -- so the dab is not a linear ramp, and treating it as one
        // gives a mark that looks soft, is the wrong shape, and would never be noticed by a test
        // that only compared soft against hard.
        //
        // At hardness 0.5 the first segment is opa = 1 - rr, so half opacity falls at rr = 0.5,
        // which is 0.707 of the radius. Read it as a ramp instead and it would fall at 0.5.
        var brush = MyPaintBrush.Parse(
            """
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 3.4 },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 1.0 },
                "hardness": { "base_value": 0.5 },
                "dabs_per_actual_radius": { "base_value": 0.35 }
              }
            }
            """, "profile");

        using var session = new PaintSession(700, 300) { Compositing = StrokeCompositing.Direct };
        var settings = Using(brush);

        for (int i = 0; i < 30; i++) session.AddSample(60 + i * 12, 150, 0.8, settings);
        session.EndStroke();

        // The darkest column is the middle of a dab; measuring anywhere else would measure the
        // overlap of two.
        int centre = 0;
        double darkest = 255;
        for (int x = 200; x < 500; x++)
        {
            double v = session.Bitmap.GetPixel(x, 150).Red;
            if (v < darkest) { darkest = v; centre = x; }
        }

        double Alpha(int y) => (255 - session.Bitmap.GetPixel(centre, y).Red) / 255.0;

        double peak = Alpha(150);
        Assert.True(peak > 0.5, $"the dab should be solid at its centre, it was {peak:F2}");

        // Walk out from the centre to where the ink has halved, and to where it ends.
        int half = 150, edge = 150;
        while (half < 300 && Alpha(half) > peak / 2) half++;
        while (edge < 300 && Alpha(edge) > 0.02) edge++;

        double radius = edge - 150;
        double halfAt = (half - 150) / radius;

        Assert.True(radius > 20, $"the dab should be big enough to measure, its radius was {radius}");
        Assert.InRange(halfAt, 0.62, 0.78);
    }

    [Fact]
    public void Spacing_follows_the_dab_radius()
    {
        // dabs_per_actual_radius is a count per radius of travel, so asking for fewer leaves gaps.
        // Without this, a brush's spacing setting would be parsed and quietly ignored.
        string Brush(double perRadius) => $$"""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 1.6 },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 1.0 },
                "hardness": { "base_value": 1.0 },
                "dabs_per_actual_radius": { "base_value": {{perRadius}} }
              }
            }
            """;

        int Runs(double perRadius)
        {
            using var session = Stroke(MyPaintBrush.Parse(Brush(perRadius), "s"), samples: 40);

            int runs = 0;
            bool inside = false;
            for (int x = 0; x < session.Width; x++)
            {
                bool ink = session.Bitmap.GetPixel(x, 150) != SKColors.White;
                if (ink && !inside) runs++;
                inside = ink;
            }
            return runs;
        }

        Assert.Equal(1, Runs(8.0));
        Assert.True(Runs(0.35) > 3, $"loose dabs should bead, they gave {Runs(0.35)} run(s)");
    }

    [Fact]
    public void A_stroke_records_the_brush_so_an_undo_replays_it()
    {
        using var session = Stroke(DrivenBy("tilt_declination", 0, 1.2, from: 0, to: 90),
                                   orientation: i => new PenOrientation(0, 90 - i * 1.4, 0, 0, 0));

        using var before = session.Bitmap.Copy();

        // A different brush entirely, drawn elsewhere, then undone.
        var plain = BrushSettings.Default with { Size = 20 };
        for (double x = 40; x <= 400; x += 4) session.AddSample(x, 260, 1.0, plain);
        session.EndStroke();
        Assert.True(session.Undo());

        int changed = 0;
        for (int y = 0; y < 220; y++)
            for (int x = 0; x < session.Width; x++)
                if (before.GetPixel(x, y) != session.Bitmap.GetPixel(x, y)) changed++;

        Assert.Equal(0, changed);
    }
}
