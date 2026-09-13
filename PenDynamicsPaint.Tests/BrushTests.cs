using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Paint;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// That a brush really is what decides the mark, and that a stroke keeps the brush that drew it.
/// </summary>
/// <remarks>
/// <para>
/// The engine, the spacing and the curve used to be application state. A stroke recorded only the
/// handful of settings that happened to live on <c>BrushSettings</c>, so switching brush and then
/// undoing redrew older strokes in the new brush's style -- work changing under a command whose
/// whole job is to step backwards.
/// </para>
/// <para>
/// So the tests here are mostly about <b>identity over time</b>: what a stroke looks like after
/// something else has changed. They read the ink rather than the record, because a stroke that
/// stores the right brush and replays through the wrong one would pass any test of the record.
/// </para>
/// </remarks>
public class BrushTests
{
    /// <summary>A solid ribbon: one mark per segment, no gaps.</summary>
    private static readonly BrushSettings Taper = new()
    {
        Name = "taper",
        Engine = BrushEngineKind.Taper,
        Size = 20,
        PressureDrives = PressureControl.Size,
    };

    /// <summary>Beads: marks two diameters apart, so the gaps are unmistakable.</summary>
    private static readonly BrushSettings Beads = new()
    {
        Name = "beads",
        Engine = BrushEngineKind.Dabs,
        Size = 20,
        Spacing = 2.0,
        PressureDrives = PressureControl.Size,
    };

    private static void Stroke(PaintSession s, BrushSettings brush, double y, double pressure = 1.0)
    {
        for (double x = 20; x <= 220; x += 2) s.AddSample(x, y, pressure, brush);
        s.EndStroke();
    }

    /// <summary>How many separate runs of ink lie along one row. A ribbon gives 1; beads give many.</summary>
    private static int InkRuns(PaintSession session, int y)
    {
        int runs = 0;
        bool inside = false;

        for (int x = 0; x < session.Width; x++)
        {
            bool ink = session.Bitmap.GetPixel(x, y) != SKColors.White;
            if (ink && !inside) runs++;
            inside = ink;
        }

        return runs;
    }

    /// <summary>How many pixels of ink lie down one column. The mark's width, in other words.</summary>
    private static int InkHeight(PaintSession session, int x)
    {
        int n = 0;
        for (int y = 0; y < session.Height; y++)
            if (session.Bitmap.GetPixel(x, y) != SKColors.White) n++;
        return n;
    }

    [Fact]
    public void The_two_engines_are_told_apart_by_the_ink()
    {
        // The control everything below depends on. If a beaded stroke did not actually bead, the
        // identity tests would be comparing two things that look the same and proving nothing.
        using var ribbon = new PaintSession(240, 200);
        Stroke(ribbon, Taper, 100);
        Assert.Equal(1, InkRuns(ribbon, 100));

        using var beaded = new PaintSession(240, 200);
        Stroke(beaded, Beads, 100);
        Assert.True(InkRuns(beaded, 100) > 3,
            $"expected the dabs to bead, got {InkRuns(beaded, 100)} run(s)");
    }

    [Fact]
    public void An_undo_replays_each_stroke_with_the_brush_that_drew_it()
    {
        // The fault this whole change exists to remove.
        //
        // Two survivors, drawn with different engines, is the point. With only one there is always
        // some single engine a broken replay could pick that happens to be right -- the current
        // brush, or the default -- and the test would pass while the behaviour was absent. Getting
        // both back means the choice was made per stroke.
        using var session = new PaintSession(240, 200);

        Stroke(session, Beads, 40);
        Stroke(session, Taper, 100);
        Stroke(session, Beads, 160);

        Assert.True(session.Undo());

        Assert.True(InkRuns(session, 40) > 3,
            $"the beaded survivor came back as {InkRuns(session, 40)} run(s), so the replay did " +
            "not use its own brush");
        Assert.Equal(1, InkRuns(session, 100));
        Assert.Equal(0, InkRuns(session, 160));
    }

