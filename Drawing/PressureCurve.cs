namespace PenDynamicsPaint.Drawing;

/// <summary>
/// How a brush reads the pen: the range of pressure it responds to, and how hard it responds
/// across that range.
/// </summary>
/// <remarks>
/// <para>
/// <b>A curve belongs to a brush, not to the application.</b> That is the whole reason this type
/// exists here rather than being inherited from PenDynamicsLab's global pressure pipeline. In the
/// Lab there is one curve because the question is what the pipeline does to the pen's signal; in a
/// paint application a soft round brush and a hard inking pen want different answers from the same
/// hardware, and a single setting cannot give both.
/// </para>
/// <para>
/// Three numbers rather than a spline. <see cref="Start"/> and <see cref="End"/> pick the part of
/// the pen's range the brush uses, and <see cref="Exponent"/> shapes the response across it:
/// </para>
/// <code>
/// t = clamp((pressure - Start) / (End - Start), 0, 1)
/// output = pow(t, Exponent)
/// </code>
/// <para>
/// That is a genuine subset of what Krita and MyPaint offer, and it is the subset people actually
/// reach for. <see cref="Start"/> removes the dead weight at the bottom of a tablet's range, which
/// is what makes a light touch mark at all on hardware that reports 200 counts before anything
/// happens. <see cref="End"/> lets the brush reach full strength without having to bottom the nib
/// out. <see cref="Exponent"/> above 1 holds the brush light until you lean on it; below 1 it comes
/// on early and saturates.
/// </para>
/// <para>
/// An arbitrary spline is the general case and is not here. It is a bigger piece of UI than it is
/// of arithmetic, and it belongs with the rest of the dynamics work -- libmypaint maps nine inputs
/// this way, not one.
/// </para>
/// <para>
/// <b>A stroke keeps the curve it was drawn with</b>, because it keeps the whole brush. Editing a
/// brush therefore changes what you draw next rather than rewriting what is already on the canvas,
/// which is the opposite of the Lab's model and correct for a painting application: strokes are
/// work, not a preview of the current settings.
/// </para>
/// </remarks>
public readonly record struct PressureCurve
{
    /// <summary>The pen reaches the brush unchanged.</summary>
    public static readonly PressureCurve Linear = new();

    private readonly double _start = 0;
    private readonly double _end = 1;
    private readonly double _exponent = 1;

    public PressureCurve() { }

    public PressureCurve(double start, double end, double exponent)
    {
        Start = start;
        End = end;
        Exponent = exponent;
    }

    /// <summary>Pressure at or below this produces nothing. Clamped to [0, 1].</summary>
    public double Start
    {
        get => _start;
        init => _start = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
    }

    /// <summary>Pressure at or above this produces full output. Clamped to [0, 1].</summary>
    public double End
    {
        get => _end;
        init => _end = double.IsNaN(value) ? 1 : Math.Clamp(value, 0, 1);
    }

    /// <summary>
    /// Shapes the response between <see cref="Start"/> and <see cref="End"/>. 1 is a straight line.
    /// </summary>
    /// <remarks>
    /// Clamped to [0.1, 8]. Zero would make every contact full strength and a negative one would
    /// send a light touch to infinity, neither of which is a brush.
    /// </remarks>
    public double Exponent
    {
        get => _exponent;
        init => _exponent = double.IsNaN(value) ? 1 : Math.Clamp(value, 0.1, 8);
    }

    /// <summary>True when this curve leaves the pen's reading alone.</summary>
    public bool IsLinear => _start == 0 && _end == 1 && _exponent == 1;

    /// <summary>
    /// What the brush makes of one pressure reading. In and out are both 0 to 1.
    /// </summary>
    /// <remarks>
    /// A range of zero width -- <see cref="Start"/> at or past <see cref="End"/> -- is treated as a
    /// threshold: nothing below it, full strength at or above. That is the limit the arithmetic
    /// approaches rather than a special case bolted on, and it is a usable brush in its own right.
    /// </remarks>
    public double Apply(double pressure)
    {
        if (double.IsNaN(pressure)) return 0;
        pressure = Math.Clamp(pressure, 0, 1);

        double span = _end - _start;
        if (span <= 0) return pressure >= _end ? 1 : 0;

        double t = Math.Clamp((pressure - _start) / span, 0, 1);
        return _exponent == 1 ? t : Math.Pow(t, _exponent);
    }
}
