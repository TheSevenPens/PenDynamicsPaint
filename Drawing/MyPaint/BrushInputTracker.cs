namespace PenDynamicsPaint.Drawing.MyPaint;

/// <summary>
/// Turns a stroke into the values a brush responds to, one dab at a time.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the input block of <c>update_states_and_setting_values</c> in
/// <c>mypaint/libmypaint</c> at <c>v1.6.1</c> (<c>mypaint-brush.c</c>).
/// </para>
/// <para>
/// Most of the inputs are not readings but <b>state</b>: speed is smoothed over two timescales,
/// direction is smoothed so a jittery hand does not spin it, and the stroke position accumulates
/// travel. That is why this is an object with a life rather than a function, and why it has to be
/// reset between strokes -- carrying a speed or a direction into the next stroke would start it
/// behaving as though the pen were already moving.
/// </para>
/// <para>
/// <b>Time is the weak point.</b> Speed is distance per second, and the pen's own clock is what
/// makes that meaningful; not every backend reports one, and a sample carries zero when none was
/// given. Where a usable interval is missing this falls back to a nominal one, which keeps speed
/// finite and plausible while making it a function of the report rate rather than of the hand --
/// exactly the dependence the rest of this application works to avoid. It is recorded in
/// <see cref="UsedNominalTime"/> so the fallback can be seen rather than assumed.
/// </para>
/// </remarks>
public sealed class BrushInputTracker
{
    /// <summary>Assumed interval when the pen supplies no clock, in seconds.</summary>
    /// <remarks>
    /// About a 100 Hz tablet. Chosen because it is in the middle of what the hardware next door
    /// actually reports, so a brush tuned with a clock does not change character without one.
    /// </remarks>
    public const double NominalInterval = 0.01;

    /// <summary>libmypaint's two fixed points for the speed curve, from <c>mypaint-brush.c</c>.</summary>
    private const double SpeedFixX = 45.0, SpeedFixY = 0.5, SpeedFixSlope = 0.015;

    private readonly Random _random;
    private readonly float[] _values = new float[BrushInputs.Count];

    private bool _started;
    private double _slowSpeed1, _slowSpeed2;
    private double _directionX, _directionY;
    private double _stroke;

    public BrushInputTracker(int? seed = null)
    {
        _random = seed is { } s ? new Random(s) : new Random();
        Reset();
    }

    /// <summary>True when the pen gave no usable clock and a nominal interval stood in.</summary>
    public bool UsedNominalTime { get; private set; }

    /// <summary>Begin a stroke. Speed, direction and stroke position all start again.</summary>
    public void Reset()
    {
        _started = false;
        _slowSpeed1 = _slowSpeed2 = 0;
        _directionX = _directionY = 0;
        _stroke = 0;
        UsedNominalTime = false;

        // From rest rather than from zero. A spacing query can arrive before the first dab, and
        // zero is a real value for several inputs -- a brush reading speed at zero would be told
        // the pen was stationary rather than that nothing had happened yet.
        BrushInputs.Neutral.CopyTo(_values);
    }

    /// <summary>
    /// The inputs as they stand, with this sample's pressure, without advancing anything.
    /// </summary>
    /// <remarks>
    /// For deciding where the next dab goes. The spacing rule asks how big a gap it wants before
    /// it knows whether there is room for a dab at all, and advancing here would consume a random
    /// value and a slice of the stroke for a dab that may never be placed.
    /// </remarks>
    public BrushInputs Peek(in StrokeSample sample)
    {
        _values[(int)BrushInput.Pressure] = (float)sample.ProcessedPressure;
        return new BrushInputs(_values);
    }

