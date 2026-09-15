using SkiaSharp;

namespace PenDynamicsPaint.Drawing;

/// <summary>
/// Puts one segment of a stroke onto a canvas.
/// </summary>
/// <remarks>
/// <para>
/// One implementation today — <see cref="RoundBrushEngine"/>, an antialiased taper between two
/// round ends. The interface exists so a stamp engine can be added without touching the window or
/// the drawing session: it would interpolate dabs between <c>from</c> and <c>to</c> rather than
/// filling a taper, and nothing above it needs to know.
/// </para>
/// <para>
/// <b>Engines receive samples, not geometry.</b> The width and opacity a mark is drawn at are
/// computed here rather than by the caller, because an engine that places marks by distance or by
/// elapsed time needs the things a width has already thrown away. A dab engine needs pressure per
/// dab, since spacing depends on dab size which depends on pressure; anything driven by time needs
/// <see cref="StrokeSample.TimestampMicroseconds"/>; libmypaint wants tilt and barrel rotation and
/// decides radius itself. A caller that reduced pressure to a number first would foreclose all of
/// that, which is what this interface used to do.
/// </para>
/// <para>
/// Coordinates are DIPs. <c>DrawSurface</c>'s canvas transform converts to physical pixels, and
/// is the only place that knows the display scaling.
/// </para>
/// </remarks>
public interface IBrushEngine : IDisposable
{
    /// <summary>
    /// How each mark composites into whatever it is drawn on. Null means ordinary source-over.
    /// </summary>
    /// <remarks>
    /// Set by the caller for the life of a stroke rather than passed per mark, because it is a
    /// property of how the stroke is being composited and not of any one segment. Wash sets
    /// <see cref="AlphaDarken.Blender"/> here and draws into a layer of its own; direct painting
    /// leaves it null.
    /// </remarks>
    SKBlender? Blender { get; set; }

    /// <summary>
    /// Whether overlapping marks within one stroke should take the greater alpha rather than
    /// accumulating.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True for the engines here, and it is what Wash is for: a swept taper puts about a hundred
    /// overlapping marks over each pixel at tablet rates, so letting them accumulate turns 15%
    /// pressure into 99% ink.
    /// </para>
    /// <para>
    /// <b>False for an engine whose marks are meant to build up.</b> MyPaint's dabs are: its
    /// opacity settings are tuned against accumulation, its dabs are deliberately spaced apart,
    /// and taking the greater alpha of two soft overlapping dabs scallops the edge between them --
    /// a comb pattern along every stroke at exactly the dab spacing.
    /// </para>
    /// </remarks>
    bool AlphaDarkenWithinStroke => true;

    /// <summary>Where the last <see cref="DrawSegment"/> put ink.</summary>
    /// <remarks>
    /// <para>
    /// Reported by the engine rather than worked out by the caller, because only the engine knows
    /// how big its marks are. The caller used to compute this from
    /// <see cref="BrushSettings.StrokeWidthFor"/>, which is the size slider -- true for an engine
    /// whose mark is the slider's width, and wrong for one that decides its own. A MyPaint brush
    /// at <c>radius_logarithmic 4.7</c> lays dabs 110 units across whatever the slider says, so
    /// the region the composite was told to refresh was a tenth of the region that had changed.
    /// </para>
    /// <para>
    /// <b>Overstating this is safe and understating it is not.</b> Too large means compositing
    /// pixels that did not need it; too small leaves stale ones on screen, which is what the
    /// airbrush showed as rectangular blocks of older paint.
    /// </para>
    /// </remarks>
    SKRect LastSegmentBounds { get; }

    /// <summary>
    /// The pixels an engine may read while it draws, or null when it may read nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by the caller for the life of a stroke, like <see cref="Blender"/>, and for the same
    /// reason: it is a property of what the stroke is being drawn onto rather than of any one
    /// segment.
    /// </para>
    /// <para>
    /// Only a smudging brush needs it, and needing it changes where the stroke goes. Wash draws
    /// into a layer of its own and merges once, so an engine reading that layer would see only the
    /// marks it had just made and none of the painting it is supposed to be dragging around. A
    /// stroke that reads the canvas therefore paints straight onto the layer, which is what
    /// libmypaint does for every stroke -- it has no such intermediate.
    /// </para>
    /// </remarks>
    SKBitmap? SampleSource { get; set; }

