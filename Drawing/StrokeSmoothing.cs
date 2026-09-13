namespace PenDynamicsPaint.Drawing;

/// <summary>
/// How much a brush filters the incoming pen signal before any mark is placed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Position, pressure and tilt are filtered independently</b>, each with its own reach. They
/// are different problems: a shaky hand wants its path steadied while its pressure is left alone, a
/// pen with a noisy sensor wants the opposite, and a brush whose width follows tilt wants tilt
/// steadied whether or not the line needed help. Krita couples the first two -- one distance, and a
/// switch that turns pressure filtering on with the same reach -- which cannot express any of it.
/// </para>
/// <para>
/// <b>Part of the brush.</b> This sat on the application at first, on the reasoning that smoothing
/// is about the hand rather than the mark and so applies to whatever you are drawing with -- which
/// is Krita's model, where it is a tool option. That is wrong for the way brushes are actually
/// used: a stabilised inking brush and an unfiltered sketching brush want different answers in the
/// same session, and the setting has to come with the brush or you are changing it by hand every
/// time you switch. libmypaint treats slow tracking as a brush property for this reason.
/// </para>
/// <para>
/// So a stroke records it by recording its brush, and nothing separate is needed.
/// </para>
/// </remarks>
public readonly record struct StrokeSmoothing
{
    /// <summary>Krita's default smoothness distance, verified against <c>KDE/krita</c> at <c>75315b18</c>.</summary>
    public const double DefaultDistance = 50.0;

    /// <summary>Krita's default tail aggressiveness, from the same source.</summary>
    public const double DefaultTail = 0.15;

    /// <summary>The largest reach either filter will accept, in document units.</summary>
    public const double MaxDistance = 500;

    /// <summary>The pen reaches the brush unfiltered.</summary>
    public static readonly StrokeSmoothing None = new();

    private readonly double _position = 0;
    private readonly double _pressure = 0;
    private readonly double _tilt = 0;
    private readonly double _tail = DefaultTail;

    public StrokeSmoothing() { }

    /// <summary>
    /// How far back along the path the position filter reaches, in document units. Zero is off.
    /// </summary>
    /// <remarks>
    /// <b>A distance, not a number of samples.</b> That is the reason this filter is a port rather
    /// than a moving average: a window measured in samples smooths twice as hard when the tablet
    /// reports twice as often, so the same gesture comes out differently on two devices and a slow
    /// stroke comes out smoother than a fast one. See <see cref="PathSmoother"/>.
    /// </remarks>
    public double Position
    {
        get => _position;
        init => _position = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, MaxDistance);
    }

    /// <summary>
    /// How far back the pressure filter reaches, in document units. Zero is off.
    /// </summary>
    /// <remarks>
    /// Also a distance, and weighted the same way, so it steadies the width of a stroke without
    /// caring how fast it was drawn. Independent of <see cref="Position"/>: filtering a wobbling
    /// path says nothing about whether the pressure behind it needed help.
    /// </remarks>
    public double Pressure
    {
        get => _pressure;
        init => _pressure = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, MaxDistance);
    }

    /// <summary>
    /// How far back the tilt filter reaches, in document units. Zero is off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own reach for the same reason pressure has one: a brush driven by tilt wants it steadied
    /// whether or not the path did. Until tilt reached the mark there was nothing for this to fix
    /// and the orientation was simply passed through, which is where the wobble was coming from.
    /// </para>
    /// <para>
    /// <b>Tilt arrives dirtier than position does.</b> Tablets quantise it coarsely -- often to
    /// whole degrees -- so a brush mapping it onto radius turns each step into a visible jump, and
    /// the azimuth is worst of all near vertical, where it is the pole of a spherical coordinate
    /// and a millimetre of wobble swings it through tens of degrees. See <see cref="PathSmoother"/>
    /// for what is done about that.
    /// </para>
    /// <para>
    /// <b>Not a port.</b> libmypaint does not filter tilt at all and Krita's smoothing does not
    /// reach it, so there is no upstream behaviour to match here -- only the same weighting the
    /// other two use, applied to a signal that needs it more.
    /// </para>
    /// </remarks>
    public double Tilt
    {
        get => _tilt;
        init => _tilt = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, MaxDistance);
    }

    /// <summary>
    /// How hard the filter lets go as the pen lifts. Zero holds on to the end of the stroke.
    /// </summary>
    /// <remarks>
    /// Where pressure is falling, the older samples are pushed further away and so weigh less,
    /// which lets the tail of a stroke follow the pen rather than being averaged into a stump.
    /// Applies to whichever filters are running.
    /// </remarks>
    public double TailAggressiveness
    {
        get => _tail;
        init => _tail = double.IsNaN(value) ? DefaultTail : Math.Clamp(value, 0, 1);
    }

    /// <summary>True when the path is filtered.</summary>
    public bool SmoothsPosition => _position > 0;

    /// <summary>True when the pressure is filtered.</summary>
    public bool SmoothsPressure => _pressure > 0;

    /// <summary>True when the pen's orientation is filtered.</summary>
    public bool SmoothsTilt => _tilt > 0;

    /// <summary>False when this leaves the pen alone, in which case no filter is run at all.</summary>
    public bool IsEnabled => SmoothsPosition || SmoothsPressure || SmoothsTilt;
}
