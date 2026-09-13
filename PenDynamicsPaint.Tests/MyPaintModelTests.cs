using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Drawing.MyPaint;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// The MyPaint brush model: how an input curve bends a setting, and what a <c>.myb</c> file says.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>mypaint/libmypaint</c> at <c>v1.6.1</c>. Two rules carry most of the behaviour
/// and neither is the obvious one, so both are pinned here rather than trusted: a curve
/// <b>adds</b> to a setting rather than scaling it, and beyond its outermost points the terminal
/// segment is <b>extrapolated</b> rather than clamped.
/// </para>
/// <para>
/// Getting either wrong produces a brush that works and is not the brush in the file, which is the
/// worst kind of wrong for a format whose whole point is that someone else authored it.
/// </para>
/// </remarks>
public class MyPaintModelTests
{
    private static InputMapping Curve(params (float X, float Y)[] points) => new(points);

    private static BrushInputs With(BrushInput input, float value)
    {
        var values = new float[BrushInputs.Count];
        BrushInputs.Neutral.CopyTo(values);
        values[(int)input] = value;
        return new BrushInputs(values);
    }

    [Fact]
    public void A_curve_adds_to_the_base_value_rather_than_scaling_it()
    {
        // The rule the whole model rests on. Scaling would look plausible on a curve running 0..1
        // and would be wrong everywhere else -- and silently wrong at a base value of zero, where
        // a multiplier can never produce anything at all.
        var setting = new DynamicSetting(2.0f);
        setting.Bend(BrushInput.Pressure, Curve((0f, 0f), (1f, 0.5f)));

        Assert.Equal(2.0f, setting.ValueFor(With(BrushInput.Pressure, 0f)), precision: 5);
        Assert.Equal(2.25f, setting.ValueFor(With(BrushInput.Pressure, 0.5f)), precision: 5);
        Assert.Equal(2.5f, setting.ValueFor(With(BrushInput.Pressure, 1f)), precision: 5);
    }

    [Fact]
    public void Two_inputs_both_have_their_say()
    {
        // They sum, so a brush can be pulled two ways at once and they can cancel. A model that
        // took the last input to speak, or multiplied them, would give the same answer whenever
        // only one curve was attached -- which is most brushes, and why this needs its own test.
        var setting = new DynamicSetting(1.0f);
        setting.Bend(BrushInput.Pressure, Curve((0f, 0f), (1f, 1f)));
        setting.Bend(BrushInput.Speed1, Curve((0f, 0f), (1f, -1f)));

        var values = new float[BrushInputs.Count];
        BrushInputs.Neutral.CopyTo(values);
        values[(int)BrushInput.Pressure] = 1f;
        values[(int)BrushInput.Speed1] = 1f;

        Assert.Equal(1.0f, setting.ValueFor(new BrushInputs(values)), precision: 5);
    }

    [Fact]
    public void Past_the_ends_the_curve_carries_on_rather_than_flattening()
    {
        // A pressure curve drawn over 0..1 still has an opinion about a tablet reporting 1.4, and
        // libmypaint follows the terminal segment out to it. Clamping instead would quietly flatten
        // the brush exactly where a pressure gain puts it.
        var setting = new DynamicSetting(0f);
        setting.Bend(BrushInput.Pressure, Curve((0f, 0f), (1f, 2f)));

        Assert.Equal(4.0f, setting.ValueFor(With(BrushInput.Pressure, 2f)), precision: 5);
        Assert.Equal(-2.0f, setting.ValueFor(With(BrushInput.Pressure, -1f)), precision: 5);
    }

    [Fact]
    public void A_flat_terminal_segment_stays_flat()
    {
        // A plateau holds rather than running off, which is what lets a brush stop responding past
        // some pressure. Note this is not a special case in the arithmetic -- the interpolation
        // returns the same number on its own, and the guard in the code is only a shortcut.
        var setting = new DynamicSetting(0f);
        setting.Bend(BrushInput.Pressure, Curve((0f, 1f), (0.5f, 1f), (1f, 3f)));

        Assert.Equal(1.0f, setting.ValueFor(With(BrushInput.Pressure, -5f)), precision: 5);
        Assert.Equal(1.0f, setting.ValueFor(With(BrushInput.Pressure, 0.25f)), precision: 5);
    }

    [Fact]
    public void Two_points_at_the_same_place_do_not_divide_by_zero()
    {
        // A brush file can carry a vertical step -- two control points sharing an x -- and the
        // interpolation would divide by their difference. This is the guard that matters, as
        // against the flat-segment one above, which only saves the arithmetic.
        var setting = new DynamicSetting(0f);
        setting.Bend(BrushInput.Pressure, Curve((0.5f, 0f), (0.5f, 1f), (1f, 2f)));

        float value = setting.ValueFor(With(BrushInput.Pressure, 0.5f));

        Assert.True(float.IsFinite(value), $"the curve produced {value}");
        Assert.Equal(0f, value, precision: 5);
    }

