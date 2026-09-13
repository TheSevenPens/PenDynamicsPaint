namespace PenDynamicsPaint.Drawing.MyPaint;

/// <summary>
/// One input's contribution to one setting: a piecewise-linear curve read off its control points.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>mypaint_mapping_calculate</c> in <c>mypaint/libmypaint</c> at <c>v1.6.1</c>
/// (<c>mypaint-mapping.c</c>).
/// </para>
/// <para>
/// <b>The curve is an offset, not a multiplier.</b> A setting's value is its base value plus the
/// output of every input curve attached to it, summed. That is why a flat curve at zero is the
/// same as no curve at all, and why two inputs can pull a setting in opposite directions and
/// cancel.
/// </para>
/// <para>
/// <b>Beyond the outermost points the terminal segment is extrapolated, not clamped.</b> That is
/// what the C does -- the segment search stops at the last pair and the interpolation runs with an
/// x outside it -- and it matters: a pressure curve drawn over 0..1 still has an opinion about a
/// tablet that reports 1.4, and clamping instead would quietly flatten the brush there. Pinned in
/// <c>InputMappingTests</c> rather than left to be discovered.
/// </para>
/// </remarks>
public sealed class InputMapping
{
    private readonly float[] _x;
    private readonly float[] _y;

    /// <summary>Build a curve through the given control points, which must be ordered by x.</summary>
    /// <remarks>
    /// Fewer than two points cannot define a segment, so such a curve contributes nothing at all --
    /// the same as being absent, which is how libmypaint treats it.
    /// </remarks>
    public InputMapping(IReadOnlyList<(float X, float Y)> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        _x = [.. points.Select(p => p.X)];
        _y = [.. points.Select(p => p.Y)];
    }

    /// <summary>Control points, in order.</summary>
    public IReadOnlyList<(float X, float Y)> Points =>
        [.. _x.Select((x, i) => (x, _y[i]))];

    /// <summary>True when this curve has nothing to say and can be skipped.</summary>
    public bool IsEmpty => _x.Length < 2;

    /// <summary>What this curve adds to its setting for a given input value.</summary>
    public float Apply(float input)
    {
        if (IsEmpty) return 0;

        float x0 = _x[0], y0 = _y[0], x1 = _x[1], y1 = _y[1];

        // Walk forward to the segment this input falls in. Stopping at the last pair is what
        // makes the ends extrapolate rather than clamp.
        for (int i = 2; i < _x.Length && input > x1; i++)
        {
            x0 = x1;
            y0 = y1;
            x1 = _x[i];
            y1 = _y[i];
        }

        // Two points at the same x would divide by zero, so that one is answered by its own y.
        //
        // The flat case is a shortcut and nothing more: the interpolation below already returns y0
        // when y0 and y1 are equal, since the two terms sum to y0 * (x1 - x0). It is kept because
        // the C has it, and noted because a reader would otherwise reasonably assume it changes
        // an answer somewhere.
        if (x0 == x1 || y0 == y1) return y0;

        return (y1 * (input - x0) + y0 * (x1 - input)) / (x1 - x0);
    }
}
