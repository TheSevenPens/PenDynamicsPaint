using PenDynamicsPaint.Drawing;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// What a brush makes of the pen: the range it responds to, and how hard it responds across it.
/// </summary>
/// <remarks>
/// Three numbers, so the arithmetic is small and the ways it can be wrong are specific: a range
/// that silently inverts, an exponent that lets a light touch reach full strength, an output that
/// leaves 0 to 1 and hands a negative width to the brush. Each of those is pinned below.
/// </remarks>
public class PressureCurveTests
{
    [Fact]
    public void A_linear_curve_hands_the_pen_through_untouched()
    {
        var c = PressureCurve.Linear;

        Assert.True(c.IsLinear);
        foreach (double p in new[] { 0, 0.1, 0.25, 0.5, 0.75, 0.9, 1.0 })
            Assert.Equal(p, c.Apply(p), precision: 9);
    }

    [Fact]
    public void Start_removes_the_dead_weight_at_the_bottom_of_the_range()
    {
        // What this is for: a tablet that reports 200 counts before the nib has really moved. With
        // Start at 0.2 that part of the range produces nothing and the rest is stretched over it,
        // so the brush reaches full strength at full pressure rather than short of it.
        var c = new PressureCurve(0.2, 1.0, 1.0);

        Assert.Equal(0, c.Apply(0.0), precision: 9);
        Assert.Equal(0, c.Apply(0.2), precision: 9);
        Assert.Equal(0.25, c.Apply(0.4), precision: 9);
        Assert.Equal(1.0, c.Apply(1.0), precision: 9);
    }

    [Fact]
    public void End_lets_the_brush_saturate_without_bottoming_the_nib_out()
    {
        var c = new PressureCurve(0.0, 0.5, 1.0);

        Assert.Equal(0.5, c.Apply(0.25), precision: 9);
        Assert.Equal(1.0, c.Apply(0.5), precision: 9);
        Assert.Equal(1.0, c.Apply(0.9), precision: 9);
    }

    [Fact]
    public void The_exponent_decides_whether_the_brush_comes_on_early_or_late()
    {
        var hard = new PressureCurve(0, 1, 2.0);    // holds light until you lean on it
        var soft = new PressureCurve(0, 1, 0.5);    // on early, then saturating

        Assert.True(hard.Apply(0.5) < 0.5, $"expected below half, got {hard.Apply(0.5)}");
        Assert.True(soft.Apply(0.5) > 0.5, $"expected above half, got {soft.Apply(0.5)}");

        // Both still span the full range, or the exponent would be doing two jobs.
        foreach (var c in new[] { hard, soft })
        {
            Assert.Equal(0, c.Apply(0), precision: 9);
            Assert.Equal(1, c.Apply(1), precision: 9);
        }
    }

    [Fact]
    public void A_curve_is_monotonic_and_stays_inside_zero_to_one()
    {
        // The property everything downstream assumes. A width comes from this number, and a
        // negative or a value past 1 is a brush that grows when you press more lightly.
        var curves = new[]
        {
            PressureCurve.Linear,
            new PressureCurve(0.2, 0.8, 2.5),
            new PressureCurve(0.0, 0.3, 0.4),
            new PressureCurve(0.6, 1.0, 1.0),
        };

        foreach (var c in curves)
        {
            double previous = -1;
            for (int i = 0; i <= 200; i++)
            {
                double y = c.Apply(i / 200.0);
                Assert.InRange(y, 0, 1);
                Assert.True(y >= previous - 1e-12, $"{c} went backwards at {i / 200.0}");
                previous = y;
            }
        }
    }

    [Fact]
    public void A_range_of_no_width_becomes_a_threshold()
    {
        // Start at or past End. The limit the arithmetic approaches rather than a special case:
        // nothing below, full strength at or above. A usable brush, and better than dividing by
        // zero and handing NaN to a width.
        var c = new PressureCurve(0.6, 0.6, 1.0);

        Assert.Equal(0, c.Apply(0.59), precision: 9);
        Assert.Equal(1, c.Apply(0.6), precision: 9);
        Assert.Equal(1, c.Apply(1.0), precision: 9);

        var inverted = new PressureCurve(0.8, 0.3, 1.0);
        Assert.Equal(0, inverted.Apply(0.1), precision: 9);
        Assert.Equal(1, inverted.Apply(0.5), precision: 9);
    }

    [Fact]
    public void The_numbers_are_clamped_where_they_are_set()
    {
        // A slider cannot produce these, but a brush built in code can, and an exponent of zero
        // would make every contact full strength while a negative one sends a light touch to
        // infinity. Neither is a brush.
        var c = new PressureCurve(-5, 40, -2);

        Assert.Equal(0, c.Start);
        Assert.Equal(1, c.End);
        Assert.Equal(0.1, c.Exponent);

        Assert.Equal(0, new PressureCurve(double.NaN, double.NaN, double.NaN).Apply(0), precision: 9);
        Assert.Equal(0, PressureCurve.Linear.Apply(double.NaN), precision: 9);
    }
}
