using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Drawing.MyPaint;
using PenDynamicsPaint.Paint;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// Smudging: a dab taking its colour from what is already on the canvas.
/// </summary>
/// <remarks>
/// <para>
/// The first thing in the engine that <b>reads</b> the canvas rather than only writing to it, and
/// that is most of what these check. A stroke that reads has to paint onto the surface it is
/// reading, so it cannot go through the stroke layer Wash uses -- there it would find only the
/// marks it had just made and would drag its own colour along, never touching the painting it is
/// supposed to be moving.
/// </para>
/// <para>
/// Ported from the legacy path of <c>update_smudge_color</c> and <c>apply_smudge</c> in
/// <c>mypaint-brush.c</c> at <c>v1.6.1</c>.
/// </para>
/// </remarks>
public class SmudgeTests
{
    private static readonly SKColor Red = new(0xE0, 0x30, 0x30);
    private static readonly SKColor Blue = new(0x20, 0x90, 0xE0);

    /// <summary>Where the bar of paint is, and how far right of it the smudge runs.</summary>
    private const int BarX = 140;

    private static MyPaintBrush Smudging(double smudge, double length = 0.5,
                                         double transparency = 0.0, double radiusLog = 0.0) =>
        MyPaintBrush.Parse($$"""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 2.6 },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 1.0 },
                "opaque_linearize": { "base_value": 0.0 },
                "hardness": { "base_value": 0.9 },
                "dabs_per_actual_radius": { "base_value": 6.0 },
                "smudge": { "base_value": {{smudge}} },
                "smudge_length": { "base_value": {{length}} },
                "smudge_transparency": { "base_value": {{transparency}} },
                "smudge_radius_log": { "base_value": {{radiusLog}} }
              }
            }
            """, "smudge");

    private static MyPaintBrush Plain() =>
        MyPaintBrush.Parse("""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 2.8 },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 1.0 },
                "opaque_linearize": { "base_value": 0.0 },
                "hardness": { "base_value": 1.0 },
                "dabs_per_actual_radius": { "base_value": 6.0 }
              }
            }
            """, "plain");

    private static BrushSettings Using(MyPaintBrush brush) => new()
    {
        Name = "b",
        Engine = BrushEngineKind.MyPaint,
        MyPaint = brush,
        Interpolation = StrokeInterpolation.Straight,
    };

    /// <summary>A tall bar of paint at <see cref="BarX"/>, for a smudge to drag off.</summary>
    private static void PaintTheBar(PaintSession session, SKColor colour, double x = BarX)
    {
        session.SetStrokeColor(colour);

        for (int i = 0; i < 40; i++)
            session.AddSample(x, 40 + i * 7.0, 0.9, Using(Plain()), default, (long)(i * 10_000));

        session.EndStroke();
    }

    /// <summary>Drag a stroke left to right across the bar, in white ink.</summary>
    /// <remarks>
    /// White, so that anything on the canvas afterwards that is <b>not</b> white came off the bar
    /// rather than out of the brush. Painting in the bar's own colour would prove nothing.
    /// </remarks>
    private static void DragAcross(PaintSession session, MyPaintBrush brush, double y = 160)
    {
        session.SetStrokeColor(SKColors.White);

        for (int i = 0; i < 120; i++)
            session.AddSample(60 + i * 6, y, 0.9, Using(brush), default, (long)(i * 10_000));

        session.EndStroke();
    }

    /// <summary>How red the canvas is at a point, over and above how blue it is.</summary>
    private static int Redness(PaintSession session, int x, int y)
    {
        var c = session.Bitmap.GetPixel(x, y);
        return c.Red - c.Blue;
    }

    [Fact]
    public void A_smudging_stroke_carries_colour_off_a_mark_and_onto_bare_canvas()
    {
        using var session = new PaintSession(900, 320);

        PaintTheBar(session, Red);
        DragAcross(session, Smudging(smudge: 1.0, length: 0.8));

        // Well clear of the bar, where nothing red was ever painted.
        Assert.True(Redness(session, 320, 160) > 40,
            "the smudge carried nothing away from the bar: the canvas at x=320 is " +
            $"{session.Bitmap.GetPixel(320, 160)}");
    }

