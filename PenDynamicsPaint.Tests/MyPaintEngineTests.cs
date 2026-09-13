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

    /// <summary>
    /// A brush settings around a MyPaint brush, composited as asked.
    /// </summary>
    /// <remarks>
    /// Direct by default because most of these measure one stroke's own marks against each other,
    /// and Wash flattens exactly that: it takes the greater alpha of overlapping marks, so a test
    /// of how dabs accumulate would be measuring the compositing instead.
    /// </remarks>
    private static BrushSettings Using(MyPaintBrush brush,
                                       StrokeCompositing compositing = StrokeCompositing.Direct) =>
        new()
        {
            Name = "mypaint",
            Engine = BrushEngineKind.MyPaint,
            MyPaint = brush,
            Compositing = compositing,
        };

    /// <summary>Draw a straight stroke, with control over what the pen reports along it.</summary>
    private static PaintSession Stroke(MyPaintBrush brush, int samples = 60, double step = 6,
                                       double microsecondsPerSample = 10_000,
                                       Func<int, PenOrientation>? orientation = null,
                                       double pressure = 0.6)
    {
        var session = new PaintSession(700, 300);
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

        using var session = new PaintSession(700, 300);
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

    /// <summary>A soft brush laying dabs a couple of radii apart, as a real one does.</summary>
    /// <summary>A brush of soft, overlapping dabs -- the shape of a real MyPaint brush.</summary>
    /// <param name="perRadius">
    /// Dabs per radius of travel, so how thickly the dabs are laid over each other.
    /// </param>
    /// <remarks>
    /// <c>opaque_linearize</c> is switched off here, and that is worth saying rather than leaving
    /// to be noticed: its default is 0.9, so a brush that never mentions it still gets it, and it
    /// thins each dab by however many of them are piling up. That is exactly the quantity the
    /// tests below vary, so leaving it on would have the engine cancelling out the thing being
    /// measured. It has its own test.
    /// </remarks>
    private static MyPaintBrush SoftDabs(double hardness, double perRadius, double opaque) =>
        MyPaintBrush.Parse($$"""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 2.1 },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": {{opaque}} },
                "opaque_linearize": { "base_value": 0.0 },
                "hardness": { "base_value": {{hardness}} },
                "dabs_per_actual_radius": { "base_value": {{perRadius}} }
              }
            }
            """, "soft");

    /// <summary>The ink along the middle of a straight stroke, as 0 (black) to 255 (white).</summary>
    private static List<int> AlongTheMiddle(MyPaintBrush brush, StrokeCompositing compositing,
                                            double step = 6, int samples = 90)
    {
        using var session = new PaintSession(700, 300);
        var settings = Using(brush, compositing);

        // Constant pressure and an even step, so anything that varies along the stroke is a fault
        // of the engine rather than of the input.
        for (int i = 0; i < samples; i++) session.AddSample(40 + i * step, 150, 0.7, settings);
        session.EndStroke();

        var row = new List<int>();
        for (int x = 200; x < 420; x++) row.Add(session.Bitmap.GetPixel(x, 150).Red);
        return row;
    }

    [Fact]
    public void A_washed_stroke_does_not_grow_a_comb_along_its_edge()
    {
        // Wash takes the greater alpha of two overlapping marks. That is right for a swept taper,
        // where the hundred marks over each pixel are meant to be one stroke, and wrong for dabs
        // meant to build up: the greater of two soft neighbours dips between their centres, so the
        // stroke grows a ripple at exactly the dab spacing.
        //
        // Found by loading a real MyPaint brush and looking. Every test here passed while it was
        // happening, because they all measure how wide or how dark a stroke is and none of them
        // looked along one.
        var brush = SoftDabs(hardness: 0.4, perRadius: 1.5, opaque: 0.9);

        int washed = Ripple(AlongTheMiddle(brush, StrokeCompositing.Wash));
        int direct = Ripple(AlongTheMiddle(brush, StrokeCompositing.Direct));

        // Direct paints each dab straight onto the layer and is the reference: whatever ripple the
        // dab spacing leaves there is the brush's own, and Wash should not add to it.
        Assert.True(washed <= direct + 8,
            $"the washed stroke ripples by {washed}/255 against {direct}/255 painted directly, " +
            "which is the dab spacing showing through");

        static int Ripple(List<int> row) => row.Max() - row.Min();
    }

    [Fact]
    public void Laying_more_dabs_over_the_same_ground_lays_more_ink()
    {
        // Why the comb happens, stated as the thing that is actually wrong. Taking the greater
        // alpha caps a stroke at one dab's worth of ink however many dabs cross it, so a brush
        // that asks for dabs three times as thickly gets no more ink for them -- and the MyPaint
        // brush files set their opacity against a model that accumulates, so they come out faint.
        //
        // This is the stronger half of the pair: it fails on the cause rather than on a threshold
        // for how visible the symptom happens to be with one set of numbers.
        int Ink(double perRadius) =>
            AlongTheMiddle(SoftDabs(hardness: 0.4, perRadius: perRadius, opaque: 0.5),
                           StrokeCompositing.Wash).Min();

        int sparse = Ink(0.8);
        int dense = Ink(2.5);

        Assert.True(sparse - dense > 30,
            $"three times the dabs should lay visibly more ink: {sparse}/255 against {dense}/255");
    }

    [Fact]
    public void A_soft_dab_is_no_fainter_than_a_hard_one_of_the_same_strength()
    {
        // A soft dab carries its falloff in a shader and its strength in the paint's alpha, and
        // Skia multiplies the two together. Building the strength into the shader as well squares
        // it, so a dab asked for at 40% arrives at 16% -- which reads as a brush that is merely
        // too faint, and is why this compares a soft dab against a hard one rather than against a
        // number.
        //
        // The dabs have to be spaced apart for this to be measurable at all. The first version of
        // this test drew them overlapping, where forty of them pile up to solid ink whatever each
        // one contributed, and it could not tell half strength from a quarter.
        int Darkest(double hardness)
        {
            using var session = new PaintSession(700, 300);
            var settings = Using(SoftDabs(hardness, perRadius: 0.3, opaque: 0.4));

            for (int i = 0; i < 40; i++) session.AddSample(40 + i * 10, 150, 0.7, settings);
            session.EndStroke();

            int darkest = 255;
            for (int x = 100; x < 400; x++)
                darkest = Math.Min(darkest, session.Bitmap.GetPixel(x, 150).Red);
            return darkest;
        }

        // Hardness 1 takes the no-shader path, where the strength can only be applied once, so it
        // is the reference. A hair below takes the shader path, and at a dab's centre the falloff
        // is 1 in both, so the ink should match.
        int hard = Darkest(1.0);
        int nearlyHard = Darkest(0.98);

        Assert.True(Math.Abs(hard - nearlyHard) <= 5,
            $"the soft dab came out at {nearlyHard}/255 against {hard}/255 for the hard one");
    }

    /// <summary>A half-opaque brush of hard dabs, with the pile-up correction set as given.</summary>
    private static MyPaintBrush HalfOpaque(double linearize, double perRadius) =>
        MyPaintBrush.Parse($$"""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 2.5 },
                "opaque": { "base_value": 0.5 },
                "opaque_multiply": { "base_value": 1.0 },
                "opaque_linearize": { "base_value": {{linearize}} },
                "hardness": { "base_value": 1.0 },
                "dabs_per_actual_radius": { "base_value": {{perRadius}} }
              }
            }
            """, "half");

    /// <summary>The ink in the middle of a straight stroke, as 0 (black) to 255 (white).</summary>
    private static int InkAtTheMiddle(MyPaintBrush brush)
    {
        using var session = new PaintSession(700, 300);
        var settings = Using(brush);
        for (int i = 0; i < 90; i++) session.AddSample(40 + i * 6, 150, 0.7, settings);
        session.EndStroke();
        return session.Bitmap.GetPixel(350, 150).Red;
    }

    [Fact]
    public void A_stroke_reaches_the_opacity_it_asked_for_however_many_dabs_it_took()
    {
        // opaque_linearize. The opacity settings state what the *stroke* should come to, not what
        // one dab should, and a brush lays several dabs over every pixel -- so each dab has to go
        // down fainter or the stroke overshoots. The airbrush asks for 52% and lays 11.5 dabs per
        // pixel; at 52% each it is solid black by the third, which is how this was found.
        //
        // Hard dabs and a flat pressure, so the only thing between the setting and the pixel is
        // the correction. Half-opaque black ink on white paper should come out near 128.
        Assert.InRange(InkAtTheMiddle(HalfOpaque(linearize: 1.0, perRadius: 3.0)), 120, 165);

        // And the same however thickly the dabs are laid, which is the whole point of it.
        int sparse = InkAtTheMiddle(HalfOpaque(linearize: 1.0, perRadius: 1.5));
        int dense = InkAtTheMiddle(HalfOpaque(linearize: 1.0, perRadius: 6.0));
        Assert.True(Math.Abs(sparse - dense) < 20,
            $"four times the dabs changed the stroke: {sparse}/255 against {dense}/255");

        // Uncorrected, six dabs at half opacity each leave a fortieth of the paper showing. Far
        // outside the window above, so none of this can pass by accident.
        Assert.InRange(InkAtTheMiddle(HalfOpaque(linearize: 0.0, perRadius: 3.0)), 0, 45);
    }

    [Fact]
    public void A_partial_pile_up_correction_is_measured_from_no_correction_at_all()
    {
        // The setting runs 0 to 1 and libmypaint interpolates the *pile* it corrects for, from 1
        // dab at 0 to the real count at 1: dabs_per_pixel = 1 + linearize * (dabs_per_pixel - 1).
        // Multiplying the count by the setting instead is the obvious misreading and agrees
        // exactly at both ends, so a test that only ever sets 0 or 1 cannot tell them apart --
        // which is what the first version of the test above did.
        int ink = InkAtTheMiddle(HalfOpaque(linearize: 0.25, perRadius: 1.5));

        // Interpolating gives a pile of 1.75 and lands near 85; scaling gives 0.75, less than no
        // correction at all, and lands near 40.
        Assert.InRange(ink, 70, 100);
    }

    /// <summary>
    /// A brush whose radius is driven only through the custom input, which pressure feeds.
    /// </summary>
    /// <remarks>
    /// Pressure reaches the mark by this one route and no other -- the opacity settings are fixed
    /// -- so a stroke that changes width proves the custom input carried it. Hard dabs, so the
    /// width being measured is the dab's own and not the point its falloff crosses a threshold.
    /// </remarks>
    private static MyPaintBrush CustomDriven(double slowness) =>
        MyPaintBrush.Parse($$"""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 2.0,
                                        "inputs": { "custom": [[0.0, -0.8], [1.0, 0.8]] } },
                "custom_input": { "base_value": 0.0,
                                  "inputs": { "pressure": [[0.0, 0.0], [1.0, 1.0]] } },
                "custom_input_slowness": { "base_value": {{slowness}} },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 1.0 },
                "opaque_linearize": { "base_value": 0.0 },
                "hardness": { "base_value": 1.0 },
                "dabs_per_actual_radius": { "base_value": 6.0 }
              }
            }
            """, "custom");

    /// <summary>A stroke that steps from light to heavy pressure half way along.</summary>
    private static PaintSession PressureStep(MyPaintBrush brush)
    {
        var session = new PaintSession(900, 300);
        var settings = Using(brush);

        for (int i = 0; i < 140; i++)
            session.AddSample(40 + i * 6, 150, i < 70 ? 0.1 : 0.95, settings, default,
                              (long)(i * 10_000));

        session.EndStroke();
        return session;
    }

    [Fact]
    public void The_custom_input_reaches_the_mark()
    {
        // The odd input out: the others are read off the pen and this one off the brush, from a
        // setting that can itself be driven by any of the others. A brush uses it to build a
        // quantity the input list does not offer and then drive several settings from it -- the
        // airbrush shrinks its radius from a pressure slowed this way, and before this went in the
        // panel reported the curve as unused and the brush did not change width at all.
        using var session = PressureStep(CustomDriven(slowness: 0));

        int light = InkHeight(session, 300);
        int heavy = InkHeight(session, 830);

        Assert.True(heavy > light * 2.5,
            $"the stroke should widen through the custom input: {light} px then {heavy} px");
    }

    [Fact]
    public void The_custom_input_lags_by_its_own_slowness()
    {
        // custom_input_slowness, and the reason the input is a state rather than a reading. A
        // brush sets it to follow pressure slowly, so that a jab does not snap the radius across.
        int InkAfterTheStep(double slowness)
        {
            using var session = PressureStep(CustomDriven(slowness));
            int total = 0;
            for (int x = 460; x < 600; x++) total += InkHeight(session, x);
            return total;
        }

        int prompt = InkAfterTheStep(0);
        int lagged = InkAfterTheStep(5.0);

        Assert.True(lagged < prompt * 0.85,
            $"a slow custom input should take its time widening: {lagged} against {prompt}");

        // And it is a lag, not a smaller brush: given the rest of the stroke it arrives anyway.
        // This is the half that fails if the decay is fed the dab's real interval instead of
        // libmypaint's fixed 0.1 -- the lag then runs ten times too long and never catches up.
        using var slow = PressureStep(CustomDriven(5.0));
        using var quick = PressureStep(CustomDriven(0));
        Assert.True(InkHeight(slow, 830) >= InkHeight(quick, 830) - 3,
            "by the end of the stroke the slow brush should have caught up");
    }

    [Fact]
    public void The_custom_input_a_dab_sees_is_the_one_from_the_dab_before_it()
    {
        // libmypaint fills the input array from the states, then evaluates the settings, then
        // advances the states -- so custom_input, which is a setting, only reaches the input on
        // the following dab. A one-dab lag sounds like nothing, and on a brush laying six dabs to
        // the radius it is nothing; the reason to pin it is that the brushes using this input are
        // the ones laying dabs far apart, where one dab is the whole visible unit.
        //
        // Sparse hard dabs and opacity driven straight off the custom input, so each dab is its
        // own mark and reads as light or dark with nothing in between.
        var brush = MyPaintBrush.Parse("""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 1.8 },
                "custom_input": { "base_value": 0.0,
                                  "inputs": { "pressure": [[0.0, 0.0], [1.0, 1.0]] } },
                "custom_input_slowness": { "base_value": 0.0 },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 0.0,
                                     "inputs": { "custom": [[0.0, 0.05], [1.0, 1.0]] } },
                "opaque_linearize": { "base_value": 0.0 },
                "hardness": { "base_value": 1.0 },
                "dabs_per_actual_radius": { "base_value": 0.5 }
              }
            }
            """, "order");

        using var session = new PaintSession(900, 300);
        var settings = Using(brush);

        const int StepX = 340;
        for (int i = 0; i < 100; i++)
            session.AddSample(40 + i * 6, 150, i < 50 ? 0.1 : 0.95, settings, default,
                              (long)(i * 10_000));
        session.EndStroke();

        int firstDark = 0;
        while (firstDark < 900 && session.Bitmap.GetPixel(firstDark, 150).Red > 100) firstDark++;

        // The pen presses harder at StepX and the dab there still carries the reading before it,
        // so the ink darkens one dab later. Advancing the state first puts it one dab early
        // instead, which lands before the step rather than after it.
        Assert.InRange(firstDark, StepX + 1, StepX + 30);
    }

    /// <summary>A nib: hard dabs, a given aspect ratio and a given angle for its long axis.</summary>
    private static MyPaintBrush Nib(double ratio, double angle, double perRadius = 6.0) =>
        MyPaintBrush.Parse($$"""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 3.2 },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 1.0 },
                "opaque_linearize": { "base_value": 0.0 },
                "hardness": { "base_value": 1.0 },
                "elliptical_dab_ratio": { "base_value": {{ratio}} },
                "elliptical_dab_angle": { "base_value": {{angle}} },
                "dabs_per_actual_radius": { "base_value": {{perRadius}} }
              }
            }
            """, "nib");

    /// <summary>How thick a straight horizontal stroke comes out, measured across it.</summary>
    private static int ThicknessOfAHorizontalStroke(MyPaintBrush brush)
    {
        using var session = new PaintSession(600, 400);
        var settings = Using(brush);

        for (int i = 0; i < 60; i++) session.AddSample(80 + i * 7, 200, 0.8, settings);
        session.EndStroke();

        return InkHeight(session, 300);
    }

    [Fact]
    public void An_elliptical_dab_is_narrower_across_the_nib()
    {
        // radius_logarithmic 3.2 is a radius of about 24.5, so a round dab draws a stroke about 49
        // across. The ratio squeezes the short axis and leaves the long one alone, which is the
        // half that is easy to get backwards: it makes a narrower nib, not a bigger dab.
        int round = ThicknessOfAHorizontalStroke(Nib(ratio: 1, angle: 0));
        int flat = ThicknessOfAHorizontalStroke(Nib(ratio: 3, angle: 0));

        Assert.InRange(round, 44, 56);

        // A third of it, give or take the antialiased rim at each edge.
        Assert.InRange(flat, 13, 23);
    }

    [Fact]
    public void The_dab_angle_turns_the_nib()
    {
        // The same flattened dab stood on end. A brush that read the ratio and ignored the angle
        // would draw both of these the same way, and the test above alone would not notice.
        int along = ThicknessOfAHorizontalStroke(Nib(ratio: 3, angle: 0));
        int across = ThicknessOfAHorizontalStroke(Nib(ratio: 3, angle: 90));

        Assert.True(across > along * 2,
            $"turning the nib should widen the stroke: {along} px against {across} px");

        // And 45 degrees lands between the two rather than snapping to one of them, which is what
        // separates a real rotation from a test of whether the angle is nearer 0 or 90.
        int diagonal = ThicknessOfAHorizontalStroke(Nib(ratio: 3, angle: 45));
        Assert.InRange(diagonal, along + 4, across - 4);
    }

    [Fact]
    public void The_nib_leans_the_way_libmypaint_leans_it()
    {
        // Which way 45 degrees points. Thickness alone cannot say: a horizontal stroke is squeezed
        // by the same amount whether the nib leans one way or the other, so both signs pass the
        // test above. Running the stroke diagonally is what separates them.
        int Ink(int dy)
        {
            using var session = new PaintSession(600, 600);
            var settings = Using(Nib(ratio: 4, angle: 45));

            for (int i = 0; i < 50; i++)
                session.AddSample(150 + i * 6, 300 + dy * i * 6, 0.8, settings);
            session.EndStroke();

            int n = 0;
            for (int x = 0; x < 600; x++) n += InkHeight(session, x);
            return n;
        }

        // libmypaint's long axis at 45 degrees runs down and to the right, so a stroke drawn that
        // way is dragging the nib along its length and leaves a narrow trail. Drawn the other way
        // the nib is broadside to the travel and leaves a wide one.
        int alongTheNib = Ink(1);
        int broadside = Ink(-1);

        Assert.True(broadside > alongTheNib * 2,
            $"the nib leans the wrong way: {alongTheNib} px of ink along it, {broadside} across");
    }

    [Fact]
    public void Dabs_pack_closer_across_the_nib_than_along_it()
    {
        // libmypaint stretches the step by the aspect ratio across the narrow axis before counting
        // dabs into it, so spacing is measured in the dab's own metric rather than on the page.
        // That is the difference between a nib and an oval stamp: dragged sideways it lays dabs as
        // densely as its narrow width needs, and dragged along its length it does not waste them.
        (int Marks, int Ink) Measure(bool vertical)
        {
            using var session = new PaintSession(500, 500);

            // A third of a dab per radius, so the gap is three radii and the dabs land clear of one
            // another along the nib -- there is something to count rather than one smear.
            var settings = Using(Nib(ratio: 4, angle: 0, perRadius: 0.3));

            for (int i = 0; i < 80; i++)
                if (vertical) session.AddSample(250, 90 + i * 4, 0.8, settings);
                else session.AddSample(90 + i * 4, 250, 0.8, settings);
            session.EndStroke();

            int marks = 0;
            bool inMark = false;
            for (int k = 0; k < 500; k++)
            {
                var pixel = vertical ? session.Bitmap.GetPixel(250, k)
                                     : session.Bitmap.GetPixel(k, 250);
                bool ink = pixel != SKColors.White;
                if (ink && !inMark) marks++;
                inMark = ink;
            }

            int total = 0;
            for (int x = 0; x < 500; x++) total += InkHeight(session, x);
            return (marks, total);
        }

        // Angle 0 lays the long axis horizontal, so a vertical stroke crosses the narrow one.
        var along = Measure(vertical: false);
        var across = Measure(vertical: true);

        // Counting separate marks says they really are distinct dabs rather than one smear. It is
        // not enough on its own: dabs packed closer than their own width merge into a single run,
        // so the count can fall as the spacing tightens. The first version of this test compared
        // only the counts and passed with the stretch applied to the wrong axis, where the dabs
        // along the nib overlapped into one mark and so counted as fewer rather than more.
        Assert.InRange(along.Marks, 2, 6);
        Assert.InRange(across.Marks, 9, 22);

        // Ink is the measure that only goes one way: more dabs over the same travel is more ink,
        // whether or not they have started to touch.
        Assert.True(across.Ink > along.Ink * 2.5,
            $"dabs should pack closer across the nib: {along.Ink} px of ink along it against " +
            $"{across.Ink} across, from {along.Marks} and {across.Marks} separate marks");
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