    /// <summary>
    /// Whether a stroke with these settings will read the canvas, and so has to paint onto it
    /// directly.
    /// </summary>
    /// <remarks>
    /// A question about the brush rather than the engine: the same MyPaint engine smudges or does
    /// not depending on what the file asks for.
    /// </remarks>
    bool SamplesTheCanvas(BrushSettings brush) => false;

    /// <summary>A stroke is starting. Clear anything carried between segments.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="RoundBrushEngine"/> carries nothing and does nothing here. An engine that places
    /// marks by accumulated distance or elapsed time does, and without this its accumulator would
    /// run on from the previous stroke -- every mark after the first would land in the wrong place,
    /// for the rest of the session.
    /// </para>
    /// <para>
    /// <b>State must be kept per <see cref="PressureChannel"/>, not per engine.</b> One gesture
    /// draws both surfaces through the same engine instance, alternating: processed, raw,
    /// processed, raw. A single accumulator would be advanced by both streams and describe
    /// neither. This method resets every channel, because a stroke begins for all of them at once
    /// -- it is the per-mark <c>channel</c> argument that separates them, not this.
    /// </para>
    /// </remarks>
    void BeginStroke();

    /// <summary>The stroke has ended. Nothing further will be drawn until the next one begins.</summary>
    /// <remarks>
    /// Separate from <see cref="BeginStroke"/> so an engine that defers work has somewhere to
    /// finish it. Wash is the case in hand: a stroke composites into its own layer as it is drawn
    /// and merges onto the document once, here, when it is known to be complete.
    /// </remarks>
    void EndStroke();

    /// <summary>Draw from <paramref name="from"/> to <paramref name="to"/> on the canvas.</summary>
    /// <param name="brush">
    /// The settings in force for this stroke. <see cref="BrushSettings.StrokeWidthFor"/> and
    /// <see cref="BrushSettings.OpacityFor"/> turn a pressure into a mark, and it is the engine's
    /// business when and how often to call them.
    /// </param>
    void DrawSegment(SKCanvas canvas, in StrokeSample from, in StrokeSample to,
        BrushSettings brush, SKColor color);
}