    [Fact]
    public void Changing_the_brush_mid_stroke_does_not_change_the_stroke()
    {
        // The brush is read at the first sample and held for the rest. Otherwise the marks would
        // follow the picker while the recorded stroke kept the brush it started with, and an undo
        // would redraw something that had never been on the canvas.
        //
        // The two brushes differ in size rather than in engine, deliberately. The engine is pinned
        // at stroke start whatever happens next, so two engines would look identical here and the
        // test would pass without the pinning it is meant to check. A width can be counted.
        using var session = new PaintSession(240, 200);

        var thin = Taper with { Size = 16 };
        var fat = Taper with { Size = 80 };

        for (double x = 20; x <= 120; x += 2) session.AddSample(x, 100, 1.0, thin);
        for (double x = 122; x <= 220; x += 2) session.AddSample(x, 100, 1.0, fat);
        session.EndStroke();

        Assert.Single(session.History.Strokes);
        Assert.Equal(16, session.History.Strokes[0].Brush.Size);

        int atStart = InkHeight(session, 60);
        int atEnd = InkHeight(session, 180);
        Assert.True(Math.Abs(atStart - atEnd) <= 1,
            $"the stroke changed width under the pen: {atStart} px then {atEnd} px");
        Assert.InRange(atEnd, 14, 18);
    }

    [Fact]
    public void A_brush_carries_its_own_curve_into_the_mark()
    {
        // Two brushes, the same pen. One saturates at half pressure, so at a quarter it is already
        // at half strength; the other is linear and is at a quarter. Pressure drives size, so the
        // difference is a width that can be counted.
        var linear = Taper with { Size = 80, Curve = PressureCurve.Linear };
        var early = Taper with { Size = 80, Curve = new PressureCurve(0, 0.5, 1.0) };

        using var a = new PaintSession(240, 200);
        Stroke(a, linear, 100, pressure: 0.25);

        using var b = new PaintSession(240, 200);
        Stroke(b, early, 100, pressure: 0.25);

        int thin = InkHeight(a, 120);
        int thick = InkHeight(b, 120);

        Assert.InRange(thin, 18, 22);      // 0.25 x 80
        Assert.InRange(thick, 38, 42);     // 0.50 x 80
    }

    [Fact]
    public void A_stroke_keeps_its_curve_when_the_brush_is_edited()
    {
        // Editing a brush changes what you draw next, not what is already on the canvas. The
        // session applies the curve as samples arrive and the stroke keeps the whole brush, so a
        // later edit has nothing to reach back into.
        using var session = new PaintSession(240, 200);

        var original = Taper with { Size = 80, Curve = PressureCurve.Linear };
        Stroke(session, original, 60, pressure: 0.25);
        int before = InkHeight(session, 120);

        // The same brush, edited -- which with a record means a different one.
        var edited = original with { Curve = new PressureCurve(0, 0.5, 1.0) };
        Stroke(session, edited, 160, pressure: 0.25);
        Assert.True(session.Undo());

        Assert.Equal(before, InkHeight(session, 120));
    }

    [Fact]
    public void A_sample_keeps_the_pen_reading_beside_what_the_brush_made_of_it()
    {
        // What replaced the generation counter. There used to be one global curve, so a cached
        // output could be left over from an older one and a stroke had to record which generation
        // it belonged to. With the curve on the brush, and the brush on the stroke, the two cannot
        // drift: every sample has to agree with its own stroke.
        using var session = new PaintSession(240, 200);

        var brush = Taper with { Curve = new PressureCurve(0.1, 0.8, 1.6) };
        for (int i = 0; i <= 20; i++) session.AddSample(20 + i * 8, 100, i / 20.0, brush);
        session.EndStroke();

        var stroke = session.History.Strokes[0];
        Assert.NotEmpty(stroke.Samples);

        foreach (var sample in stroke.Samples)
            Assert.Equal(stroke.Brush.Curve.Apply(sample.RawPressure),
                         sample.ProcessedPressure, precision: 9);

        // And the pen reading itself is untouched, which is the half worth keeping.
        Assert.Equal(1.0, stroke.Samples[^1].RawPressure, precision: 9);
    }

