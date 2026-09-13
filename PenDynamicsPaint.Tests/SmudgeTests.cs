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

    /// <summary>How much red was left along a stretch of the drag.</summary>
    /// <remarks>
    /// Summed rather than sampled at a point. A correct smudge fades as it goes, so any single
    /// point is somewhere on a slope and a threshold there is a statement about the slope's
    /// steepness; the total is a statement about how much paint was moved.
    /// </remarks>
    private static int RedAlong(PaintSession session, int from, int to)
    {
        int total = 0;
        for (int x = from; x < to; x++) total += Math.Max(0, Redness(session, x, 160));
        return total;
    }

    [Fact]
    public void A_smudging_stroke_carries_colour_off_a_mark_and_onto_bare_canvas()
    {
        using var session = new PaintSession(900, 320);

        PaintTheBar(session, Red);
        DragAcross(session, Smudging(smudge: 1.0, length: 0.8));

        // Clear of the bar, where nothing red was ever painted. Close to it rather than far,
        // because a smudge that moves paint runs out: there is only so much to carry, and how far
        // it reaches is a question for the length rather than for this.
        Assert.True(Redness(session, 170, 160) > 20,
            "the smudge carried nothing away from the bar: the canvas at x=170 is " +
            $"{session.Bitmap.GetPixel(170, 160)}");
    }

    [Fact]
    public void A_smudge_moves_paint_rather_than_copying_it()
    {
        // The difference between smudging and painting in a colour picked off the canvas, and the
        // thing that was wrong at first: the mark being dragged from has to end up with less paint
        // in it.
        //
        // Painting a dab over the canvas can only ever add, so a smudge built that way copies its
        // colour onward for as long as the stroke lasts and the mark it came from is untouched. It
        // composites by pulling the canvas towards the dab instead, which takes paint away where
        // the brush is carrying less than the canvas holds.
        using var session = new PaintSession(900, 320);

        PaintTheBar(session, Red);

        using var before = session.Bitmap.Copy();
        int wasRed = before.GetPixel(BarX, 160).Red - before.GetPixel(BarX, 160).Blue;

        // A long length, because the paint on the brush is finite: it reads the canvas as it stood
        // when the stroke began rather than its own trail, so the length is the only thing that
        // decides how much is lifted and how far it goes.
        DragAcross(session, Smudging(smudge: 1.0, length: 0.95));

        int nowRed = Redness(session, BarX, 160);

        Assert.True(nowRed < wasRed / 2,
            $"the bar still holds {nowRed} of its {wasRed} where the smudge crossed it, so the " +
            "paint was copied rather than moved");

        // Thinned, not erased outright: this is a smudge and not a rubber.
        Assert.True(nowRed > 0, "the smudge wiped the bar out entirely");
    }

    [Fact]
    public void The_trail_fades_as_the_paint_runs_out()
    {
        // A brush carrying paint has a finite amount of it. The old arrangement laid the same
        // solid colour at every distance -- it was reading its own trail and topping itself up --
        // and the giveaway was that the trail never got any paler however far it went.
        using var session = new PaintSession(900, 320);

        PaintTheBar(session, Red);
        DragAcross(session, Smudging(smudge: 1.0, length: 0.95));

        int near = Redness(session, 175, 160);
        int middle = Redness(session, 230, 160);
        int far = Redness(session, 330, 160);

        Assert.True(near > middle, $"the trail did not fade: {near} then {middle}");
        Assert.True(middle > far, $"the trail did not keep fading: {middle} then {far}");
        Assert.True(far < near / 3, $"the trail barely faded at all: {near} then {far}");
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
    public void How_far_paint_is_carried_is_what_the_length_sets()
    {
        // smudge_length is how long the brush holds on to what it picked up, and the honest way to
        // measure that is how much paint is still on the brush some way past a mark it crossed.
        //
        // Which colour wins does not answer it: whatever the brush last crossed dominates the
        // pickup at every length, so both a brief and a lasting smudge come out of the blue bar
        // blue. The difference is how much of it there still is further on -- a brush with a long
        // memory is still putting paint down where a brief one has already put everything down.
        // An earlier version of this compared the colours and measured nothing.
        int CarriedPast(double length)
        {
            using var session = new PaintSession(900, 320);

            PaintTheBar(session, Red);
            PaintTheBar(session, Blue, x: 320);
            DragAcross(session, Smudging(smudge: 1.0, length: length));

            // Total ink well past the blue bar. Green is the channel neither bar is made of, so
            // how far it falls below white measures paint of either colour.
            int ink = 0;
            for (int x = 345; x < 560; x++) ink += 255 - session.Bitmap.GetPixel(x, 160).Green;
            return ink;
        }

        int brief = CarriedPast(0.2);
        int lasting = CarriedPast(0.9);

        Assert.True(lasting > brief * 3,
            $"a long smudge should carry far more paint past the bar: {lasting} against {brief}");
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
    public void A_wider_pickup_keeps_hold_of_a_mark_for_longer()
    {
        // smudge_radius_log widens the area the colour is read from, as a log multiple of the dab's
        // own radius. Since the reading is taken behind the dab, a wider one goes on overlapping a
        // mark after the dab itself has left it, so it lifts more paint and carries it further.
        //
        // An earlier version of this measured where the smudge first laid anything, on the grounds
        // that a wide reading finds a mark sooner. It did, while the reading was a disc centred on
        // the dab and reached ahead of the brush -- which is the fault that reading behind it fixed,
        // so the test was pinning the thing that had to change.
        int Carried(double radiusLog)
        {
            using var session = new PaintSession(900, 320);

            PaintTheBar(session, Red);
            DragAcross(session, Smudging(smudge: 1.0, length: 0.8, radiusLog: radiusLog));

            int ink = 0;
            for (int x = 170; x < 560; x++) ink += 255 - session.Bitmap.GetPixel(x, 160).Green;
            return ink;
        }

        int narrow = Carried(0.0);
        int wide = Carried(1.0);

        Assert.True(wide > narrow * 2,
            $"a wider reading should carry more paint away: {wide} against {narrow}");
    }

    [Fact]
    public void Paint_is_dragged_the_way_the_brush_is_moving_and_not_backwards()
    {
        // Reported from drawing a smudge down through a horizontal line and watching red climb
        // upwards out of it, against the direction of the stroke, where Krita's smudge drags it
        // down only.
        //
        // The reading was a disc centred on the dab, so it reached as far in front of the brush as
        // behind: a dab still short of the line already overlapped it, picked its colour up and
        // laid it down there. It reads the half behind the dab now, so nothing is lifted off ground
        // the brush has not yet covered.
        using var session = new PaintSession(900, 620);

        // A horizontal line, crossed by a smudge drawn downwards through it.
        session.SetStrokeColor(Red);
        for (int i = 0; i < 120; i++)
            session.AddSample(60 + i * 6.5, 300, 0.9, Using(Plain()), default, (long)(i * 10_000));
        session.EndStroke();

        session.SetStrokeColor(SKColors.White);
        for (int i = 0; i < 90; i++)
            session.AddSample(430, 180 + i * 4.0, 0.9, Using(Smudging(smudge: 1.0, length: 0.95)),
                              default, (long)(i * 10_000));
        session.EndStroke();

        // Ink either side of the line, clear of its own width.
        int above = 0, below = 0;
        for (int y = 180; y < 280; y++) above += 255 - session.Bitmap.GetPixel(430, y).Green;
        for (int y = 325; y < 460; y++) below += 255 - session.Bitmap.GetPixel(430, y).Green;

        Assert.True(below > 200, $"the smudge dragged nothing downwards: {below}");
        Assert.True(above < below / 8,
            $"the smudge pulled paint back up against its own travel: {above} above the line " +
            $"against {below} below it");
    }

    [Fact]
    public void A_brush_does_not_pick_up_its_own_trail()
    {
        // What makes the paint on a brush finite. Reading the layer as it is being changed, every
        // dab past a mark reads the paint the dab before it just laid and tops itself back up, so
        // the colour never runs out: the trail comes out the same strength at any distance and
        // carries on to the edge of the canvas.
        //
        // Reading the canvas as it stood when the stroke began means there is nothing to top up
        // from. The giveaway either way is whether the trail is still the same strength a long way
        // out, so this measures the shape of it rather than any one point.
        using var session = new PaintSession(900, 320);

        PaintTheBar(session, Red);
        DragAcross(session, Smudging(smudge: 1.0, length: 0.95));

        int near = RedAlong(session, 170, 240);
        int far = RedAlong(session, 400, 470);

        Assert.True(near > 200, $"the smudge carried almost nothing: {near}");
        Assert.True(far < near / 10,
            $"the trail is as strong {far} a long way out as it is {near} near the mark, so the " +
            "brush is reading what it has just laid down");
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

            return Redness(session, 170, 160);
        }

        int washed = Carried(StrokeCompositing.Wash);
        int direct = Carried(StrokeCompositing.Direct);

        Assert.True(washed > 20, $"the washed smudge carried nothing: {washed}");
        Assert.True(Math.Abs(washed - direct) < 15,
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
        // smudge_transparency is a floor on how much paint has to be under the reading before the
        // brush will pick anything up, and a dab that fails it is not drawn at all. It is what
        // keeps a smudge brush from spreading a mark outwards past its own edge.
        //
        // Measured against the same brush with the floor open, rather than against bare paper. The
        // floor does not stop a smudge travelling by a little -- dabs laid while the brush is still
        // over the mark clear its edge by their own radius, floor or no floor -- so a test that
        // looked for an empty canvas either passed for the wrong reason or failed for one.
        (int Last, int Total) Reach(double transparency)
        {
            using var session = new PaintSession(900, 320);

            PaintTheBar(session, Red);
            DragAcross(session, Smudging(smudge: 1.0, length: 0.95, transparency: transparency));

            int last = -1, total = 0;
            for (int x = 157; x < 600; x++)
            {
                if (session.Bitmap.GetPixel(x, 160) != SKColors.White) last = x;
                total += 255 - session.Bitmap.GetPixel(x, 160).Green;
            }

            return (last, total);
        }

        var open = Reach(0.0);
        var floored = Reach(0.9);

        Assert.True(open.Last > 300, $"the unfloored smudge should travel: it stopped at {open.Last}");
        Assert.True(floored.Last < 200,
            $"the floored smudge travelled to {floored.Last}, so the floor refused nothing");
        Assert.True(floored.Total < open.Total / 3,
            $"the floor let {floored.Total} through against {open.Total} without it");
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
