using PenDynamicsPaint.Paint;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// Pins the property the viewport exists for: <b>zoom and pan move the view, never the ink</b>.
/// </summary>
/// <remarks>
/// <para>
/// PenDynamicsLab has no mapping to get wrong — there the control is the canvas, so a position in
/// one is a position in the other. A document behind a viewport puts every coordinate through a
/// magnification and an offset. That is the part worth holding down, and it is held down here
/// rather than by drawing and looking, because a mapping error of half a pixel is invisible on
/// screen and ruinous in a recording.
/// </para>
/// <para>
/// No window and no Avalonia: <see cref="PaintViewport"/> is doubles in and doubles out on purpose.
/// </para>
/// </remarks>
public class PaintViewportTests
{
    private const double Tol = 1e-9;

    private static void AssertClose(double expected, double actual, string what)
        => Assert.True(Math.Abs(expected - actual) < Tol,
                       $"{what}: expected {expected}, got {actual}");

    [Fact]
    public void Document_to_viewport_and_back_is_the_identity()
    {
        var v = new PaintViewport { Zoom = 2.375, PanX = -140.25, PanY = 61.5 };

        foreach (var (x, y) in new[] { (0.0, 0.0), (17.3, 900.125), (-52.0, 4.5), (1024.0, 768.0) })
        {
            var (vx, vy) = v.ToViewport(x, y);
            var (dx, dy) = v.ToDocument(vx, vy);
            AssertClose(x, dx, "round trip X");
            AssertClose(y, dy, "round trip Y");
        }
    }

    [Fact]
    public void Zoom_does_not_move_the_ink()
    {
        // The acceptance property. The same document position is the same document
        // position at any magnification -- what changes is only where it appears on screen.
        var a = new PaintViewport { Zoom = 1.0, PanX = 0, PanY = 0 };
        var b = new PaintViewport { Zoom = 2.0, PanX = 0, PanY = 0 };

        var (ax, ay) = a.ToViewport(300, 200);
        var (bx, by) = b.ToViewport(300, 200);

        // It lands somewhere different on screen...
        Assert.NotEqual(ax, bx);
        Assert.NotEqual(ay, by);

        // ...and is the same place in the document, which is the half that matters.
        var (adx, ady) = a.ToDocument(ax, ay);
        var (bdx, bdy) = b.ToDocument(bx, by);
        AssertClose(300, adx, "document X at 100%");
        AssertClose(300, bdx, "document X at 200%");
        AssertClose(200, ady, "document Y at 100%");
        AssertClose(200, bdy, "document Y at 200%");
    }

    [Fact]
    public void A_pen_held_still_while_the_view_zooms_would_draw_somewhere_else()
    {
        // The other side of the same coin, and the one that catches a mapping that ignores zoom.
        // The pen is at a fixed place on the glass; magnifying the view must change which part of
        // the document is under it.
        var v = new PaintViewport { Zoom = 1.0, PanX = 0, PanY = 0 };
        var (before, _) = v.ToDocument(400, 400);

        v.Zoom = 2.0;
        var (after, _) = v.ToDocument(400, 400);

        AssertClose(400, before, "document X at 100%");
        AssertClose(200, after, "document X at 200%");
    }

    [Fact]
    public void Zooming_about_a_point_keeps_that_point_still()
    {
        var v = new PaintViewport { Zoom = 1.0, PanX = 30, PanY = 12 };
        var (anchorX, anchorY) = v.ToDocument(250, 180);

        v.ZoomAt(2.0, 250, 180);

        var (stillX, stillY) = v.ToDocument(250, 180);
        AssertClose(anchorX, stillX, "anchor X");
        AssertClose(anchorY, stillY, "anchor Y");
        AssertClose(2.0, v.Zoom, "zoom");
    }

