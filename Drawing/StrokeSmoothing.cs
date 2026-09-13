namespace PenDynamicsPaint.Drawing;

/// <summary>
/// How much the incoming path is filtered before any mark is placed.
/// </summary>
/// <remarks>
/// <para>
/// Not a brush setting, and that is a decision rather than an accident of where it landed.
/// Smoothing is about the hand rather than the mark: it changes where the pen is taken to have
/// been, and every brush drawing that path gets the same benefit. Krita puts it on the tool for
/// the same reason. <b>libmypaint disagrees</b> -- its slow tracking is a brush property -- so this
/// may yet move; what settles it is whether someone wants a stabilised inking brush and an unfiltered
/// sketching brush in the same session, which is a question about using it rather than about the code.
/// </para>
/// <para>
/// A stroke records the settings that made it, the same way it records its brush, so an undo
/// replays it with the filtering it was drawn under rather than with whatever is set now.
/// </para>
/// </remarks>
public readonly record struct StrokeSmoothing
{
    /// <summary>Krita's default smoothness distance, verified against <c>KDE/krita</c> at <c>75315b18</c>.</summary>
    public const double DefaultDistance = 50.0;

    /// <summary>Krita's default tail aggressiveness, from the same source.</summary>
    public const double DefaultTail = 0.15;

    /// <summary>The pen reaches the brush unfiltered.</summary>
    public static readonly StrokeSmoothing None = new() { Distance = 0 };

    /// <summary>What Krita opens with.</summary>
    public static readonly StrokeSmoothing Default = new();

    private readonly double _distance = DefaultDistance;
    private readonly double _tail = DefaultTail;

    public StrokeSmoothing() { }

    /// <summary>
    /// How far back along the path the filter reaches, in document units. Zero turns it off.
    /// </summary>
    /// <remarks>
    /// <b>A distance, not a number of samples.</b> That is the whole reason this filter is worth
    /// porting rather than reaching for a moving average: a window measured in samples smooths
    /// twice as hard when the tablet reports twice as often, so the same gesture drawn on two
    /// devices comes out differently. A window measured in distance does not -- see
    /// <see cref="PathSmoother"/>.
    /// </remarks>
    public double Distance
    {
        get => _distance;
        init => _distance = double.IsNaN(value) ? DefaultDistance : Math.Clamp(value, 0, 500);
    }

    /// <summary>
    /// How hard the filter lets go as the pen lifts. Zero holds on to the end of the stroke.
    /// </summary>
    /// <remarks>
    /// Where pressure is falling, the older samples are pushed further away and so weigh less,
    /// which lets the tail of a stroke follow the pen rather than being averaged into a stump.
    /// </remarks>
    public double TailAggressiveness
    {
        get => _tail;
        init => _tail = double.IsNaN(value) ? DefaultTail : Math.Clamp(value, 0, 1);
    }

    /// <summary>Whether the pen's pressure is filtered along with its position.</summary>
    /// <remarks>
    /// Off by default, as in Krita. Filtering pressure with the same weights smooths out the
    /// width of a stroke as well as its path, which is sometimes wanted and is a separate thing
    /// from wanting a steady line.
    /// </remarks>
    public bool SmoothPressure { get; init; }

    /// <summary>False when this leaves the path alone, in which case no filter is run at all.</summary>
    public bool IsEnabled => _distance > 0;
}
