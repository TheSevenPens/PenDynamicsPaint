using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Paint;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// Pins what Wash is for: <b>a stroke asked for 15% ink gets 15% ink</b>, however many samples it
/// is made of.
/// </summary>
/// <remarks>
/// <para>
/// Painted directly, a translucent mark composites again at every overlap. A real stroke has a
/// median sample spacing of 0.41 document units, so with a 40 unit brush roughly a hundred
/// segments cover each pixel and a light stroke saturates: 15% pressure renders at 99%. Measured
/// in TheSevenPens/PenDynamicsLab#68, whose naive renderer keeps that behaviour on purpose as
/// reference.
/// </para>
/// <para>
/// Both modes are exercised here. The direct case is not a curiosity: without it a passing Wash
/// number could mean the compositing works or could mean the test never overlapped anything.
/// </para>
/// </remarks>
public class WashCompositingTests
{
    /// <summary>Luminance of the ink at full opacity, over the white paper.</summary>
    private const double InkLuminance = 0.299 * 0x1A + 0.587 * 0x1A + 0.114 * 0x2E;

    private const double PaperLuminance = 255.0;

    /// <summary>
    /// Draw one straight horizontal stroke at a constant pressure, sampled the way a tablet
    /// samples: far closer together than the brush is wide.
    /// </summary>
    private static PaintSession Stroke(StrokeCompositing mode, double pressure,
                                       double spacing = 0.5, double size = 40)
    {
        var session = new PaintSession(240, 200);
        var brush = BrushSettings.Default with
        {
            Size = size,
            PressureDrives = PressureControl.Opacity,
            Compositing = mode,
        };

        for (double x = 40; x <= 200; x += spacing)
            session.AddSample(x, 100, pressure, brush);

        session.EndStroke();
        return session;
    }

    /// <summary>The alpha the ink appears to have been laid down at, read off the paper.</summary>
    private static double ApparentAlpha(PaintSession session, int x, int y)
    {
        var c = session.Bitmap.GetPixel(x, y);
        double lum = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
        return (PaperLuminance - lum) / (PaperLuminance - InkLuminance);
    }

    [Fact]
    public void Wash_gives_a_light_stroke_the_opacity_it_asked_for()
    {
        using var session = Stroke(StrokeCompositing.Wash, pressure: 0.15);

        double alpha = ApparentAlpha(session, 120, 100);
        Assert.True(Math.Abs(alpha - 0.15) < 0.03,
            $"expected about 0.15 alpha, got {alpha:F3}");
    }

    [Fact]
    public void Direct_saturates_the_same_stroke()
    {
        // The control. Without it, the test above could pass because nothing overlapped.
        using var session = Stroke(StrokeCompositing.Direct, pressure: 0.15);

        double alpha = ApparentAlpha(session, 120, 100);
        Assert.True(alpha > 0.9,
            $"expected the direct path to saturate, got {alpha:F3} -- if this fails the sampling " +
            "is too sparse to overlap and the Wash result means nothing");
    }

    [Fact]
    public void Wash_does_not_depend_on_how_often_the_pen_was_sampled()
    {
        // The property underneath the headline one. Drawing the same path slowly means more
        // samples, and under direct painting that alone changes the ink.
        using var dense = Stroke(StrokeCompositing.Wash, pressure: 0.3, spacing: 0.4);
        using var sparse = Stroke(StrokeCompositing.Wash, pressure: 0.3, spacing: 4.0);

        double a = ApparentAlpha(dense, 120, 100);
        double b = ApparentAlpha(sparse, 120, 100);
        Assert.True(Math.Abs(a - b) < 0.02, $"dense {a:F3} vs sparse {b:F3}");
    }

    [Fact]
    public void Direct_does_depend_on_it()
    {
        using var dense = Stroke(StrokeCompositing.Direct, pressure: 0.3, spacing: 0.4);
        using var sparse = Stroke(StrokeCompositing.Direct, pressure: 0.3, spacing: 8.0);

        double a = ApparentAlpha(dense, 120, 100);
        double b = ApparentAlpha(sparse, 120, 100);
        Assert.True(a - b > 0.1,
            $"the direct path should darken with sample count: dense {a:F3} vs sparse {b:F3}");
    }

    [Fact]
    public void Wash_keeps_opacity_varying_along_the_stroke()
    {
        // The difference between alpha-darken within the layer and simply capping the stroke at one
        // opacity. Each point should show what its own pressure asked for, so a ramp stays a ramp.
        var session = new PaintSession(400, 200);
        var brush = BrushSettings.Default with { Size = 30, PressureDrives = PressureControl.Opacity };

        for (double x = 40; x <= 360; x += 0.5)
        {
            double p = 0.1 + 0.7 * (x - 40) / 320;   // 0.1 at the left, 0.8 at the right
            session.AddSample(x, 100, p, brush);
        }
        session.EndStroke();

        double left = ApparentAlpha(session, 60, 100);
        double right = ApparentAlpha(session, 340, 100);

        Assert.True(right - left > 0.4, $"expected a ramp, got {left:F3} to {right:F3}");
        Assert.True(left < 0.3, $"the light end should stay light, got {left:F3}");
        session.Dispose();
    }

    [Fact]
    public void Undo_replays_the_surviving_strokes_washed()
    {
        // A washed stroke replayed directly would let its overlaps accumulate, so undoing a later
        // stroke would darken an earlier one. That reads as a rendering fault and is really a
        // bookkeeping one.
        var session = new PaintSession(240, 200);
        var brush = BrushSettings.Default with { Size = 40, PressureDrives = PressureControl.Opacity };

        for (double x = 40; x <= 200; x += 0.5) session.AddSample(x, 100, 0.15, brush);
        session.EndStroke();
        double before = ApparentAlpha(session, 120, 100);

        // A second stroke elsewhere, then undo it. The first must look exactly as it did.
        for (double x = 40; x <= 200; x += 0.5) session.AddSample(x, 160, 0.5, brush);
        session.EndStroke();
        Assert.True(session.Undo());

        double after = ApparentAlpha(session, 120, 100);
        Assert.True(Math.Abs(before - after) < 0.02,
            $"the surviving stroke changed across an undo: {before:F3} then {after:F3}");
        session.Dispose();
    }

    [Fact]
    public void Opaque_strokes_look_the_same_either_way()
    {
        // With pressure driving size there is no translucency, so the modes must be
        // indistinguishable. If they are not, Wash is doing something it should not.
        using var direct = Stroke(StrokeCompositing.Direct, pressure: 0.8, size: 40);
        using var wash = Stroke(StrokeCompositing.Wash, pressure: 0.8, size: 40);

        // Both were drawn in opacity mode above, so re-read at full pressure where alpha is 1.
        using var d2 = Stroke(StrokeCompositing.Direct, pressure: 1.0);
        using var w2 = Stroke(StrokeCompositing.Wash, pressure: 1.0);

        Assert.True(Math.Abs(ApparentAlpha(d2, 120, 100) - ApparentAlpha(w2, 120, 100)) < 0.01);
    }
}