    [Fact]
    public void A_brush_that_does_not_smudge_paints_its_own_ink()
    {
        // The control. Without it every test here passes on a build that simply paints whatever is
        // underneath, and there would be nothing to say the ink matters at all.
        using var session = new PaintSession(900, 320);

        PaintTheBar(session, Red);
        DragAcross(session, Smudging(smudge: 0.0));

        var clear = session.Bitmap.GetPixel(320, 160);

        Assert.Equal(SKColors.White, clear);
    }

    [Fact]
    public void How_long_a_colour_survives_being_dragged_over_another_is_what_the_length_sets()
    {
        // smudge_length is how long the brush holds on to what it picked up, and the honest way to
        // measure that is to make it cross something else. Distance alone will not do it: a smudge
        // reads the surface it is painting, so past the first mark it is reading its own trail and
        // topping itself up, and red travels to the edge of the canvas at every length.
        //
        // Crossing the blue bar breaks that loop. Then the question is whether the red on the brush
        // outlasts the blue being laid over it, and the length is exactly what decides.
        int RedPastTheBlue(double length)
        {
            using var session = new PaintSession(900, 320);

            PaintTheBar(session, Red);
            PaintTheBar(session, Blue, x: 320);
            DragAcross(session, Smudging(smudge: 1.0, length: length));

            return Redness(session, 440, 160);
        }

        int brief = RedPastTheBlue(0.2);
        int lasting = RedPastTheBlue(0.9);

        // A short memory comes out of the blue bar blue; a long one is still carrying red.
        Assert.True(brief < -60, $"a short smudge should be blue past the blue bar, not {brief}");
        Assert.True(lasting > 40, $"a long smudge should still be red past it, not {lasting}");
    }

    [Fact]
    public void A_partial_smudge_mixes_what_it_picked_up_with_the_ink()
    {
        // Between the two ends: the brush paints, and carries some of what it crossed while doing
        // it. A build that treated smudge as a switch would land on one end or the other.
        //
        // Measured just clear of the bar, and that matters. The response to this setting is not
        // remotely linear, because a smudge reads the surface it is painting: below 1 every dab
        // dilutes the carried colour with ink and then reads back its own diluted trail, so the
        // dilution compounds and the colour is gone within a few dab widths. At 1 there is no ink
        // in the mix and it sustains itself indefinitely. That is libmypaint's arithmetic rather
        // than a choice here, and it is why this looks close to the source instead of far along.
        int Green(double smudge)
        {
            using var session = new PaintSession(900, 320);

            PaintTheBar(session, Red);
            DragAcross(session, Smudging(smudge: smudge, length: 0.8));

            return session.Bitmap.GetPixel(180, 160).Green;
        }

        int none = Green(0.0);
        int part = Green(0.75);
        int all = Green(1.0);

        Assert.Equal(255, none);

        Assert.True(part < 250, $"the partial smudge picked up nothing: green came out {part}");
        Assert.True(part > 150,
            $"the partial smudge behaved like a full one: green {part} against {all}");
    }

    [Fact]
    public void A_wider_pickup_reaches_paint_from_further_off()
    {
        // smudge_radius_log widens the disc the colour is read from, as a log multiple of the dab's
        // own radius. The effect that shows it is where a smudge first finds anything: a wide
        // pickup catches the edge of the bar while the dab is still clear of it, and so starts
        // laying colour earlier than a narrow one.
        int FirstInk(double radiusLog)
        {
            using var session = new PaintSession(900, 320);

            PaintTheBar(session, Red);
            DragAcross(session, Smudging(smudge: 1.0, length: 0.8, radiusLog: radiusLog));

            for (int x = 0; x < 900; x++)
                if (session.Bitmap.GetPixel(x, 160) != SKColors.White) return x;

            return 900;
        }

        int narrow = FirstInk(0.0);
        int wide = FirstInk(1.0);

        Assert.True(wide < narrow - 10,
            $"a wider pickup should start carrying sooner: it began at x={wide} against {narrow}");
    }