/// <summary>An antialiased taper between two round ends.</summary>
/// <remarks>
/// <para>
/// This used to stroke a straight line at a single width, taken from the sample at the end of the
/// segment. Width therefore <b>stepped</b> at every sample boundary rather than changing along
/// the segment, and the silhouette of a stroke was a staircase wherever pressure moved — which at
/// tablet report rates is everywhere. Tapering between the two samples' widths is what turns that
/// staircase into a ramp.
/// </para>
/// <para>
/// Consecutive segments share an endpoint <i>and</i> a width — segment i ends at the width segment
/// i+1 starts at — so the ramp is continuous across the whole stroke rather than only within each
/// piece of it.
/// </para>
/// </remarks>
public sealed class RoundBrushEngine : IBrushEngine
{
    // One paint and one path for the life of the engine. These used to be allocated per segment —
    // on every point, for both surfaces, at 16 ms — which is a lot of garbage for a few writes.
    private readonly SKPaint _paint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
    };

    private readonly SKPath _path = new();

    /// <inheritdoc />
    public SKBlender? Blender { get; set; }

    /// <inheritdoc />
    /// <remarks>The taper's own outline, which is exactly the shape that was filled.</remarks>
    public SKRect LastSegmentBounds { get; private set; }

    /// <summary>Never read: a taper's colour is the ink it was given.</summary>
    public SKBitmap? SampleSource { get; set; }

    /// <summary>Nothing to reset: every mark is decided by the two samples it is drawn from.</summary>
    /// <remarks>
    /// Empty on purpose rather than absent. That this engine needs no per-stroke state is a
    /// property of a swept taper, not of brush engines, and the next one will need both.
    /// </remarks>
    public void BeginStroke() { }

    /// <inheritdoc cref="BeginStroke"/>
    public void EndStroke() { }

    public void DrawSegment(SKCanvas canvas, in StrokeSample from, in StrokeSample to,
        BrushSettings brush, SKColor color)
    {
        // The reduction the caller used to perform. Doing it here changes nothing about the mark
        // and is the whole point of the interface taking samples: an engine that wanted pressure
        // per dab rather than per segment could call these as often as it liked.
        double pressureFrom = from.RawPressure;
        double pressureTo = to.RawPressure;

        float widthFrom = brush.StrokeWidthFor(pressureFrom);
        float widthTo = brush.StrokeWidthFor(pressureTo);

        // Opacity comes from the end of the segment, as it always has. A taper has one alpha for
        // the whole filled path, so there is nothing to ramp it across.
        byte alpha = (byte)Math.Clamp(brush.OpacityFor(pressureTo) * 255, 0, 255);
        _paint.Color = color.WithAlpha(alpha);
        _paint.Blender = Blender;

        BuildTaper(_path,
            new SKPoint((float)from.Position.X, (float)from.Position.Y), widthFrom / 2f,
            new SKPoint((float)to.Position.X, (float)to.Position.Y), widthTo / 2f);

        LastSegmentBounds = _path.Bounds;
        canvas.DrawPath(_path, _paint);
    }

    /// <summary>
    /// Build the outline of two circles and the region swept between them, into
    /// <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One closed contour, not three overlapping shapes.</b> The obvious construction — a
    /// quad between the tangent points plus a circle at each end — is wrong as soon as the brush
    /// is translucent, because the overlaps get painted twice and show as darker lozenges at
    /// every sample. A single filled path has no overlaps to double.
    /// </para>
    /// <para>
    /// The straight sides are the circles' external tangents. A side that is tangent meets each
    /// radius at a right angle, so the tangent points sit at <c>acos((ra - rb) / d)</c> from the
    /// centre line: the normal to the centre line, turned <b>back towards the wide end</b> by
    /// <c>asin((ra - rb) / d)</c>. Turning it the other way gives a shape that still closes and
    /// still looks like a taper, with sides that cut across the caps instead of meeting them.
    /// </para>
    /// </remarks>
    internal static void BuildTaper(SKPath path, SKPoint a, float ra, SKPoint b, float rb)
    {
        path.Reset();

        ra = Math.Max(ra, 0.01f);
        rb = Math.Max(rb, 0.01f);

        float dx = b.X - a.X, dy = b.Y - a.Y;
        float d = MathF.Sqrt(dx * dx + dy * dy);

        // Degenerate cases, both of which have no tangents to compute: the centres coincide, or
        // one circle swallows the other. The union is then just the larger circle.
        if (d <= MathF.Abs(ra - rb) + 1e-4f)
        {
            if (ra >= rb) path.AddCircle(a.X, a.Y, ra);
            else path.AddCircle(b.X, b.Y, rb);
            return;
        }

        float phi = MathF.Atan2(dy, dx);
        float alpha = MathF.Asin(Math.Clamp((ra - rb) / d, -1f, 1f));

        // The external tangent points, one either side of the axis. The offset is subtracted
        // from the normal on both sides, which is what leans the sides in towards the narrow end.
        float up = phi + MathF.PI / 2 - alpha;
        float down = phi - MathF.PI / 2 + alpha;

        float alphaDeg = alpha * 180f / MathF.PI;

        path.MoveTo(a.X + ra * MathF.Cos(up), a.Y + ra * MathF.Sin(up));
        path.LineTo(b.X + rb * MathF.Cos(up), b.Y + rb * MathF.Sin(up));

        // Round the far end, then the near one. Both sweeps run the same way round so the contour
        // stays simple, and together they account for the full 360 degrees the two caps share.
        // The wide end takes the larger share of it, being the end that bulges out past its own
        // tangent points.
        path.ArcTo(Bounds(b, rb), Deg(up), -(180f - 2f * alphaDeg), false);
        path.LineTo(a.X + ra * MathF.Cos(down), a.Y + ra * MathF.Sin(down));
        path.ArcTo(Bounds(a, ra), Deg(down), -(180f + 2f * alphaDeg), false);

        path.Close();

        static SKRect Bounds(SKPoint c, float r) => new(c.X - r, c.Y - r, c.X + r, c.Y + r);
        static float Deg(float radians) => radians * 180f / MathF.PI;
    }

    public void Dispose()
    {
        _paint.Dispose();
        _path.Dispose();
    }
}
