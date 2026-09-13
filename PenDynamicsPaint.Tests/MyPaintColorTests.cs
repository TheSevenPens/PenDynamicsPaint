using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Drawing.MyPaint;
using PenDynamicsPaint.Paint;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// What a MyPaint brush is allowed to do to the ink it was handed.
/// </summary>
/// <remarks>
/// The shifts are per dab and driven by inputs like any other setting, so a brush can colour a
/// stroke by what the pen is doing rather than by what was picked. That is what the Tilt testing
/// brush is: hue from the direction of lean, and nothing else moving, so how steady the tilt is
/// becomes something the eye can read directly instead of being inferred from the width of a mark.
/// </remarks>
public class MyPaintColorTests
{
    /// <summary>A mid blue with room to move in every direction.</summary>
    private static readonly SKColor Ink = SKColor.FromHsv(220, 60, 60);

    /// <summary>A plain opaque brush, with the three colour shifts set as given.</summary>
    private static MyPaintBrush Shifting(double hue = 0, double saturation = 0, double value = 0) =>
        MyPaintBrush.Parse($$"""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 2.4 },
                "opaque": { "base_value": 1.0 },
                "opaque_multiply": { "base_value": 1.0 },
                "opaque_linearize": { "base_value": 0.0 },
                "hardness": { "base_value": 1.0 },
                "dabs_per_actual_radius": { "base_value": 6.0 },
                "change_color_h": { "base_value": {{hue}} },
                "change_color_hsv_s": { "base_value": {{saturation}} },
                "change_color_v": { "base_value": {{value}} }
              }
            }
            """, "tint");

    /// <summary>Draw a straight stroke and read the ink at a point along it.</summary>
    private static SKColor Drawn(MyPaintBrush brush, SKColor ink, int atX = 300,
                                 Func<int, PenOrientation>? orientation = null)
    {
        using var session = new PaintSession(700, 240);
        session.SetStrokeColor(ink);

        var settings = new BrushSettings
        {
            Name = "tint",
            Engine = BrushEngineKind.MyPaint,
            MyPaint = brush,
            Interpolation = StrokeInterpolation.Straight,
            Compositing = StrokeCompositing.Direct,
        };

        for (int i = 0; i < 110; i++)
            session.AddSample(40 + i * 5.5, 120, 0.8, settings,
                              orientation?.Invoke(i) ?? default, (long)(i * 10_000));

        session.EndStroke();
        return session.Bitmap.GetPixel(atX, 120);
    }

    private static (double H, double S, double V) Hsv(SKColor c)
    {
        c.ToHsv(out float h, out float s, out float v);
        return (h, s / 100.0, v / 100.0);
    }

    /// <summary>How far apart two hues are, the short way round the wheel.</summary>
    private static double HueApart(double a, double b)
    {
        double d = Math.Abs(a - b) % 360.0;
        return d > 180 ? 360 - d : d;
    }

    [Fact]
    public void A_brush_that_asks_for_nothing_draws_the_ink_it_was_given()
    {
        // The control, and the path most strokes take: no colour settings means the round trip
        // through HSV is skipped entirely, so the ink has to come out bit for bit.
        var plain = Drawn(Shifting(), Ink);

        Assert.Equal(Ink.Red, plain.Red);
        Assert.Equal(Ink.Green, plain.Green);
        Assert.Equal(Ink.Blue, plain.Blue);
    }

    [Fact]
    public void Hue_shifts_by_the_fraction_of_a_turn_it_is_given()
    {
        // A quarter of the wheel is 90 degrees, and the setting counts in turns rather than
        // degrees because that is what a brush file gives.
        var shifted = Drawn(Shifting(hue: 0.25), Ink);

        var (h, s, v) = Hsv(shifted);

        Assert.True(HueApart(h, 220 + 90) < 6,
            $"the hue came out at {h:F0} rather than near {220 + 90}");

        // And nothing else moved.
        Assert.InRange(s, 0.55, 0.65);
        Assert.InRange(v, 0.55, 0.65);
    }

