using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Paint;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// What the stack composites to, and what the region tracking is allowed to skip.
/// </summary>
/// <remarks>
/// <para>
/// The composite is a cache of the layers, rebuilt only over the region that changed. That is not
/// an optimisation that can be left unchecked: recompositing a whole document per frame does not
/// fit in a frame, and <b>a region that is too small leaves stale pixels behind</b> -- ink that was
/// drawn and never appeared, or that appears one stroke later. Neither reads as a compositing
/// fault when you meet it.
/// </para>
/// <para>
/// So the headline test compares the incremental result against a full recomposite of the same
/// stack, and it arrives with a control: a test that proves the incremental path is really
/// skipping work, without which the comparison could pass because nothing was ever being skipped.
/// </para>
/// </remarks>
public class LayerCompositingTests
{
    private static readonly SKColor Red = new(0xFF, 0x00, 0x00);
    private static readonly SKColor Blue = new(0x00, 0x00, 0xFF);

    private static BrushSettings Opaque(double size = 20) =>
        BrushSettings.Default with { Size = size, PressureDrives = PressureControl.Size };

    /// <summary>A straight opaque stroke, drawn and finished.</summary>
    private static void Stroke(PaintSession s, SKColor color, double x0, double y0,
                               double x1, double y1, double step = 2.0, double size = 20)
    {
        s.SetStrokeColor(color);
        var brush = Opaque(size);

        double dx = x1 - x0, dy = y1 - y0;
        double length = Math.Sqrt(dx * dx + dy * dy);
        for (double d = 0; d <= length; d += step)
        {
            double t = d / length;
            s.AddSample(x0 + dx * t, y0 + dy * t, 1.0, brush);
        }

        s.EndStroke();
    }

    /// <summary>Where the two bitmaps first differ, or null when they match everywhere.</summary>
    private static (int X, int Y, SKColor A, SKColor B)? FirstDifference(SKBitmap a, SKBitmap b)
    {
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                var ca = a.GetPixel(x, y);
                var cb = b.GetPixel(x, y);
                if (ca != cb) return (x, y, ca, cb);
            }

