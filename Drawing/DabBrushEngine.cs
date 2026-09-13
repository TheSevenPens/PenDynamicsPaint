using SkiaSharp;

namespace PenDynamicsPaint.Drawing;

/// <summary>
/// Stamps round marks along the stroke, spaced by distance travelled.
/// </summary>
/// <remarks>
/// <para>
/// The other way of laying down a stroke. <see cref="RoundBrushEngine"/> fills the region swept
/// between two samples, so one event pair makes exactly one mark and the ink follows the tablet's
/// report rate. This carries an accumulated distance across events and stamps whenever enough of it
/// has built up, so one event pair makes zero, one or many marks and the ink follows the path. See
/// <see cref="DabSpacing"/> for the rule and what it buys.
/// </para>
/// <para>
/// Why it matters beyond looking different: a swept taper can only ever be a hard-edged uniform
/// ribbon. Everything expressive -- texture, scatter, rotation following direction, a brush tip
/// that is not a circle -- needs discrete marks to put it on. This is the shape that admits them,
/// and the round dab is the simplest thing that can go in it.
/// </para>
/// <para>
/// <b>Spacing is a fraction of the mark's own size</b>, not a fixed distance, which is what keeps a
/// stroke looking like one stroke as pressure changes its width. Since pressure drives size, it
/// drives spacing with it -- the same coupling Krita's Pixel Brush has.
/// </para>
/// <para>
/// The spacing value itself lives on <see cref="BrushSettings.Spacing"/> rather than here. An
/// engine holding its own configuration would mean a stroke could not record what it was drawn
/// with: the engine is shared between strokes and its settings would be whatever the last change
/// left behind. What stays here is per-stroke state -- the accumulated distance -- which belongs to
/// the act of drawing rather than to the brush.
/// </para>
/// </remarks>
public sealed class DabBrushEngine : IBrushEngine
{
    private readonly DabSpacing _spacing = new();

    // One paint for the life of the engine. Allocating per dab would be allocating per few pixels
    // of travel, which at tablet rates is a great many.
    private readonly SKPaint _paint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
    };

    /// <inheritdoc />
    public SKBlender? Blender { get; set; }

    /// <inheritdoc />
    /// <remarks>Accumulated from the dabs placed, since spacing decides how many there are.</remarks>
    public SKRect LastSegmentBounds { get; private set; }

    /// <inheritdoc />
    public void BeginStroke() => _spacing.Reset();

    /// <inheritdoc />
    public void EndStroke() { }

    /// <inheritdoc />
    public void DrawSegment(SKCanvas canvas, in StrokeSample from, in StrokeSample to,
        BrushSettings brush, SKColor color, PressureChannel channel)
    {
        LastSegmentBounds = SKRect.Empty;

        double x0 = from.Position.X, y0 = from.Position.Y;
        double dx = to.Position.X - x0, dy = to.Position.Y - y0;
        double length = Math.Sqrt(dx * dx + dy * dy);

        double pressureFrom = from.PressureFor(channel);
        double pressureTo = to.PressureFor(channel);

        // Captured rather than recomputed inside the loop body: the walk asks for the spacing at a
        // position before it decides where the mark goes, and the two have to agree about pressure.
        double PressureAt(double distance)
        {
            double t = length > 0 ? distance / length : 0;
            return pressureFrom + (pressureTo - pressureFrom) * t;
        }

        foreach (double at in _spacing.Walk(length, d => brush.StrokeWidthFor(PressureAt(d)) * brush.Spacing))
        {
            double t = at / length;
            double pressure = PressureAt(at);
            float radius = brush.StrokeWidthFor(pressure) / 2f;
            if (radius <= 0) continue;

            byte alpha = (byte)Math.Clamp(brush.OpacityFor(pressure) * 255, 0, 255);
            _paint.Color = color.WithAlpha(alpha);
            _paint.Blender = Blender;

            float cx = (float)(x0 + dx * t), cy = (float)(y0 + dy * t);

            var touched = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
            LastSegmentBounds = LastSegmentBounds.IsEmpty
                ? touched : SKRect.Union(LastSegmentBounds, touched);

            canvas.DrawCircle(cx, cy, radius, _paint);
        }
    }

    public void Dispose() => _paint.Dispose();
}