    [Fact]
    public void Hue_wraps_round_the_wheel_rather_than_piling_up_at_the_end()
    {
        // Hue is a position on a circle, so a shift past the end comes back round. It matters for
        // exactly the brushes this exists for: one driving hue from an input that goes round -- the
        // way the pen leans, the direction of a stroke -- should return to the colour it started
        // at rather than sticking at whatever the clamp left it on.
        //
        // 1.75 turns from 220 lands back at 220 + 270.
        var wrapped = Drawn(Shifting(hue: 1.75), Ink);

        var (h, _, _) = Hsv(wrapped);

        Assert.True(HueApart(h, 220 + 270) < 6,
            $"the hue came out at {h:F0} rather than near {(220 + 270) % 360}, which is where " +
            "one and three quarter turns from 220 lands");
    }

    [Fact]
    public void Grey_ink_cannot_be_given_a_colour_by_the_saturation_setting()
    {
        // libmypaint scales the saturation shift by the saturation already there, so it moves a
        // colour further from grey in proportion to how colourful it is and can never move a grey
        // off the axis at all. Surprising enough to pin: read as an ordinary addition, this setting
        // looks like it should tint anything.
        var grey = new SKColor(0x80, 0x80, 0x80);

        var shifted = Drawn(Shifting(saturation: 8.0), grey);

        Assert.Equal(shifted.Red, shifted.Green);
        Assert.Equal(shifted.Green, shifted.Blue);
    }

    [Fact]
    public void The_saturation_setting_does_move_ink_that_has_some()
    {
        // The other half, or the test above passes on a build that dropped the setting entirely.
        var before = Hsv(Drawn(Shifting(), Ink));
        var after = Hsv(Drawn(Shifting(saturation: 1.0), Ink));

        Assert.True(after.S > before.S + 0.1,
            $"saturation went from {before.S:F2} to {after.S:F2}, which is barely anything");
    }

    [Fact]
    public void Value_clamps_rather_than_wrapping()
    {
        // The opposite of hue, and worth stating because they sit on adjacent lines. Value is a
        // distance along an axis with two ends: driven past the top it stays at the top. Wrapping
        // it would take a brush asked for its brightest ink and hand back black.
        var bright = Drawn(Shifting(value: 3.0), Ink);

        var (_, _, v) = Hsv(bright);
        Assert.True(v > 0.97, $"value came out at {v:F2} after being driven well past the top");
    }

    [Fact]
    public void The_colour_is_worked_out_for_every_dab_rather_than_once_a_stroke()
    {
        // What makes this a readout rather than a tint. The Tilt testing brush shows how steady
        // tilt is because each dab is coloured by the orientation of that dab -- so a stroke whose
        // orientation changes along it comes out as a gradient, and a jump in the pen's reading
        // shows as a jump in the colour.
        var brush = MyPaintBrush.Parse(StockBrushes.TiltTesting, "Tilt testing");

        // A pen whose bearing sweeps right round over the stroke.
        PenOrientation Sweeping(int i) =>
            new(Azimuth: -180 + 360.0 * i / 109.0, Altitude: 45, Twist: 0, TiltX: 0, TiltY: 0);

        var early = Hsv(Drawn(brush, Ink, atX: 150, orientation: Sweeping));
        var late = Hsv(Drawn(brush, Ink, atX: 500, orientation: Sweeping));

        Assert.True(HueApart(early.H, late.H) > 60,
            $"the hue barely moved along the stroke: {early.H:F0} then {late.H:F0}");
    }

    [Fact]
    public void The_HSL_pair_is_still_reported_as_unhonoured()
    {
        // libmypaint has a second path to the same place through a different colour space. It is
        // not implemented, and a brush leaning on it has to say so rather than quietly drawing
        // something else.
        var brush = MyPaintBrush.Parse("""
            {
              "version": 3,
              "settings": {
                "radius_logarithmic": { "base_value": 2.0 },
                "change_color_l": { "base_value": 0.4 }
              }
            }
            """, "hsl");

        Assert.Contains("change_color_l", brush.Ignored);
    }
}
