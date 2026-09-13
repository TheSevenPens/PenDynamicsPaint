using PenDynamicsPaint.Drawing;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// Pins segmentation invariance: <b>the same path puts marks in the same places however it was
/// cut into segments</b>.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole difference between spacing by distance and painting per event. A tablet
/// reporting at 180 Hz gives short segments; the same path drawn twice as fast gives segments twice
/// as long. If the marks move, the ink depends on how quickly the pen was travelling rather than on
/// where it went.
/// </para>
/// <para>
/// The case below came out of the research that established the rule, and it arrives with its own
/// control: an implementation that resets the accumulator per segment paints <b>nothing at all</b>
/// on the ten-segment version. Without that control, a passing invariance test could mean the rule
/// works or could mean both variants happened to place marks at the same trivial positions.
/// </para>
/// </remarks>
public class DabSpacingTests
{
    private const double Spacing = 2.5;

    private static double[] OneSegment(double length)
    {
        var s = new DabSpacing();
        return [.. s.Walk(length, _ => Spacing)];
    }

    /// <summary>Marks in absolute distance along the path, walked as <paramref name="count"/> pieces.</summary>
    private static double[] ManySegments(double total, int count)
    {
        var s = new DabSpacing();
        double piece = total / count;
        var marks = new List<double>();

        for (int i = 0; i < count; i++)
        {
            double origin = i * piece;
            foreach (double at in s.Walk(piece, _ => Spacing))
                marks.Add(origin + at);
        }

        return [.. marks];
    }

    [Fact]
    public void One_long_segment_places_marks_every_spacing()
    {
        Assert.Equal([2.5, 5.0, 7.5, 10.0], OneSegment(10));
    }

    [Fact]
    public void Ten_short_segments_place_them_in_the_same_places()
    {
        var marks = ManySegments(10, 10);

        Assert.Equal(4, marks.Length);
        for (int i = 0; i < marks.Length; i++)
            Assert.True(Math.Abs(marks[i] - (2.5 * (i + 1))) < 1e-9,
                        $"mark {i} at {marks[i]}, expected {2.5 * (i + 1)}");
    }

    [Fact]
    public void Cutting_the_path_more_finely_still_does_not_move_the_marks()
    {
        // 180 Hz against 45 Hz, as far as this rule is concerned.
        Assert.Equal(ManySegments(10, 4), ManySegments(10, 40));
    }

    [Fact]
    public void An_accumulator_reset_each_segment_would_paint_nothing()
    {
        // The control. Each 1-unit piece is shorter than the 2.5 spacing, so without the carry
        // there is never room for a mark and the stroke is invisible.
        var marks = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            var fresh = new DabSpacing();          // the defect, made explicit
            foreach (double at in fresh.Walk(1.0, _ => Spacing)) marks.Add(at);
        }

        Assert.Empty(marks);
    }

    [Fact]
    public void A_segment_too_short_for_a_mark_still_carries_its_length()
    {
        var s = new DabSpacing();

        Assert.Empty(s.Walk(1.0, _ => Spacing));
        Assert.Equal(1.0, s.Accumulated, precision: 9);

        Assert.Empty(s.Walk(1.0, _ => Spacing));
        Assert.Equal(2.0, s.Accumulated, precision: 9);

        // The third finally reaches it, 2.5 from the start of the first.
        Assert.Equal([0.5], s.Walk(1.0, _ => Spacing).ToArray());
    }

    [Fact]
    public void Spacing_is_floored_so_a_vanishing_brush_cannot_stall()
    {
        // A brush whose size follows pressure has a spacing that goes to zero as the pen lifts.
        // Without the floor the walk would place marks forever without advancing.
        var s = new DabSpacing();
        var marks = s.Walk(10, _ => 0.0).Take(100).ToArray();

        Assert.Equal(20, marks.Length);                       // 10 / 0.5
        Assert.Equal(DabSpacing.MinSpacing, marks[0], precision: 9);
    }

    [Fact]
    public void A_stationary_pen_deposits_nothing()
    {
        // Correct for spacing by distance, and the reason an airbrush needs a separate time-based
        // rule rather than a smaller spacing.
        var s = new DabSpacing();

        Assert.Empty(s.Walk(0, _ => Spacing));
        Assert.Equal(0, s.Accumulated);
    }

    [Fact]
    public void Spacing_is_read_per_mark_not_once_per_segment()
    {
        // A brush that grows along the segment should space its marks further apart as it goes.
        var s = new DabSpacing();
        var marks = s.Walk(30, at => 2.0 + at).ToArray();

        Assert.True(marks.Length >= 3);
        double first = marks[0];
        double second = marks[1] - marks[0];
        double third = marks[2] - marks[1];
        Assert.True(second > first && third > second,
                    $"gaps should widen: {first}, {second}, {third}");
    }

    [Fact]
    public void Resetting_between_strokes_does_not_carry_distance_into_the_next()
    {
        var s = new DabSpacing();
        s.Walk(2.0, _ => Spacing).ToArray();     // leaves 2.0 accumulated
        Assert.Equal(2.0, s.Accumulated, precision: 9);

        s.Reset();

        // Without the reset the next stroke's first mark would land at 0.5 rather than 2.5.
        Assert.Equal([2.5], s.Walk(3.0, _ => Spacing).ToArray());
    }
}
