
namespace PenDynamicsPaint.Drawing;

/// <summary>
/// Filters the incoming pen signal, weighting recent samples by how far back along the stroke they
/// lie.
/// </summary>
/// <remarks>
/// <para>
/// Krita's weighted smoothing, ported from <c>KDE/krita</c> at <c>75315b18</c>
/// (<c>libs/ui/tool/kis_tool_freehand_helper.cpp</c>). Each new value is replaced by a weighted
/// mean of the recent path, with the weight of a sample falling off as a Gaussian in the
/// <b>distance travelled</b> back to it:
/// </para>
/// <code>
/// sigma  = reach / 3
/// weight = (1 / (sqrt(2*pi) * sigma)) * exp(-d^2 / (2 * sigma^2))
/// </code>
/// <para>
/// where <c>d</c> is the path length from the newest sample back to the one being weighted. The
/// <c>/3</c> is Krita's: the setting names three standard deviations, the range holding over 90% of
/// the weight.
/// </para>
/// <para>
/// <b>The property this is worth having for is rate independence.</b> The window is measured in
/// document units, so a tablet reporting at 200 Hz and one reporting at 100 Hz filter the same
/// gesture the same amount. A moving average over the last N samples does not: it smooths twice as
/// hard on the faster device, and a stroke drawn slowly comes out smoother than the same stroke
/// drawn quickly. This is the fault the dab engine's spacing rule avoids, in a different place --
/// see <see cref="DabSpacing"/>.
/// </para>
/// <para>
/// <b>Position and pressure are walked separately</b>, because they have their own reaches. Krita
/// runs one walk and takes both from it, which it can do because one setting covers both. Two
/// reaches mean two Gaussians and two self-truncating windows, and sharing a walk between them
/// would mean one channel's cutoff deciding where the other stopped.
/// </para>
/// <para>
/// The filter is recursive: what goes into the history is the <b>filtered</b> sample, so each
/// output is a mean over previous outputs. Krita does this too, and it is what makes the filter
/// settle at a steady lag rather than merely blur.
/// </para>
/// <para>
/// <b>The stroke ends a little short of where the pen lifted.</b> The filter lags, and nothing runs
/// it out to the final raw position. That is Krita's behaviour and is kept deliberately: running it
/// out would put an unfiltered hook on the end of every stroke, which is more visible than the
/// shortfall it fixes.
/// </para>
/// </remarks>
public sealed class PathSmoother
{
    /// <summary>Krita smooths nothing until it has more than this many samples.</summary>
    /// <remarks>
    /// So the first few marks of a stroke are unfiltered. With too little path behind it the mean
    /// is dominated by wherever the pen happened to land, which drags the start of the stroke
    /// somewhere the pen never was.
    /// </remarks>
    public const int MinimumHistory = 3;

    /// <summary>
    /// How far the weight may fall below the newest sample's before the walk stops.
    /// </summary>
    /// <remarks>
    /// Krita's <c>baseRate / rate > 100</c>. The window truncates itself, so the cost per sample
    /// follows the reach rather than the length of the stroke.
    /// </remarks>
    private const double WeightCutoff = 100.0;

    private readonly List<StrokeSample> _history = [];

    /// <summary>Path length from each entry back to the one before it.</summary>
    /// <remarks>
    /// Measured from the previous <b>filtered</b> position to the new raw one, as Krita does. The
    /// first entry's distance is zero because there is nothing before it.
    /// </remarks>
    private readonly List<double> _distances = [];

    /// <summary>Samples filtered so far in this stroke.</summary>
    public int Count => _history.Count;

    /// <summary>Begin a new stroke. Nothing is carried over.</summary>
    /// <remarks>
    /// Without this the first marks of a stroke would be averaged with the end of the previous
    /// one, pulling them toward wherever the last stroke finished.
    /// </remarks>
    public void Reset()
    {
        _history.Clear();
        _distances.Clear();
    }