    [Fact]
    public void Brush_opacity_caps_what_pressure_can_reach()
    {
        // Krita's brush opacity: a 40% brush never exceeds 40% however hard it is pressed.
        var brush = new BrushSettings { Opacity = 0.4, PressureDrives = PressureControl.Opacity };

        Assert.Equal(0.4f, brush.OpacityFor(1.0), precision: 5);
        Assert.Equal(0.2f, brush.OpacityFor(0.5), precision: 5);

        // With pressure driving size instead, opacity is the brush's own and pressure does not
        // touch it.
        var sized = brush with { PressureDrives = PressureControl.Size };
        Assert.Equal(0.4f, sized.OpacityFor(0.1), precision: 5);
    }

    [Fact]
    public void Pressure_can_drive_both_size_and_opacity()
    {
        // An exclusive choice cannot express an ordinary soft brush, which is why Both exists.
        var both = new BrushSettings { Size = 100, PressureDrives = PressureControl.Both };

        Assert.Equal(50f, both.StrokeWidthFor(0.5), precision: 5);
        Assert.Equal(0.5f, both.OpacityFor(0.5), precision: 5);

        // And each of the exclusive settings still leaves the other alone.
        var sizeOnly = both with { PressureDrives = PressureControl.Size };
        Assert.Equal(50f, sizeOnly.StrokeWidthFor(0.5), precision: 5);
        Assert.Equal(1f, sizeOnly.OpacityFor(0.5), precision: 5);

        var opacityOnly = both with { PressureDrives = PressureControl.Opacity };
        Assert.Equal(100f, opacityOnly.StrokeWidthFor(0.5), precision: 5);
        Assert.Equal(0.5f, opacityOnly.OpacityFor(0.5), precision: 5);
    }

    [Fact]
    public void A_stroke_records_the_spacing_it_was_drawn_with()
    {
        // Spacing moved off the engine for this reason. An engine holding its own settings is
        // shared between strokes, so a replay would use whatever the last change left behind.
        using var session = new PaintSession(240, 200);

        Stroke(session, Beads, 60);
        Stroke(session, Beads with { Spacing = 0.05 }, 140);

        Assert.Equal(2.0, session.History.Strokes[0].Brush.Spacing);
        Assert.Equal(0.05, session.History.Strokes[1].Brush.Spacing);

        // And the tight one really is continuous while the loose one beads, on the same canvas.
        Assert.True(InkRuns(session, 60) > 3);
        Assert.Equal(1, InkRuns(session, 140));
    }

    [Fact]
    public void A_stroke_records_the_smoothing_that_made_it()
    {
        // It rides along on the brush rather than being recorded separately, which is one of the
        // things moving it onto the brush bought. The samples stored are what the pen reported, so
        // redrawing means filtering them again, and doing that with today's setting would move ink
        // already on the canvas.
        using var session = new PaintSession(240, 200);

        Stroke(session, Taper, 60);
        Stroke(session, Taper with { Smoothing = new StrokeSmoothing { Position = 60 } }, 140);

        Assert.False(session.History.Strokes[0].Brush.Smoothing.IsEnabled);
        Assert.Equal(60, session.History.Strokes[1].Brush.Smoothing.Position);
    }

