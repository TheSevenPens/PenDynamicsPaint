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
