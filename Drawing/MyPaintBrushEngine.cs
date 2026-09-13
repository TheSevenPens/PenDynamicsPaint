using PenDynamicsPaint.Drawing.MyPaint;
using SkiaSharp;

namespace PenDynamicsPaint.Drawing;

/// <summary>
/// Stamps dabs whose size, opacity, spacing and softness are decided per dab by a MyPaint brush.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DabBrushEngine"/> with the numbers coming from somewhere else. That engine takes its
/// radius from pressure through one curve; this one evaluates a <see cref="MyPaintBrush"/> at
/// every dab, against inputs that include speed, direction, tilt, barrel rotation, how far into
/// the stroke the pen is, and a fresh random value. The spacing rule underneath is the same
/// <see cref="DabSpacing"/>, because distance spacing is distance spacing whatever decides the
/// gap.
/// </para>
/// <para>
/// <b>What is honoured, and what is not.</b> Radius, both opacities, the pile-up correction,
/// hardness, spacing, elliptical dabs and the two random offsets reach the mark. Smudge, colour
/// dynamics, tracking and the eraser do not: smudge needs a read of the canvas, and the rest
/// belong to parts of the pipeline that have their own answers here already. A brush file that
/// leans on any of them still loads, and says so through <see cref="MyPaintBrush.Ignored"/>.
/// </para>
/// </remarks>
public sealed class MyPaintBrushEngine : IBrushEngine
{
    /// <summary>How many stops the dab's falloff is sampled into.</summary>
    /// <remarks>
    /// The profile is two straight lines in the <b>square</b> of the normalised radius, so it is a
    /// curve in the radius itself and cannot be handed to a gradient as two stops. Sixteen is
    /// enough that the banding is finer than a pixel at the sizes a dab is drawn at.
    /// </remarks>
    private const int FalloffStops = 16;

    private readonly DabSpacing _spacing = new();
    private readonly BrushInputTracker _inputs;
    private readonly Random _jitter;