    [Fact]
    public void Position_and_pressure_are_filtered_independently()
    {
        // Two reaches rather than one with a switch. Either can run without the other, so a shaky
        // hand can have its path steadied with its pressure left alone.
        var pathOnly = Taper with { Smoothing = new StrokeSmoothing { Position = 80 } };
        var pressureOnly = Taper with { Smoothing = new StrokeSmoothing { Pressure = 80 } };

        Assert.True(pathOnly.Smoothing.SmoothsPosition);
        Assert.False(pathOnly.Smoothing.SmoothsPressure);
        Assert.False(pressureOnly.Smoothing.SmoothsPosition);
        Assert.True(pressureOnly.Smoothing.SmoothsPressure);

        // And on the canvas: filtering only the pressure must leave the path where the pen put it.
        var wobble = new List<(double X, double Y)>();
        for (double x = 20; x <= 220; x += 2) wobble.Add((x, 100 + 6 * Math.Sin(x * 0.9)));

        using var plain = new PaintSession(240, 200);
        using var pressureFiltered = new PaintSession(240, 200);
        using var pathFiltered = new PaintSession(240, 200);

        foreach (var (session, brush) in new[]
                 {
                     (plain, Taper with { Size = 10 }),
                     (pressureFiltered, pressureOnly with { Size = 10 }),
                     (pathFiltered, pathOnly with { Size = 10 }),
                 })
        {
            foreach (var (x, y) in wobble) session.AddSample(x, y, 1.0, brush);
            session.EndStroke();
        }

        Assert.Equal(InkHeight(plain, 120), InkHeight(pressureFiltered, 120));
        Assert.True(InkHeight(pathFiltered, 120) < InkHeight(plain, 120),
            $"the path filter should flatten the wobble: {InkHeight(pathFiltered, 120)} px " +
            $"against {InkHeight(plain, 120)} px");

        // The other direction: a position-only brush must leave the pressure alone, so a stroke
        // whose pressure slams about keeps slamming about. Read as total ink, since a wide dab
        // covers the narrow one beside it and every column reads as wide either way.
        int Ink(StrokeSmoothing smoothing)
        {
            using var session = new PaintSession(300, 200);
            var wide = Taper with
            {
                Size = 80,
                PressureDrives = PressureControl.Size,
                Smoothing = smoothing,
            };

            int i = 0;
            for (double x = 20; x <= 280; x += 2, i++)
                session.AddSample(x, 100, i % 2 == 0 ? 0.2 : 0.9, wide);
            session.EndStroke();

            int ink = 0;
            for (int y = 0; y < session.Height; y++)
                for (int x = 0; x < session.Width; x++)
                    if (session.Bitmap.GetPixel(x, y) != SKColors.White) ink++;
            return ink;
        }

        int untouched = Ink(StrokeSmoothing.None);
        int positionOnly = Ink(new StrokeSmoothing { Position = 60 });

        Assert.True(Math.Abs(untouched - positionOnly) < untouched * 0.05,
            $"filtering the path changed the widths: {positionOnly} px against {untouched} px");
    }