        return null;
    }

    [Fact]
    public void Incremental_compositing_matches_a_full_recomposite()
    {
        using var session = new PaintSession(240, 200);
        session.AddLayer();

        // Read between strokes so each one composites over its own region rather than one union
        // at the end, which is what the viewport does and the case that can go stale.
        Stroke(session, Red, 20, 30, 210, 60);
        _ = session.Bitmap;
        Stroke(session, Blue, 30, 170, 200, 40, size: 35);
        _ = session.Bitmap;
        Stroke(session, Red, 120, 10, 130, 190, size: 8);

        using var incremental = session.Bitmap.Copy();

        session.InvalidateComposite();
        using var full = session.Bitmap.Copy();

        var diff = FirstDifference(incremental, full);
        Assert.True(diff is null,
            diff is null ? "" :
            $"the incremental composite is stale at {diff.Value.X},{diff.Value.Y}: " +
            $"{diff.Value.A} where a full recomposite gives {diff.Value.B}");
    }

    [Fact]
    public void Compositing_really_does_skip_the_region_that_did_not_change()
    {
        // The control for the test above. Pixels are put on a layer behind the session's back, so
        // nothing marks them stale; a composite that redrew the whole document would pick them up.
        // If this fails, the comparison above is comparing two full recomposites and proves
        // nothing about the region tracking.
        using var session = new PaintSession(240, 200);
        _ = session.Bitmap;                       // flush the opening full composite

        session.Layers[0].Bitmap.SetPixel(20, 20, Red);

        Stroke(session, Blue, 150, 150, 220, 180);

        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(20, 20));
    }

    [Fact]
    public void A_layer_covers_the_one_below_it()
    {
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 20, 100, 220, 100, size: 40);
        session.AddLayer();
        Stroke(session, Blue, 20, 100, 220, 100, size: 40);

        Assert.Equal(Blue, session.Bitmap.GetPixel(120, 100));
    }

    [Fact]
    public void Hiding_a_layer_reveals_what_is_under_it()
    {
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 20, 100, 220, 100, size: 40);
        session.AddLayer();
        Stroke(session, Blue, 20, 100, 220, 100, size: 40);

        Assert.True(session.SetLayerVisible(1, false));
        Assert.Equal(Red, session.Bitmap.GetPixel(120, 100));

        Assert.True(session.SetLayerVisible(1, true));
        Assert.Equal(Blue, session.Bitmap.GetPixel(120, 100));
    }

    [Fact]
    public void A_half_opacity_layer_shows_half_its_ink()
    {
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 20, 100, 220, 100, size: 40);
        Assert.True(session.SetLayerOpacity(0, 0.5));

        // Red at half over white paper: red stays full, the other two channels land near half.
        var c = session.Bitmap.GetPixel(120, 100);
        Assert.Equal(0xFF, c.Red);
        Assert.InRange(c.Green, 120, 135);
        Assert.InRange(c.Blue, 120, 135);
    }

    [Fact]
    public void Moving_a_layer_down_puts_it_behind()
    {
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 20, 100, 220, 100, size: 40);
        session.AddLayer();
        Stroke(session, Blue, 20, 100, 220, 100, size: 40);
        Assert.Equal(Blue, session.Bitmap.GetPixel(120, 100));

        Assert.True(session.MoveLayer(1, 0));
        Assert.Equal(Red, session.Bitmap.GetPixel(120, 100));
    }

    [Fact]
    public void A_washed_stroke_looks_the_same_before_and_after_the_pen_lifts()
    {
        // The stroke in progress belongs to its layer, so the layer's opacity has to apply to the
        // two together. Composite the stroke over the whole stack instead and it shows at full
        // strength until the pen lifts, then drops to half -- a stroke that changes when you stop
        // drawing it.
        using var session = new PaintSession(240, 200) { Compositing = StrokeCompositing.Wash };
        Assert.True(session.SetLayerOpacity(0, 0.5));

        session.SetStrokeColor(Red);
        var brush = Opaque(40);
        for (double x = 20; x <= 220; x += 2) session.AddSample(x, 100, 1.0, brush);

        var midStroke = session.Bitmap.GetPixel(120, 100);

        // Or the test would pass by seeing nothing both times.
        Assert.NotEqual(SKColors.White, midStroke);

        session.EndStroke();

        // Forced, rather than reading whatever the merge left in the composite. The merge marks
        // nothing stale on purpose, so without this both reads could be the same cached pixel and
        // the comparison would say nothing about what the layers actually hold.
        session.InvalidateComposite();
        var afterLift = session.Bitmap.GetPixel(120, 100);

        Assert.Equal(afterLift, midStroke);
    }

    [Fact]
    public void Merging_the_stroke_layer_changes_nothing_on_screen()
    {
        // Why the merge marks nothing stale: the composite has been drawing the stroke layer
        // directly above its own layer with source-over, and source-over is associative, so the
        // picture after the merge is the picture that was already there.
        using var session = new PaintSession(240, 200) { Compositing = StrokeCompositing.Wash };

        session.SetStrokeColor(Red);
        var brush = Opaque(40);
        for (double x = 20; x <= 220; x += 2) session.AddSample(x, 100, 1.0, brush);
        session.EndStroke();

        using var asLeft = session.Bitmap.Copy();
        session.InvalidateComposite();
        using var recomposited = session.Bitmap.Copy();

        var diff = FirstDifference(asLeft, recomposited);
        Assert.True(diff is null,
            diff is null ? "" :
            $"the merge changed the picture at {diff.Value.X},{diff.Value.Y}: " +
            $"{diff.Value.A} became {diff.Value.B}, so it cannot skip marking the region stale");
    }

    [Fact]
    public void A_stroke_in_progress_goes_under_the_layer_above_it()
    {
        // The other half of the same decision. A stroke drawn on the bottom layer must go under
        // the one above while it is still being drawn, not over everything.
        using var session = new PaintSession(240, 200) { Compositing = StrokeCompositing.Wash };

        // Blue covers the left half only, so the same in-progress stroke can be read in both
        // states -- hidden under the blue, and visible past the end of it. Without the second
        // reading this would pass on a build that never composited the stroke at all.
        session.AddLayer();
        Stroke(session, Blue, 20, 100, 120, 100, size: 40);

        Assert.True(session.SetActiveLayer(0));
        session.SetStrokeColor(Red);
        var brush = Opaque(40);
        for (double x = 20; x <= 220; x += 2) session.AddSample(x, 100, 1.0, brush);

        Assert.Equal(Blue, session.Bitmap.GetPixel(70, 100));
        Assert.Equal(Red, session.Bitmap.GetPixel(180, 100));
    }
}
