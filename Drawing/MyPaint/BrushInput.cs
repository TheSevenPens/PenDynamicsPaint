namespace PenDynamicsPaint.Drawing.MyPaint;

/// <summary>
/// The things a MyPaint brush can respond to, beyond pressure.
/// </summary>
/// <remarks>
/// <para>
/// The identifiers are libmypaint's own, from <c>brushsettings.json</c> at <c>v1.6.1</c>, because
/// they are what a <c>.myb</c> file names. A brush file that mentions an input missing here is
/// loaded with that curve dropped rather than rejected -- see <see cref="MyPaintBrush"/>.
/// </para>
/// <para>
/// <b>This is the subset the application can actually supply.</b> libmypaint defines eighteen;
/// the rest need a canvas rotation, a grid map or a zoom-aware brush, none of which exist here.
/// Listing only what can be computed keeps the gap visible: an input in this enum is one a stroke
/// can really drive.
/// </para>
/// </remarks>
public enum BrushInput
{
    /// <summary>What the pen reported, after the brush's own pressure gain.</summary>
    Pressure,

    /// <summary>Speed, smoothed slowly. Logarithmic, so it is finite at a standstill.</summary>
    Speed1,

    /// <summary>Speed, smoothed on a second timescale, so a brush can respond to both.</summary>
    Speed2,

    /// <summary>A fresh random value per dab, in 0..1.</summary>
    Random,

    /// <summary>How far into the stroke the pen is, 0 at the start and rising to 1.</summary>
    Stroke,

    /// <summary>Direction of travel in degrees, folded to 0..180 so it has no front or back.</summary>
    Direction,

    /// <summary>How far the pen is tilted from vertical, 0 to 90 degrees.</summary>
    TiltDeclination,

    /// <summary>Which way the tilt points, -180 to 180 degrees.</summary>
    TiltAscension,

    /// <summary>Barrel rotation, for a pen that reports twist.</summary>
    BarrelRotation,

    /// <summary>
    /// Whatever the brush's own <c>custom_input</c> setting works out to, lagged.
    /// </summary>
    /// <remarks>
    /// The odd one out: the others are read off the pen, and this one is read off the brush. The
    /// setting that feeds it can itself be driven by any of the others, so a brush uses it to
    /// build a quantity the input list does not offer -- a slowed-down pressure, or two inputs
    /// mixed -- and then drive several settings from that one quantity. The airbrush shrinks its
    /// radius from a pressure slowed this way.
    /// </remarks>
    Custom,
}

/// <summary>
/// One sample's worth of input values, as a brush sees them.
/// </summary>
/// <remarks>
/// A fixed-size set rather than a dictionary: a dab evaluates every setting, each setting reads
/// several inputs, and a stroke places thousands of dabs. Looking each one up by name would be the
/// hot path of the whole engine.
/// </remarks>
public readonly struct BrushInputs
{
    /// <summary>How many inputs there are, for anything that needs to size an array.</summary>
    public const int Count = (int)BrushInput.Custom + 1;

    private readonly float[] _values;

    public BrushInputs(float[] values) => _values = values;

    public float this[BrushInput input] => _values[(int)input];

    /// <summary>Fill an array with these values, for a tracker starting from rest.</summary>
    public void CopyTo(float[] destination) => _values.CopyTo(destination, 0);

    /// <summary>What libmypaint calls the <c>normal</c> value: where a brush sits at rest.</summary>
    /// <remarks>
    /// Used to evaluate a setting without a stroke, which is what a brush preview needs and what
    /// makes a setting's base value meaningful on its own.
    /// </remarks>
    public static BrushInputs Neutral { get; } = new(
    [
        0.4f,   // pressure
        0.5f,   // speed1
        0.5f,   // speed2
        0.5f,   // random
        0.5f,   // stroke
        0.0f,   // direction
        0.0f,   // tilt declination
        0.0f,   // tilt ascension
        0.0f,   // barrel rotation
        0.0f,   // custom
    ]);
}
