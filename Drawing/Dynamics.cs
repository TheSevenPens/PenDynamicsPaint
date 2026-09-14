namespace PenDynamicsPaint.Drawing;

/// <summary>
/// One pen reading mapped onto one property of the mark.
/// </summary>
/// <remarks>
/// <para>
/// Three parts, and each is a separate question. <see cref="Enabled"/> is whether this reading has
/// any say at all. <see cref="Curve"/> shapes it: what the pen reports against what comes out.
/// <see cref="Minimum"/> is the floor, the share of the property left when the reading is at its
/// weakest -- a brush whose width drops to nothing at a light touch and one that thins to half are
/// different brushes, and no curve that ends at zero can express the second.
/// </para>
/// <para>
/// The output is always 0 to 1 and is a <b>scale</b>, not a value. What it scales is the property's
/// own setting, so the size slider stays the size of the brush and this decides how much of it the
/// pen is asking for. A disabled input scales by 1, which is how a brush with no dynamics at all
/// draws at the size it says.
/// </para>
/// </remarks>
public sealed record DynamicInput
{
    /// <summary>No say. Scales by 1, so the property is whatever its own setting says.</summary>
    public static readonly DynamicInput Off = new();

    private readonly double _minimum;

    /// <summary>Whether this reading drives the property at all.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The share of the property left at the weakest reading. Clamped to [0, 1].
    /// </summary>
    /// <remarks>
    /// Zero is the usual answer for pressure driving size, and it is what makes a stroke taper to
    /// nothing at each end. Raised, the brush keeps that much width however lightly it is used,
    /// which is what a marker or a technical pen does.
    /// </remarks>
    public double Minimum
    {
        get => _minimum;
        init => _minimum = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
    }

    /// <summary>How the reading is shaped on its way to the property.</summary>
    public PressureCurve Curve { get; init; } = PressureCurve.Linear;

    /// <summary>What this input asks of the property, as a scale from 0 to 1.</summary>
    public double Apply(double reading) =>
        Enabled ? _minimum + (1 - _minimum) * Curve.Apply(reading) : 1;
}

/// <summary>
/// Everything the pen says about one property of the mark.
/// </summary>
/// <remarks>
/// <para>
/// One of these per property rather than one per brush. A brush whose width follows pressure and
/// whose opacity does not is the ordinary case, and the two want different curves as soon as both
/// are driven -- the single shared curve this replaced could only ever give them the same shape.
/// </para>
/// <para>
/// <b>Inputs multiply.</b> Each returns a scale from 0 to 1 and the property gets the product, so
/// the most restrictive has the last word: a pen held light and leaned over at once gives a
/// thinner mark than either alone. That is what Clip Studio does, and it is the rule that lets a
/// second input be switched on without having to retune the first. Taking the smallest instead
/// would leave whichever input is not currently winning doing nothing at all.
/// </para>
/// <para>
/// Only pressure is here so far. Tilt is computed and smoothed already, velocity wants a speed per
/// sample, and randomness wants a decision about whether it varies per stroke or per dab; each
/// arrives as another <see cref="DynamicInput"/> and another factor in <see cref="Scale"/>.
/// </para>
/// </remarks>
public sealed record Dynamics
{
    /// <summary>Nothing drives the property. It is whatever its own setting says.</summary>
    public static readonly Dynamics None = new();

    /// <summary>Pressure alone, across the whole range, unshaped.</summary>
    public static readonly Dynamics FromPressure = new()
    {
        Pressure = new DynamicInput { Enabled = true },
    };

    /// <summary>How hard the pen is pressed.</summary>
    public DynamicInput Pressure { get; init; } = DynamicInput.Off;

    /// <summary>How many readings drive this property, for the button that says so.</summary>
    public int Count => Pressure.Enabled ? 1 : 0;

    /// <summary>What the pen is asking of the property, as a scale from 0 to 1.</summary>
    public double Scale(double pressure) => Pressure.Apply(pressure);
}
