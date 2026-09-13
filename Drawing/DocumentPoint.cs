namespace PenDynamicsPaint.Drawing;

/// <summary>
/// A position in document units: what everything below the viewport works in.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the drawing layer owes nothing to a UI framework. It used to be
/// <c>Avalonia.Point</c>, which made the stroke model, the smoothing filter, the curve fitter and
/// every brush engine depend on a windowing toolkit to say where a mark goes. Nothing about placing
/// ink needs one, and the dependency would have had to come out before any of this could be used
/// from a test host, a command-line renderer, or a second front end.
/// </para>
/// <para>
/// <b>Doubles, not floats.</b> <c>SKPoint</c> was the obvious alternative and is the wrong one: a
/// position arrives from the viewport's mapping already computed in double, and then goes through
/// the filter, the fitter and the spacing walk before it becomes a pixel. Narrowing at the front of
/// that narrows before the arithmetic rather than after it. Skia takes floats, so the conversion
/// happens where the mark is actually drawn -- which is once, at the end.
/// </para>
/// <para>
/// Used both as a position and as an offset between two positions, as <c>Avalonia.Point</c> was.
/// Separating those into a point and a vector type is a real distinction, and one nothing here has
/// needed: the fitter's tangents are the only offsets in the codebase, and keeping them as points
/// costs it nothing.
/// </para>
/// </remarks>
public readonly record struct DocumentPoint(double X, double Y)
{
    /// <summary>The document's top-left corner.</summary>
    public static readonly DocumentPoint Origin = default;

    public static DocumentPoint operator +(DocumentPoint a, DocumentPoint b) =>
        new(a.X + b.X, a.Y + b.Y);

    public static DocumentPoint operator -(DocumentPoint a, DocumentPoint b) =>
        new(a.X - b.X, a.Y - b.Y);

    public static DocumentPoint operator *(DocumentPoint p, double scale) =>
        new(p.X * scale, p.Y * scale);

    public static DocumentPoint operator /(DocumentPoint p, double divisor) =>
        new(p.X / divisor, p.Y / divisor);

    /// <summary>Distance from the origin, which for an offset is its magnitude.</summary>
    public double Length => Math.Sqrt(X * X + Y * Y);

    /// <summary>True when this offset has no direction, so nothing can be built from it.</summary>
    public bool IsZero => X == 0 && Y == 0;

    /// <summary>False when either coordinate has stopped being a number.</summary>
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    /// <summary>How far apart two positions are.</summary>
    public double DistanceTo(DocumentPoint other) => (other - this).Length;

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}