    [Fact]
    public void Zooming_about_a_point_repeatedly_does_not_drift()
    {
        // A wheel produces many small steps, so an anchor that is only approximately fixed walks
        // the document out from under the pointer over a dozen notches.
        var v = new PaintViewport { Zoom = 1.0, PanX = 0, PanY = 0 };
        var (anchorX, anchorY) = v.ToDocument(640, 360);

        for (int i = 0; i < 24; i++) v.ZoomAt(1.1, 640, 360);
        for (int i = 0; i < 24; i++) v.ZoomAt(1 / 1.1, 640, 360);

        var (endX, endY) = v.ToDocument(640, 360);
        Assert.True(Math.Abs(anchorX - endX) < 1e-6, $"anchor X drifted to {endX}");
        Assert.True(Math.Abs(anchorY - endY) < 1e-6, $"anchor Y drifted to {endY}");
    }

    [Fact]
    public void The_anchor_survives_the_zoom_being_clamped()
    {
        // Winding the wheel past the limit must not keep moving the paper. Zoom stops; pan must
        // stop with it, or the document slides away while the magnification stays put.
        var v = new PaintViewport { Zoom = PaintViewport.MaxZoom, PanX = 5, PanY = 7 };
        double panX = v.PanX, panY = v.PanY;

        v.ZoomAt(2.0, 300, 300);

        AssertClose(PaintViewport.MaxZoom, v.Zoom, "zoom stays clamped");
        AssertClose(panX, v.PanX, "pan X unchanged");
        AssertClose(panY, v.PanY, "pan Y unchanged");
    }

    [Fact]
    public void A_pan_mid_stroke_moves_the_document_under_the_pen_by_exactly_the_pan()
    {
        // Singled out because it is the one a cached origin gets wrong. Two samples
        // at the same place on the glass, with a pan between them, must differ in the document by
        // exactly the pan -- no more, and not zero.
        var v = new PaintViewport { Zoom = 2.0, PanX = 0, PanY = 0 };

        var (firstX, firstY) = v.ToDocument(500, 300);
        v.PanByViewport(80, -40);          // the pointer dragged right and up
        var (secondX, secondY) = v.ToDocument(500, 300);

        // Dragging right pulls the paper right, so the part of the document under a fixed point on
        // the glass moves back towards the origin. The delta is the drag converted to document
        // units, negated -- 80 DIP at 200% is 40 document units.
        AssertClose(-40, secondX - firstX, "document shift X");
        AssertClose(+20, secondY - firstY, "document shift Y");
    }

    [Fact]
    public void Panning_is_in_document_units_so_it_slows_down_as_you_zoom_in()
    {
        var far = new PaintViewport { Zoom = 0.5 };
        var near = new PaintViewport { Zoom = 4.0 };

        far.PanByViewport(100, 0);
        near.PanByViewport(100, 0);

        AssertClose(-200, far.PanX, "pan at 50%");
        AssertClose(-25, near.PanX, "pan at 400%");
    }

    [Fact]
    public void Fit_centres_the_document_and_leaves_a_margin()
    {
        var v = new PaintViewport();
        v.FitToViewport(documentWidth: 1000, documentHeight: 500,
                        viewportWidth: 800, viewportHeight: 600, margin: 0.0);

        AssertClose(0.8, v.Zoom, "zoom fits the tighter axis");

        // The document's centre lands at the viewport's centre.
        var (cx, cy) = v.ToViewport(500, 250);
        AssertClose(400, cx, "centre X");
        AssertClose(300, cy, "centre Y");
    }

    [Fact]
    public void Zoom_is_clamped_and_survives_nonsense()
    {
        var v = new PaintViewport();

        v.Zoom = 0;
        AssertClose(PaintViewport.MinZoom, v.Zoom, "zero");

        v.Zoom = 1e9;
        AssertClose(PaintViewport.MaxZoom, v.Zoom, "huge");

        v.Zoom = double.NaN;
        AssertClose(1.0, v.Zoom, "NaN falls back to 1 rather than poisoning every later mapping");
    }
}