    [Fact]
    public void An_undo_replays_a_stroke_with_the_filtering_it_was_drawn_under()
    {
        // The stroke under test is drawn *with* a filter, which is what makes this able to fail.
        // Replaying it without one -- or with whatever is selected later -- shifts and reshapes it,
        // because the filter lags and flattens. A stroke drawn unfiltered would look identical
        // however it was replayed, and the test would prove nothing.
        using var session = new PaintSession(240, 200);

        var wobbly = Taper with { Size = 12, Smoothing = new StrokeSmoothing { Position = 80 } };
        for (double x = 20; x <= 220; x += 2)
            session.AddSample(x, 60 + 6 * Math.Sin(x * 0.9), 1.0, wobbly);
        session.EndStroke();

        using var before = session.Bitmap.Copy();

        // An unfiltered brush, drawn elsewhere, then undone. The first stroke must not have moved.
        Stroke(session, Taper, 160);
        Assert.True(session.Undo());

        int changed = 0;
        for (int y = 0; y < 120; y++)
            for (int x = 0; x < session.Width; x++)
                if (before.GetPixel(x, y) != session.Bitmap.GetPixel(x, y)) changed++;

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Smoothing_moves_the_ink_but_not_the_recorded_path()
    {
        // The filter is non-destructive: the document keeps where the pen went, and what it drew
        // is worked out from that. Both halves matter, so both are checked -- the samples have to
        // be untouched, and the ink has to have moved, or the filter is not running at all.
        var path = new List<(double X, double Y)>();
        for (double x = 20; x <= 220; x += 2) path.Add((x, 100 + 6 * Math.Sin(x * 0.9)));

        using var plain = new PaintSession(240, 200);
        using var filtered = new PaintSession(240, 200);

        var soft = Taper with { Size = 10, Smoothing = new StrokeSmoothing { Position = 80 } };

        foreach (var (session, brush) in new[] { (plain, Taper with { Size = 10 }), (filtered, soft) })
        {
            foreach (var (x, y) in path) session.AddSample(x, y, 1.0, brush);
            session.EndStroke();
        }

        // Recorded identically: smoothing never reaches the stroke.
        Assert.Equal(plain.History.Strokes[0].Samples.Select(s => s.Position),
                     filtered.History.Strokes[0].Samples.Select(s => s.Position));

        // Drawn differently: the wobble is flattened, so the ink covers fewer rows.
        Assert.True(InkHeight(filtered, 120) < InkHeight(plain, 120),
            $"filtered {InkHeight(filtered, 120)} px, unfiltered {InkHeight(plain, 120)} px");
    }

    [Fact]
    public void Smoothed_pressure_reaches_the_brush_through_its_curve()
    {
        // The order the session applies things in: filter the pen, then let the brush respond.
        // Curving first and filtering after would smooth the brush's output rather than the hand's
        // input, and a brush with a steep curve would come out filtered harder than a gentle one
        // holding the same pen.
        //
        // Only visible with pressure smoothing on, which is why nothing else here catches it: with
        // it off the filtered pressure is the raw pressure and both orders agree.
        var brush = Taper with { Size = 80, PressureDrives = PressureControl.Size };

        // Both runs filter the position, so the only thing that differs is the pressure reach.
        // With the position reach at zero instead, a build that filtered pressure whenever it
        // filtered position would still pass -- there would be nothing for it to key off.
        int InkArea(bool smoothPressure)
        {
            using var session = new PaintSession(300, 200);
            var filtered = brush with
            {
                Smoothing = new StrokeSmoothing
                {
                    Position = 60,
                    Pressure = smoothPressure ? 60 : 0,
                },
            };

            // Pressure slamming between light and heavy on every sample, along a straight line.
            int i = 0;
            for (double x = 20; x <= 280; x += 2, i++)
                session.AddSample(x, 100, i % 2 == 0 ? 0.2 : 0.9, filtered);
            session.EndStroke();

            // Total ink, not the width at a column. At two units between samples an 72 px dab
            // swallows the 16 px one beside it, so every column reads as wide whether or not the
            // pressure was steadied -- which is how the first version of this test measured
            // nothing. The area over the whole stroke does show it: a steadied pressure settles
            // near the mean and lays down a narrower ribbon than the peaks would.
            int ink = 0;
            for (int y = 0; y < session.Height; y++)
                for (int x = 0; x < session.Width; x++)
                    if (session.Bitmap.GetPixel(x, y) != SKColors.White) ink++;
            return ink;
        }

        int jumpy = InkArea(smoothPressure: false);
        int steady = InkArea(smoothPressure: true);

        Assert.True(jumpy > 10000, $"the stroke should cover real ground, it covered {jumpy} px");
        Assert.True(steady < jumpy * 0.8,
            $"smoothing pressure should narrow the stroke: {steady} px against {jumpy} px");
    }

    [Fact]
    public void Changing_smoothing_mid_stroke_does_not_change_the_stroke()
    {
        // The filter carries state across samples. Changing how far it reaches part way through
        // would put a step in the middle of the stroke.
        using var session = new PaintSession(240, 200);
        var brush = Taper with { Size = 10 };
        var filtered = brush with { Smoothing = new StrokeSmoothing { Position = 120 } };

        for (double x = 20; x <= 120; x += 2) session.AddSample(x, 100, 1.0, brush);
        for (double x = 122; x <= 220; x += 2) session.AddSample(x, 100, 1.0, filtered);
        session.EndStroke();

        Assert.Single(session.History.Strokes);
        Assert.False(session.History.Strokes[0].Brush.Smoothing.IsEnabled);

        // A filter switched on half way would have pulled the second half off the line.
        Assert.Equal(InkHeight(session, 60), InkHeight(session, 200));
    }

    [Fact]
    public void A_curved_brush_fills_the_corners_a_straight_one_leaves()
    {
        // Sampled coarsely, as a tablet does on a fast stroke. The chord cuts inside the arc, so
        // the curved version puts ink where the straight one leaves paper.
        var corner = new List<(double X, double Y)>();
        for (int i = 0; i < 9; i++)
        {
            double a = Math.PI * i / 16;
            corner.Add((120 + 90 * Math.Cos(a), 30 + 90 * Math.Sin(a)));
        }

        int Ink(StrokeInterpolation how)
        {
            using var session = new PaintSession(260, 200);
            var brush = Taper with { Size = 6, Interpolation = how };

            foreach (var (x, y) in corner) session.AddSample(x, y, 1.0, brush);
            session.EndStroke();

            int ink = 0;
            for (int y = 0; y < session.Height; y++)
                for (int x = 0; x < session.Width; x++)
                    if (session.Bitmap.GetPixel(x, y) != SKColors.White) ink++;
            return ink;
        }

        int straight = Ink(StrokeInterpolation.Straight);
        int curved = Ink(StrokeInterpolation.Curved);

        Assert.True(straight > 1000, $"the stroke should cover ground, it covered {straight} px");
        Assert.True(curved > straight,
            $"the fitted path is the longer one: {curved} px against {straight} px");
    }

    [Fact]
    public void A_curved_stroke_reaches_the_last_sample()
    {
        // The fitter is one sample behind, so without a flush at the end of the stroke the ink
        // would stop short of where the pen lifted.
        using var session = new PaintSession(300, 200);
        var brush = Taper with { Size = 8, Interpolation = StrokeInterpolation.Curved };

        for (double x = 20; x <= 260; x += 30) session.AddSample(x, 100, 1.0, brush);
        session.EndStroke();

        Assert.NotEqual(SKColors.White, session.Bitmap.GetPixel(258, 100));
    }

    [Fact]
    public void An_undo_replays_a_stroke_with_the_interpolation_it_was_drawn_under()
    {
        // Carried on the brush like everything else, so a replay refits rather than joining the
        // samples up straight.
        using var session = new PaintSession(300, 220);

        var curved = Taper with { Size = 8, Interpolation = StrokeInterpolation.Curved };
        for (int i = 0; i < 9; i++)
        {
            double a = Math.PI * i / 16;
            session.AddSample(120 + 90 * Math.Cos(a), 30 + 90 * Math.Sin(a), 1.0, curved);
        }
        session.EndStroke();

        using var before = session.Bitmap.Copy();

        Stroke(session, Taper, 200);
        Assert.True(session.Undo());

        int changed = 0;
        for (int y = 0; y < 180; y++)
            for (int x = 0; x < session.Width; x++)
                if (before.GetPixel(x, y) != session.Bitmap.GetPixel(x, y)) changed++;

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Every_brush_in_the_opening_library_puts_ink_down()
    {
        // The library is a starting set, not decoration: a preset that drew nothing -- a curve
        // whose range excluded the pressure being applied, say -- would be discovered by whoever
        // picked it rather than here.
        foreach (var brush in BrushLibrary.Defaults)
        {
            using var session = new PaintSession(240, 200);
            Stroke(session, brush, 100, pressure: 0.7);

            Assert.True(InkRuns(session, 100) > 0, $"{brush.Name} drew nothing");
        }
    }
}