    [Fact]
    public void A_curve_with_one_point_says_nothing()
    {
        // One point defines no segment. libmypaint skips such a curve, and so does this -- the
        // alternative is a constant offset that the brush author never drew.
        var setting = new DynamicSetting(1.5f);
        setting.Bend(BrushInput.Pressure, Curve((0.5f, 9f)));

        Assert.True(setting.IsConstant);
        Assert.Equal(1.5f, setting.ValueFor(With(BrushInput.Pressure, 0.5f)), precision: 5);
    }

    [Fact]
    public void Several_segments_are_followed_in_turn()
    {
        var setting = new DynamicSetting(0f);
        setting.Bend(BrushInput.Pressure, Curve((0f, 0f), (0.5f, 1f), (1f, 0f)));

        Assert.Equal(0.5f, setting.ValueFor(With(BrushInput.Pressure, 0.25f)), precision: 5);
        Assert.Equal(1.0f, setting.ValueFor(With(BrushInput.Pressure, 0.5f)), precision: 5);
        Assert.Equal(0.5f, setting.ValueFor(With(BrushInput.Pressure, 0.75f)), precision: 5);
    }

    // -- .myb loading ---------------------------------------------

    private const string Brush = """
        {
          "version": 3,
          "settings": {
            "radius_logarithmic": { "base_value": 1.5,
                                    "inputs": { "pressure": [[0.0, -0.7], [1.0, 0.4]] } },
            "opaque":             { "base_value": 1.0, "inputs": {} },
            "opaque_multiply":    { "base_value": 0.0,
                                    "inputs": { "pressure": [[0.0, 0.0], [1.0, 1.0]] } },
            "hardness":           { "base_value": 0.42, "inputs": {} },
            "smudge":             { "base_value": 0.6, "inputs": {} },
            "elliptical_dab_ratio": { "base_value": 1.0, "inputs": {} },
            "offset_by_random":   { "base_value": 0.0,
                                    "inputs": { "gridmap_x": [[0.0, 0.0], [1.0, 1.0]] } }
          }
        }
        """;

    [Fact]
    public void A_brush_file_loads_its_settings_and_its_curves()
    {
        var brush = MyPaintBrush.Parse(Brush, "test");

        Assert.Equal(1.5f, brush[MyPaintSetting.RadiusLogarithmic].BaseValue, precision: 5);
        Assert.Equal(0.42f, brush[MyPaintSetting.Hardness].BaseValue, precision: 5);

        Assert.Contains(BrushInput.Pressure, brush[MyPaintSetting.RadiusLogarithmic].BentBy);
        Assert.True(brush[MyPaintSetting.Hardness].IsConstant);

        // The radius curve: -0.7 at no pressure, +0.4 at full.
        Assert.Equal(0.8f, brush[MyPaintSetting.RadiusLogarithmic]
            .ValueFor(With(BrushInput.Pressure, 0f)), precision: 5);
        Assert.Equal(1.9f, brush[MyPaintSetting.RadiusLogarithmic]
            .ValueFor(With(BrushInput.Pressure, 1f)), precision: 5);
    }

    [Fact]
    public void A_setting_the_file_leaves_out_keeps_the_libmypaint_default()
    {
        // Brush files are written sparsely, so most settings are absent from most of them. Taking
        // zero for an absent setting would give every brush a radius of one unit and no spacing.
        var brush = MyPaintBrush.Parse("""{ "version": 3, "settings": {} }""", "bare");

        Assert.Equal(2.0f, brush[MyPaintSetting.RadiusLogarithmic].BaseValue, precision: 5);
        Assert.Equal(0.8f, brush[MyPaintSetting.Hardness].BaseValue, precision: 5);
        Assert.Equal(2.0f, brush[MyPaintSetting.DabsPerActualRadius].BaseValue, precision: 5);
    }

    [Fact]
    public void What_the_brush_asked_for_and_did_not_get_is_recorded()
    {
        // A brush is not rejected for wanting smudge, but the shortfall has to be visible or the
        // application is quietly drawing something other than what the file describes.
        var brush = MyPaintBrush.Parse(Brush, "test");

        Assert.Contains("smudge", brush.Ignored);
        Assert.Contains("offset_by_random by gridmap_x", brush.Ignored);

        // And a setting sitting at a value that does nothing is not worth reporting.
        Assert.DoesNotContain("elliptical_dab_ratio", brush.Ignored);
    }

    [Fact]
    public void An_unreadable_brush_says_which_kind_it_is()
    {
        // Version 2 brushes are a line-based text format and are perfectly good brushes this
        // cannot read. Saying so is more use than reporting a JSON syntax error.
        var old = Assert.Throws<FormatException>(
            () => MyPaintBrush.Parse("version 2\nradius_logarithmic 2.0\n", "old"));
        Assert.Contains("version 2", old.Message);

        var broken = Assert.Throws<FormatException>(() => MyPaintBrush.Parse("{ \"settings\": ", "x"));
        Assert.Contains("JSON", broken.Message);

        var empty = Assert.Throws<FormatException>(() => MyPaintBrush.Parse("{}", "x"));
        Assert.Contains("settings", empty.Message);
    }
}