    /// <summary>
    /// The filtered form of one sample: a position and a pressure, each filtered or not.
    /// </summary>
    /// <remarks>
    /// Carries the raw pressure rather than the brush's reading of it, because filtering belongs
    /// before the curve -- it is the pen's signal being steadied, not the brush's response. The
    /// caller runs the result through the brush afterwards.
    /// </remarks>
    public readonly record struct Filtered(DocumentPoint Position, double RawPressure,
                                          PenOrientation Orientation);

    /// <summary>
    /// Filter one sample, returning what the stroke should be taken to have done.
    /// </summary>
    /// <param name="sample">The sample as the pen reported it.</param>
    /// <param name="smoothing">The brush's settings. A stroke pins its brush, so these are stable.</param>
    public Filtered Next(in StrokeSample sample, StrokeSmoothing smoothing)
    {
        if (!smoothing.IsEnabled)
            return new Filtered(sample.Position, sample.RawPressure, sample.Orientation);

        // From the previous filtered position to this raw one, which is the step the weighting
        // walks back over.
        double step = _history.Count == 0
            ? 0
            : Distance(_history[^1].Position, sample.Position);

        _distances.Add(step);
        _history.Add(sample);

        if (_history.Count <= MinimumHistory)
            return new Filtered(sample.Position, sample.RawPressure, sample.Orientation);

        var position = smoothing.SmoothsPosition
            ? WeightedPosition(smoothing.Position, smoothing.TailAggressiveness, sample.Position)
            : sample.Position;

        double pressure = smoothing.SmoothsPressure
            ? WeightedPressure(smoothing.Pressure, smoothing.TailAggressiveness, sample.RawPressure)
            : sample.RawPressure;

        var orientation = smoothing.SmoothsTilt
            ? WeightedOrientation(smoothing.Tilt, smoothing.TailAggressiveness, sample.Orientation)
            : sample.Orientation;

        // The filtered values go into the history, not the raw ones, so the next sample is
        // averaged over outputs. This is what makes the filter settle.
        _history[^1] = sample with
        {
            Position = position,
            RawPressure = pressure,
            Orientation = orientation,
        };

        return new Filtered(position, pressure, orientation);
    }

    private DocumentPoint WeightedPosition(double reach, double tail, DocumentPoint fallback)
    {
        double x = 0, y = 0, weightSum = 0;

        foreach (var (index, weight) in Walk(reach, tail))
        {
            weightSum += weight;
            x += weight * _history[index].Position.X;
            y += weight * _history[index].Position.Y;
        }

        // Krita additionally refuses a result where either coordinate came out zero. That guard is
        // not reproduced: it rejects a legitimate filtered position anywhere on the top or left
        // edge of the document, which here is inside the page rather than off-canvas. The
        // condition it was reaching for is this one.
        return weightSum > 0 ? new DocumentPoint(x / weightSum, y / weightSum) : fallback;
    }

    private double WeightedPressure(double reach, double tail, double fallback)
    {
        double pressure = 0, weightSum = 0;

        foreach (var (index, weight) in Walk(reach, tail))
        {
            weightSum += weight;
            pressure += weight * _history[index].RawPressure;
        }

        return weightSum > 0 ? pressure / weightSum : fallback;
    }