    [Fact]
    public void A_washed_smudge_reads_the_painting_and_not_its_own_stroke()
    {
        // The architectural half. Wash gives a stroke a layer of its own and merges it at the end,
        // and a smudge reading that layer would find only the dabs it had just laid -- so it would
        // pick up its own white ink at the first dab and carry that, and the bar would never move.
        //
        // Both compositing modes therefore have to smudge the same way, which they can only do if
        // a smudging stroke skips the stroke layer entirely.
        int Carried(StrokeCompositing compositing)
        {
            using var session = new PaintSession(900, 320) { Compositing = compositing };

            PaintTheBar(session, Red);
            DragAcross(session, Smudging(smudge: 1.0, length: 0.8));

            return Redness(session, 320, 160);
        }

        int washed = Carried(StrokeCompositing.Wash);
        int direct = Carried(StrokeCompositing.Direct);

        Assert.True(washed > 40, $"the washed smudge carried nothing: {washed}");
        Assert.True(Math.Abs(washed - direct) < 25,
            $"the two compositing modes smudge differently: {washed} washed against {direct} direct");
    }

    [Fact]
    public void Nothing_is_carried_from_one_stroke_into_the_next()
    {
        // The colour on the brush belongs to the stroke. Carried across, a second stroke drawn
        // somewhere else would start by laying down whatever the first one happened to end on --
        // paint appearing from nowhere, some distance from anything that was painted.
        using var session = new PaintSession(900, 320);

        PaintTheBar(session, Red);
        DragAcross(session, Smudging(smudge: 1.0, length: 0.9));

        // A second drag, well below the bar's bottom end, over nothing at all.
        session.SetStrokeColor(SKColors.White);
        for (int i = 0; i < 60; i++)
            session.AddSample(400 + i * 6, 290, 0.9, Using(Smudging(smudge: 1.0, length: 0.9)),
                              default, (long)(i * 10_000));
        session.EndStroke();

        for (int x = 420; x < 700; x++)
            Assert.Equal(SKColors.White, session.Bitmap.GetPixel(x, 290));
    }

    [Fact]
    public void A_smudge_refuses_bare_canvas_when_it_is_told_to()
    {
        // smudge_transparency is a floor on how much alpha has to be under the dab before it will
        // pick anything up. Above the floor the dab is not drawn at all, which is what keeps a
        // smudge brush from spreading a mark outwards past its own edge.
        using var session = new PaintSession(900, 320);

        PaintTheBar(session, Red);
        DragAcross(session, Smudging(smudge: 1.0, length: 0.8, transparency: 0.9));

        // Beyond the bar there is nothing solid enough to pick up, so nothing should be laid.
        for (int x = 260; x < 600; x++)
            Assert.Equal(SKColors.White, session.Bitmap.GetPixel(x, 160));
    }

    [Fact]
    public void An_undone_smudge_replays_where_it_was_drawn()
    {
        // A smudge depends on what was under it, so replaying one means replaying the strokes
        // beneath it first and reading the result. That is what the history already does -- the
        // layer is reset to its baseline and the strokes redrawn in order -- but only if a replayed
        // smudge is given the layer to read. Handed nothing, it would pick up nothing and redraw
        // the stroke as plain white ink.
        using var session = new PaintSession(900, 320);

        PaintTheBar(session, Red);
        DragAcross(session, Smudging(smudge: 1.0, length: 0.8));

        using var before = session.Bitmap.Copy();

        // A stroke on top, then undo it: the smudge underneath has to be replayed to get here.
        session.SetStrokeColor(Blue);
        for (int i = 0; i < 30; i++)
            session.AddSample(500 + i * 4, 250, 0.9, Using(Plain()), default, (long)(i * 10_000));
        session.EndStroke();

        Assert.True(session.Undo());

        for (int y = 0; y < before.Height; y++)
            for (int x = 0; x < before.Width; x++)
                if (before.GetPixel(x, y) != session.Bitmap.GetPixel(x, y))
                    Assert.Fail($"the replayed smudge differs at {x},{y}: " +
                                $"{session.Bitmap.GetPixel(x, y)} against {before.GetPixel(x, y)}");
    }
}