    private readonly SKPaint _paint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
    };

    private readonly float[] _stopPositions = new float[FalloffStops];
    private readonly SKColor[] _stopColors = new SKColor[FalloffStops];

    private DocumentPoint _previous;
    private bool _started;

    /// <summary>Time since the last dab that no segment has accounted for yet.</summary>
    /// <remarks>
    /// A segment often places no dab at all -- that is what carrying the spacing accumulator
    /// across segments means -- and the time it took still has to reach the next one, or a brush
    /// reading speed would think the pen had teleported.
    /// </remarks>
    private double _carried;

    public MyPaintBrushEngine(int? seed = null)
    {
        _inputs = new BrushInputTracker(seed);
        _jitter = seed is { } s ? new Random(s ^ 0x5eed) : new Random();
    }

    /// <inheritdoc />
    public SKBlender? Blender { get; set; }

    /// <summary>
    /// False: MyPaint's dabs accumulate, and alpha-darkening them combs every stroke.
    /// </summary>
    /// <remarks>
    /// The dabs are spaced a fraction of a radius apart and each is soft, so the greater alpha of
    /// two neighbours dips between their centres. Accumulating fills that in, which is what the
    /// brush files expect -- their opacity values are chosen against a model that builds up.
    /// </remarks>
    public bool AlphaDarkenWithinStroke => false;

    /// <summary>True when the pen gave no clock, so speed was computed against a nominal rate.</summary>
    public bool UsedNominalTime => _inputs.UsedNominalTime;

    /// <inheritdoc />
    public void BeginStroke()
    {
        _spacing.Reset();
        _inputs.Reset();
        _started = false;
        _carried = 0;
    }

    /// <inheritdoc />
    public void EndStroke() { }

    /// <inheritdoc />
    /// <inheritdoc />
    /// <remarks>
    /// Accumulated from the dabs actually placed, because the radius of any one of them is not
    /// known until its inputs have been worked out -- speed, tilt and the custom input all bend
    /// it, and the custom input carries state from the dab before. Nothing short of placing them
    /// can say where they land.
    /// </remarks>
    public SKRect LastSegmentBounds { get; private set; }

    public void DrawSegment(SKCanvas canvas, in StrokeSample from, in StrokeSample to,
        BrushSettings brush, SKColor color, PressureChannel channel)
    {
        var mypaint = brush.MyPaint ?? MyPaintBrush.Default;

        LastSegmentBounds = SKRect.Empty;

        double length = from.Position.DistanceTo(to.Position);
        double seconds = Interval(from, to);

        // The brush's own radius, before anything bends it. Spacing and the stroke input are both
        // measured against it, so it has to be read once from the base value rather than per dab.
        double baseRadius = Math.Exp(mypaint[MyPaintSetting.RadiusLogarithmic].BaseValue);

        if (!_started)
        {
            _previous = from.Position;
            _started = true;
        }

        double reached = 0;

        // Constant along the segment, which is what lets the spacing rule fold the dab's own
        // metric into a single factor rather than integrating along the path.
        double ux = length > 0 ? (to.Position.X - from.Position.X) / length : 0;
        double uy = length > 0 ? (to.Position.Y - from.Position.Y) / length : 0;

        // Copied out of the `in` parameters, which a lambda may not capture. A sample is a small
        // readonly struct, so this costs nothing worth measuring.
        StrokeSample start = from, end = to;

        foreach (double at in _spacing.Walk(length,
                     d => SpacingAt(d, length, start, end, mypaint, ux, uy)))
        {
            double t = length > 0 ? at / length : 0;
            var here = Lerp(from.Position, to.Position, t);
            var sample = Blend(from, to, t);

            double elapsed = _carried + seconds * (t - reached);
            _carried = 0;
            reached = t;

            var inputs = _inputs.Next(here, _previous, sample, elapsed, mypaint, baseRadius);
            _previous = here;

            Stamp(canvas, here, inputs, mypaint, color);
        }

        _carried += seconds * (1 - reached);
    }

    /// <summary>The gap wanted at a point along the segment, in document units.</summary>
    /// <remarks>
    /// Two settings ask for it, and libmypaint takes whichever wants the tighter spacing: one
    /// measures against the brush's base radius and one against the dab's current radius, so a
    /// brush that shrinks under pressure can be told whether its dabs should close up with it.
    /// </remarks>
    /// <param name="ux">Unit direction of travel, x. See the elliptical branch below.</param>
    /// <param name="uy">Unit direction of travel, y.</param>
    private double SpacingAt(double distance, double length, in StrokeSample from,
                             in StrokeSample to, MyPaintBrush brush, double ux, double uy)
    {
        double t = length > 0 ? distance / length : 0;
        var sample = Blend(from, to, t);

        var inputs = _inputs.Peek(sample);

        double radius = Radius(brush, inputs);
        double baseRadius = Math.Exp(brush[MyPaintSetting.RadiusLogarithmic].BaseValue);

        double perActual = brush[MyPaintSetting.DabsPerActualRadius].ValueFor(inputs);
        double perBasic = brush[MyPaintSetting.DabsPerBasicRadius].ValueFor(inputs);

        double gap = double.MaxValue;
        if (perActual > 0) gap = Math.Min(gap, radius / perActual);
        if (perBasic > 0) gap = Math.Min(gap, baseRadius / perBasic);

        // No spacing setting at all would place dabs forever; the walk's own floor would catch it,
        // but a dab per radius is a more useful answer than a dab every half unit.
        if (gap == double.MaxValue) gap = radius;

        // An elliptical dab measures distance in its own metric: libmypaint stretches the step by
        // the aspect ratio across the narrow axis before counting dabs into it, so a nib dragged
        // sideways lays them closer together than the same nib drawn along its length. That is the
        // difference between a nib and an oval stamp, and it is why the ratio is worked out before
        // the spacing rather than at the dab.
        //
        // The walk measures real distance, so the stretch is divided out of the gap instead. Same
        // dabs in the same places: the segment is straight, so the factor is constant along it.
        double stretch = Stretch(brush, inputs, ux, uy);
        return stretch > 0 ? gap / stretch : gap;
    }

    /// <summary>
    /// How much longer a step along <paramref name="ux"/>, <paramref name="uy"/> is in the dab's
    /// own metric than on the page. 1 for a round dab, up to the aspect ratio across the nib.
    /// </summary>
    private static double Stretch(MyPaintBrush brush, in BrushInputs inputs, double ux, double uy)
    {
        double ratio = Ratio(brush, inputs);
        if (ratio <= 1) return 1;

        (double cs, double sn) = AxisOf(brush, inputs);

        double across = (uy * cs - ux * sn) * ratio;
        double along = uy * sn + ux * cs;

        return Math.Sqrt(across * across + along * along);
    }

    /// <summary>The dab's aspect ratio, never below 1 -- libmypaint treats a smaller one as round.</summary>
    private static double Ratio(MyPaintBrush brush, in BrushInputs inputs) =>
        Math.Max(1.0, brush[MyPaintSetting.EllipticalDabRatio].ValueFor(inputs));

    /// <summary>Cosine and sine of the long axis's angle.</summary>
    /// <remarks>
    /// The angle is used as the file gives it. libmypaint folds it into a half turn first, which
    /// changes nothing: shifting by 180 degrees negates both the cosine and the sine, and the
    /// geometry only ever uses their squares or a rotation of a shape with the same symmetry.
    /// </remarks>
    private static (double Cos, double Sin) AxisOf(MyPaintBrush brush, in BrushInputs inputs)
    {
        double radians = brush[MyPaintSetting.EllipticalDabAngle].ValueFor(inputs) * Math.PI / 180.0;
        return (Math.Cos(radians), Math.Sin(radians));
    }

    private static double Radius(MyPaintBrush brush, in BrushInputs inputs) =>
        Math.Exp(brush[MyPaintSetting.RadiusLogarithmic].ValueFor(inputs));

    private void Stamp(SKCanvas canvas, DocumentPoint at, in BrushInputs inputs,
                       MyPaintBrush brush, SKColor color)
    {
        double radius = Radius(brush, inputs);

        double byRandom = brush[MyPaintSetting.RadiusByRandom].ValueFor(inputs);
        if (byRandom > 0) radius *= Math.Exp(byRandom * (_jitter.NextDouble() * 2 - 1));

        if (radius <= 0 || !double.IsFinite(radius)) return;

        double offset = brush[MyPaintSetting.OffsetByRandom].ValueFor(inputs);
        if (offset > 0)
        {
            at = new DocumentPoint(at.X + offset * radius * Gaussian(),
                                   at.Y + offset * radius * Gaussian());
        }

        float alpha = Alpha(brush, inputs);
        if (alpha <= 0) return;

        float hardness = Math.Clamp(brush[MyPaintSetting.Hardness].ValueFor(inputs), 0f, 1f);

        _paint.Blender = Blender;
        _paint.Color = color.WithAlpha((byte)Math.Clamp(alpha * 255, 0, 255));

        // The long axis keeps the radius and the short one is squeezed, which is why the bounds
        // below still hold: an elliptical dab reaches no further than the round one it came from.
        double ratio = Ratio(brush, inputs);
        bool elliptical = ratio > 1.0001;

        var touched = new SKRect((float)(at.X - radius), (float)(at.Y - radius),
                                 (float)(at.X + radius), (float)(at.Y + radius));
        LastSegmentBounds = LastSegmentBounds.IsEmpty ? touched
                                                      : SKRect.Union(LastSegmentBounds, touched);

        // Drawn as a round dab in a squeezed frame rather than as an ellipse. The falloff is a
        // sampled radial gradient, and a gradient goes through the canvas transform with the shape
        // it fills -- so this squeezes the softness along with the outline, which an ellipse drawn
        // with a round gradient would not.
        if (elliptical)
        {
            canvas.Save();
            canvas.Translate((float)at.X, (float)at.Y);
            canvas.RotateDegrees((float)brush[MyPaintSetting.EllipticalDabAngle].ValueFor(inputs));
            canvas.Scale(1f, (float)(1.0 / ratio));
        }

        var centre = elliptical ? SKPoint.Empty : new SKPoint((float)at.X, (float)at.Y);

        // The shader carries the falloff at full strength and the paint's own alpha scales it.
        // Building the dab's alpha into the stops as well would apply it twice, which squares it:
        // a dab asked for at half strength would arrive at a quarter.
        _paint.Shader = hardness >= 1f ? null : Falloff(centre, radius, color, hardness);

        canvas.DrawCircle(centre.X, centre.Y, (float)radius, _paint);

        _paint.Shader = null;
        if (elliptical) canvas.Restore();
    }

    /// <summary>Dab alpha: the two opacity settings, multiplied, then thinned for the pile-up.</summary>
    /// <remarks>
    /// <para>
    /// Two opacity settings rather than one because a brush wants somewhere to put a fixed
    /// strength and somewhere to attach pressure, and a single setting cannot hold both without
    /// one overwriting the other. The convention in the brush files is that pressure goes on the
    /// multiplier.
    /// </para>
    /// <para>
    /// The thinning is <c>opaque_linearize</c>, ported from <c>prepare_and_draw_dab</c> in
    /// <c>mypaint-brush.c</c> at <c>v1.6.1</c>. What the two settings state is the opacity the
    /// <b>stroke</b> should reach, not the opacity of one dab, and a brush lays several dabs over
    /// every pixel -- so each dab has to go down fainter or the stroke overshoots. The airbrush
    /// asks for 52% opacity and lays 11.5 dabs per pixel; painted at 52% each, the stroke is solid
    /// black after the third one.
    /// </para>
    /// <para>
    /// <b>The default is 0.9, not 0.</b> So this applies to nearly every brush file, including the
    /// ones that never mention the setting -- which is why leaving it out did not look like one
    /// brush being wrong.
    /// </para>
    /// </remarks>
    private static float Alpha(MyPaintBrush brush, in BrushInputs inputs)
    {
        float opaque = brush[MyPaintSetting.Opaque].ValueFor(inputs);
        float multiply = brush[MyPaintSetting.OpaqueMultiply].ValueFor(inputs);

        // Clamped separately, so that two negative values cannot multiply into a positive one.
        float alpha = Math.Clamp(Math.Max(0, opaque) * multiply, 0, 1);

        float linearize = brush[MyPaintSetting.OpaqueLinearize].BaseValue;
        if (linearize == 0) return alpha;

        float perPixel = (brush[MyPaintSetting.DabsPerActualRadius].ValueFor(inputs)
                        + brush[MyPaintSetting.DabsPerBasicRadius].ValueFor(inputs)) * 2f;

        // Dabs that do not overlap have nothing to correct for, and the setting scales how much
        // of the correction to apply rather than switching it on and off.
        perPixel = 1f + linearize * (Math.Max(1f, perPixel) - 1f);

        // What survives one dab is the whole stroke's transmission spread over the pile:
        // (1 - stroke) == (1 - dab)^perPixel.
        return 1f - MathF.Pow(1f - alpha, 1f / perPixel);
    }

    /// <summary>
    /// The dab's soft edge, as a radial gradient sampled from libmypaint's profile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two straight lines in <c>rr</c>, the square of the normalised distance from the centre:
    /// solid out to <c>rr == hardness</c>, then falling to nothing at the rim. Ported from
    /// <c>calculate_opa</c> in <c>mypaint-tiled-surface.c</c>.
    /// </para>
    /// <para>
    /// Being linear in the square means it is not linear in the radius, which is why the gradient
    /// is sampled rather than given two stops. Getting that wrong produces a dab that looks
    /// plausible and is the wrong shape, which is worse than one that looks wrong.
    /// </para>
    /// </remarks>
    /// <param name="centre">Where the dab is <b>in the canvas's current frame</b>, which for an
    /// elliptical dab is the origin of the squeezed one rather than a point on the page.</param>
    /// <param name="color">The ink, at full alpha. The dab's own alpha is the paint's.</param>
    private SKShader Falloff(SKPoint centre, double radius, SKColor color, float hardness)
    {
        hardness = Math.Max(hardness, 1e-4f);

        float segment1Slope = -(1f / hardness - 1f);
        float segment2Offset = hardness / (1f - hardness);
        float segment2Slope = -segment2Offset;

        for (int i = 0; i < FalloffStops; i++)
        {
            float r = (float)i / (FalloffStops - 1);
            float rr = r * r;

            float opacity = rr <= hardness
                ? 1f + rr * segment1Slope
                : segment2Offset + rr * segment2Slope;

            _stopPositions[i] = r;
            _stopColors[i] = color.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255));
        }

        return SKShader.CreateRadialGradient(
            centre, (float)radius, _stopColors, _stopPositions, SKShaderTileMode.Clamp);
    }

    /// <summary>A rough normal deviate, for throwing a dab off the path.</summary>
    /// <remarks>
    /// The sum of three uniforms rather than a Box-Muller pair: it is bounded, which keeps a
    /// single dab from being flung across the document, and the difference is invisible in ink.
    /// </remarks>
    private double Gaussian() =>
        _jitter.NextDouble() + _jitter.NextDouble() + _jitter.NextDouble() - 1.5;

    /// <summary>Seconds between two samples, or zero when the pen reported no clock.</summary>
    private static double Interval(in StrokeSample from, in StrokeSample to)
    {
        if (from.TimestampMicroseconds <= 0 || to.TimestampMicroseconds <= 0) return 0;

        long micros = to.TimestampMicroseconds - from.TimestampMicroseconds;
        return micros > 0 ? micros / 1_000_000.0 : 0;
    }

    private static DocumentPoint Lerp(DocumentPoint a, DocumentPoint b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static StrokeSample Blend(in StrokeSample from, in StrokeSample to, double t) =>
        to with
        {
            RawPressure = from.RawPressure + (to.RawPressure - from.RawPressure) * t,
            ProcessedPressure = from.ProcessedPressure +
                                (to.ProcessedPressure - from.ProcessedPressure) * t,
        };

    public void Dispose() => _paint.Dispose();
}
