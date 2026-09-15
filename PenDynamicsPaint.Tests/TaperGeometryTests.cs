using System;
using PenDynamicsPaint.Drawing;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// That the taper outline really is the union of its two circles: the sides tangent to both, and
/// no part of either circle left outside.
/// </summary>
/// <remarks>
/// <para>
/// This is worth its own tests because the defect it guards against is close to invisible while
/// drawing. A stroke puts down a taper for every pair of consecutive samples, and consecutive
/// samples are a fraction of a pixel apart with radii that differ by less than that, so the angle
/// the sides are wrong by is tiny and each mark is buried under the next. It shows up only where
/// one end is much wider than the other -- a fast flick, a hard press after a light one -- as a
/// notch out of the wide end.
/// </para>
/// <para>
/// So the tests here read the geometry rather than the ink: a rendered stroke is exactly where
/// the fault hides.
/// </para>
/// </remarks>
public class TaperGeometryTests
{
    /// <summary>
    /// The straight side meets each circle at a right angle to its radius. That is what tangent
    /// means, and it is the property the construction is derived from.
    /// </summary>
    [Theory]
    [InlineData(40f, 10f)]   // wide to narrow
    [InlineData(10f, 40f)]   // narrow to wide
    [InlineData(25f, 25f)]   // parallel sides
    public void SidesAreTangentToBothCircles(float ra, float rb)
    {
        SKPoint a = new(100, 100), b = new(220, 160);

        var path = new SKPath();
        RoundBrushEngine.BuildTaper(path, a, ra, b, rb);

        // The contour is: tangent point on A, tangent point on B, arc, tangent point on A's
        // opposite side, arc. The first two points are one whole side of it.
        var points = path.Points;
        SKPoint ta = points[0], tb = points[1];

        Assert.Equal(ra, Distance(a, ta), 3);
        Assert.Equal(rb, Distance(b, tb), 3);

        // Perpendicular to both radii: the side's direction dotted with each radius is zero.
        var side = Normalize(new SKPoint(tb.X - ta.X, tb.Y - ta.Y));
        Assert.Equal(0, Dot(side, Normalize(new SKPoint(ta.X - a.X, ta.Y - a.Y))), 3);
        Assert.Equal(0, Dot(side, Normalize(new SKPoint(tb.X - b.X, tb.Y - b.Y))), 3);
    }

    /// <summary>
    /// Every point of either circle is inside the outline, and the corners around it are not.
    /// </summary>
    /// <remarks>
    /// The tangency test alone would pass a shape whose caps were swept the wrong way round --
    /// the sides can be tangent while an arc takes the short way where it needed the long one,
    /// leaving a bite out of the wide end. This is the test that catches that.
    /// </remarks>
    [Theory]
    [InlineData(40f, 10f)]
    [InlineData(10f, 40f)]
    [InlineData(25f, 25f)]
    public void OutlineContainsBothCirclesWhole(float ra, float rb)
    {
        SKPoint a = new(100, 100), b = new(220, 160);

        var path = new SKPath();
        RoundBrushEngine.BuildTaper(path, a, ra, b, rb);

        foreach (var (centre, r) in new[] { (a, ra), (b, rb) })
        {
            for (int degrees = 0; degrees < 360; degrees += 5)
            {
                double t = degrees * Math.PI / 180;

                // Just inside the rim, so the test is about the shape rather than about which
                // side of its own boundary a point on it counts as.
                var inside = new SKPoint(
                    centre.X + (float)((r - 0.5) * Math.Cos(t)),
                    centre.Y + (float)((r - 0.5) * Math.Sin(t)));

                Assert.True(path.Contains(inside.X, inside.Y),
                    $"r={r} at {degrees} degrees is outside the outline");
            }

            // Just outside the rim, and off the side the other circle is not on, is outside the
            // outline -- so a test that passes has not done it by swelling the shape.
            for (int degrees = 0; degrees < 360; degrees += 5)
            {
                double t = degrees * Math.PI / 180;
                var outside = new SKPoint(
                    centre.X + (float)((r + 0.5) * Math.Cos(t)),
                    centre.Y + (float)((r + 0.5) * Math.Sin(t)));

                // Skip the points the sides legitimately cover: anything within the other
                // circle, or within the band of straight sides between the two.
                if (Distance(outside, a) <= ra + 0.5 || Distance(outside, b) <= rb + 0.5) continue;
                if (PerpendicularReach(outside, a, ra, b, rb)) continue;

                Assert.False(path.Contains(outside.X, outside.Y),
                    $"r={r} at {degrees} degrees is inside the outline and should not be");
            }
        }
    }

    /// <summary>
    /// One circle swallowing the other is the degenerate case, and the answer is the larger
    /// circle on its own. There are no external tangents to compute.
    /// </summary>
    [Fact]
    public void ContainedCircleGivesTheLargerCircle()
    {
        SKPoint a = new(100, 100), b = new(105, 100);

        var path = new SKPath();
        RoundBrushEngine.BuildTaper(path, a, 40, b, 10);

        Assert.Equal(new SKRect(60, 60, 140, 140), path.Bounds);
    }

    /// <summary>
    /// Whether <paramref name="p"/> falls between the two circles rather than beyond either of
    /// them, where the straight sides are entitled to cover it however far out it sits.
    /// </summary>
    private static bool PerpendicularReach(SKPoint p, SKPoint a, float ra, SKPoint b, float rb)
    {
        var axis = Normalize(new SKPoint(b.X - a.X, b.Y - a.Y));
        float along = Dot(new SKPoint(p.X - a.X, p.Y - a.Y), axis);
        return along > 0 && along < Distance(a, b);
    }

    private static float Distance(SKPoint p, SKPoint q) => MathF.Sqrt(
        (p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));

    private static SKPoint Normalize(SKPoint v)
    {
        float length = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        return new SKPoint(v.X / length, v.Y / length);
    }

    private static float Dot(SKPoint p, SKPoint q) => p.X * q.X + p.Y * q.Y;
}