    /// <summary>
    /// Advance to one dab and read off the inputs a brush should see there.
    /// </summary>
    /// <param name="at">Where the dab is, in document units.</param>
    /// <param name="previous">Where the last one was.</param>
    /// <param name="sample">The pen reading this dab was interpolated from.</param>
    /// <param name="seconds">Time since the last dab, or zero if the pen gave no clock.</param>
    /// <param name="brush">Read for the settings that decide how the inputs themselves behave.</param>
    /// <param name="baseRadius">The brush's radius before any input bends it, in document units.</param>
    public BrushInputs Next(DocumentPoint at, DocumentPoint previous, in StrokeSample sample,
                            double seconds, MyPaintBrush brush, double baseRadius)
    {
        if (seconds <= 0)
        {
            seconds = NominalInterval;
            UsedNominalTime = true;
        }

        double dx = at.X - previous.X, dy = at.Y - previous.Y;
        double travelled = Math.Sqrt(dx * dx + dy * dy);

        // Distance per second for speed, and distance in radii for the stroke position: one is
        // about the hand, the other about how much brush has been laid down.
        double speed = travelled / seconds;
        double inRadii = baseRadius > 0 ? travelled / baseRadius : 0;

        if (!_started)
        {
            // The first dab has no previous position, so every rate would be nonsense. Start the
            // smoothed values where they are rather than letting a spurious first reading in.
            _started = true;
            _slowSpeed1 = _slowSpeed2 = speed;
            _directionX = dx;
            _directionY = dy;
        }
        else
        {
            _slowSpeed1 += (speed - _slowSpeed1) * Decay(Base(brush, MyPaintSetting.Speed1Slowness), seconds);
            _slowSpeed2 += (speed - _slowSpeed2) * Decay(Base(brush, MyPaintSetting.Speed2Slowness), seconds);

            // Smoothed as a vector, so a reversal averages through zero instead of jumping 180
            // degrees, and so a stationary pen keeps pointing where it last went.
            double fac = Decay(0.1, seconds);
            _directionX += (dx - _directionX) * fac;
            _directionY += (dy - _directionY) * fac;
        }

        _values[(int)BrushInput.Pressure] =
            (float)(sample.ProcessedPressure * Math.Exp(Base(brush, MyPaintSetting.PressureGainLog)));

        _values[(int)BrushInput.Speed1] = (float)SpeedInput(_slowSpeed1, Base(brush, MyPaintSetting.Speed1Gamma));
        _values[(int)BrushInput.Speed2] = (float)SpeedInput(_slowSpeed2, Base(brush, MyPaintSetting.Speed2Gamma));

        _values[(int)BrushInput.Random] = (float)_random.NextDouble();

        _values[(int)BrushInput.Stroke] = (float)AdvanceStroke(inRadii, brush);

        // Folded to half a turn: a brush that responds to direction cares which way the stroke
        // lies, not which end of it the pen started from.
        double heading = Math.Atan2(_directionY, _directionX) * 180 / Math.PI;
        _values[(int)BrushInput.Direction] = (float)Mod(heading + 180.0, 180.0);

        var orientation = sample.Orientation;
        _values[(int)BrushInput.TiltDeclination] = (float)(90.0 - orientation.Altitude);
        _values[(int)BrushInput.TiltAscension] = (float)(Mod(orientation.Azimuth + 180.0, 360.0) - 180.0);
        _values[(int)BrushInput.BarrelRotation] = (float)Mod(orientation.Twist, 360.0);

        return new BrushInputs(_values);
    }

    /// <summary>
    /// How far through the stroke the pen is, rising with travel and wrapping at the top.
    /// </summary>
    /// <remarks>
    /// Wrapping rather than stopping is libmypaint's behaviour and is what lets the input drive a
    /// repeating pattern along a long stroke, rather than only the first part of one.
    /// </remarks>
    private double AdvanceStroke(double travelledInRadii, MyPaintBrush brush)
    {
        double frequency = Math.Exp(-Base(brush, MyPaintSetting.StrokeDurationLogarithmic));
        _stroke = Math.Max(0, _stroke + travelledInRadii * frequency);

        double wrap = 1.0 + Math.Max(0, Base(brush, MyPaintSetting.StrokeHoldtime));
        if (_stroke > wrap) _stroke = 0;

        return Math.Min(_stroke, 1.0);
    }

    /// <summary>
    /// Speed as a brush sees it: logarithmic, so it is finite at a standstill and still moves at
    /// the top end.
    /// </summary>
    /// <remarks>
    /// The scale and offset come from libmypaint's two fixed points -- the curve passes through
    /// 0.5 at 45 units per second and has a slope of 0.015 there -- which is what makes the same
    /// brush file behave the same way about "fast" as it does in MyPaint.
    /// </remarks>
    private static double SpeedInput(double speed, double gammaLog)
    {
        double gamma = Math.Exp(gammaLog);
        double m = SpeedFixSlope * (SpeedFixX + gamma);
        double q = SpeedFixY - m * Math.Log(SpeedFixX + gamma);

        return Math.Log(gamma + Math.Max(0, speed)) * m + q;
    }

    /// <summary>How much of the way to a new value to move, given a timescale and an interval.</summary>
    private static double Decay(double slowness, double seconds) =>
        slowness <= 0 ? 1.0 : 1.0 - Math.Exp(-seconds / slowness);

    private static double Base(MyPaintBrush brush, MyPaintSetting setting) =>
        brush[setting].BaseValue;

    /// <summary>Always-positive remainder, as libmypaint's <c>mod_arith</c> gives.</summary>
    private static double Mod(double value, double modulus)
    {
        double r = value % modulus;
        return r < 0 ? r + modulus : r;
    }
}