    /// <summary>
    /// The pen's orientation, steadied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Altitude and the two raw tilt axes are plain numbers and are averaged as such. The two
    /// <b>angles that wrap</b> -- azimuth and twist -- are not: averaging 359 and 1 gives 180,
    /// which points the opposite way. They are averaged as unit vectors and turned back into an
    /// angle, the same thing <c>BrushInputTracker</c> does to the direction of travel.
    /// </para>
    /// <para>
    /// Azimuth carries a second weight on top of that: the sine of how far the pen is leaning. Near
    /// vertical the azimuth is the pole of a spherical coordinate and says almost nothing -- a
    /// millimetre of wobble swings it through tens of degrees -- so those samples are allowed to
    /// count for almost nothing rather than being averaged in as though they meant something. A
    /// stroke drawn entirely upright leaves the resultant at nearly zero length, and then the raw
    /// reading is the honest answer.
    /// </para>
    /// </remarks>
    private PenOrientation WeightedOrientation(double reach, double tail, PenOrientation fallback)
    {
        double altitude = 0, tiltX = 0, tiltY = 0, weightSum = 0;
        double azimuthX = 0, azimuthY = 0;
        double twistX = 0, twistY = 0;

        foreach (var (index, weight) in Walk(reach, tail))
        {
            var at = _history[index].Orientation;

            weightSum += weight;
            altitude += weight * at.Altitude;
            tiltX += weight * at.TiltX;
            tiltY += weight * at.TiltY;

            // How much this sample's azimuth is worth: none at all with the pen upright, most with
            // it laid over.
            double lean = Math.Abs(Math.Sin((90.0 - at.Altitude) * Math.PI / 180.0));

            azimuthX += weight * lean * Math.Cos(at.Azimuth * Math.PI / 180.0);
            azimuthY += weight * lean * Math.Sin(at.Azimuth * Math.PI / 180.0);

            twistX += weight * Math.Cos(at.Twist * Math.PI / 180.0);
            twistY += weight * Math.Sin(at.Twist * Math.PI / 180.0);
        }

        if (weightSum <= 0) return fallback;

        return fallback with
        {
            Altitude = altitude / weightSum,
            TiltX = tiltX / weightSum,
            TiltY = tiltY / weightSum,
            Azimuth = Resultant(azimuthX, azimuthY, fallback.Azimuth),
            Twist = Resultant(twistX, twistY, fallback.Twist),
        };
    }

    /// <summary>The angle a summed vector points in, or the raw reading if it cancelled out.</summary>
    /// <remarks>
    /// A resultant of nearly zero length means the samples disagreed in every direction, which is
    /// what a genuinely random angle looks like. Its direction is then noise, and reporting the
    /// pen's own reading is better than reporting the direction that noise happened to land on.
    /// </remarks>
    private static double Resultant(double x, double y, double fallback)
    {
        if (x * x + y * y < 1e-9) return fallback;

        double degrees = Math.Atan2(y, x) * 180.0 / Math.PI;
        return degrees < 0 ? degrees + 360.0 : degrees;
    }

    /// <summary>
    /// Walk back through the history, newest first, yielding each sample's weight until the
    /// weights become negligible.
    /// </summary>
    /// <param name="reach">How far back to look, in document units. Three sigma.</param>
    /// <param name="tail">
    /// How hard to let go where pressure is falling. Samples before a drop are pushed further
    /// away, so the end of a stroke follows the pen instead of being averaged into a stump.
    /// </param>
    private IEnumerable<(int Index, double Weight)> Walk(double reach, double tail)
    {
        double sigma = reach / 3.0;               // the setting names 3 sigma
        double peak = 1.0 / (Math.Sqrt(2 * Math.PI) * sigma);
        double twoSigmaSquared = 2 * sigma * sigma;

        double travelled = 0, newestWeight = 0;

        for (int i = _history.Count - 1; i >= 0; i--)
        {
            double distance = _distances[i];

            if (i < _history.Count - 1)
            {
                double falling = _history[i].RawPressure - _history[i + 1].RawPressure;
                if (falling > 0)
                {
                    falling *= 40.0 * tail * (1.0 - _history[i].RawPressure);
                    distance += falling * 3.0 * sigma;
                }
            }

            travelled += distance;
            double weight = peak * Math.Exp(-travelled * travelled / twoSigmaSquared);

            if (i == _history.Count - 1) newestWeight = weight;
            else if (weight <= 0 || newestWeight / weight > WeightCutoff) yield break;

            yield return (i, weight);
        }
    }

    private static double Distance(DocumentPoint a, DocumentPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
