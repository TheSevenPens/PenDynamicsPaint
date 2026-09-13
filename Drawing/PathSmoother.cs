using Avalonia;

namespace PenDynamicsPaint.Drawing;

/// <summary>
/// Filters the incoming path, weighting recent samples by how far back along the stroke they lie.
/// </summary>
/// <remarks>
/// <para>
/// Krita's weighted smoothing, ported from <c>KDE/krita</c> at <c>75315b18</c>
/// (<c>libs/ui/tool/kis_tool_freehand_helper.cpp</c>). Each new position is replaced by a weighted
/// mean of the recent path, with the weight of a sample falling off as a Gaussian in the
/// <b>distance travelled</b> back to it:
/// </para>
/// <code>
/// sigma  = Distance / 3
/// weight = (1 / (sqrt(2*pi) * sigma)) * exp(-d^2 / (2 * sigma^2))
/// </code>
/// <para>
/// where <c>d</c> is the path length from the newest sample back to the one being weighted. The
/// <c>/3</c> is Krita's: the setting names three standard deviations, which is the range holding
/// over 90% of the weight.
/// </para>
/// <para>
/// <b>The property this is worth having for is rate independence.</b> The window is measured in
/// document units, so a tablet reporting at 200 Hz and one reporting at 100 Hz filter the same
/// gesture the same amount. A moving average over the last N samples does not: it smooths twice as
/// hard on the faster device, and a stroke drawn slowly comes out smoother than the same stroke
/// drawn quickly. This is the same fault the dab engine's spacing rule avoids, in a different
/// place -- see <see cref="DabSpacing"/>.
/// </para>
/// <para>
/// The filter is recursive: what goes into the history is the <b>smoothed</b> position, so each
/// output is a mean over previous outputs. Krita does this too, and it is what makes the filter
/// settle rather than merely blur.
/// </para>
/// <para>
/// <b>The stroke ends a little short of where the pen lifted.</b> The filter lags, and nothing runs
/// it out to the final raw position. That is Krita's behaviour and is kept deliberately: running
/// it out would put an unfiltered hook on the end of every stroke, which is more visible than the
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
    /// follows <see cref="StrokeSmoothing.Distance"/> rather than the length of the stroke.
    /// </remarks>
    private const double WeightCutoff = 100.0;

    private readonly List<StrokeSample> _history = [];

    /// <summary>Path length from each entry back to the one before it.</summary>
    /// <remarks>
    /// Measured from the previous <b>smoothed</b> position to the new raw one, as Krita does. The
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
    /// The filtered form of one sample: a smoothed position, and optionally a smoothed pressure.
    /// </summary>
    /// <remarks>
    /// Takes the raw pressure rather than the brush's reading of it, because filtering belongs
    /// before the curve -- it is the pen's signal being steadied, not the brush's response. The
    /// caller runs the result through the brush afterwards.
    /// </remarks>
    public readonly record struct Filtered(Point Position, double RawPressure);

    /// <summary>
    /// Filter one sample, returning where the stroke should be taken to have gone.
    /// </summary>
    /// <param name="sample">The sample as the pen reported it.</param>
    /// <param name="smoothing">Settings for this stroke. Read per sample, but a stroke pins them.</param>
    public Filtered Next(in StrokeSample sample, StrokeSmoothing smoothing)
    {
        if (!smoothing.IsEnabled) return new Filtered(sample.Position, sample.RawPressure);

        // From the previous smoothed position to this raw one, which is the step the weighting
        // walks back over.
        double step = _history.Count == 0
            ? 0
            : Distance(_history[^1].Position, sample.Position);

        _distances.Add(step);
        _history.Add(sample);

        if (_history.Count <= MinimumHistory)
            return new Filtered(sample.Position, sample.RawPressure);

        double sigma = smoothing.Distance / 3.0;          // the setting names 3 sigma
        double peak = 1.0 / (Math.Sqrt(2 * Math.PI) * sigma);
        double twoSigmaSquared = 2 * sigma * sigma;

        double travelled = 0, weightSum = 0, x = 0, y = 0, pressure = 0, newestWeight = 0;

        for (int i = _history.Count - 1; i >= 0; i--)
        {
            var older = _history[i];
            double distance = _distances[i];

            // Where pressure is falling toward the present, push this sample further away so it
            // weighs less. That is what lets the tail of a stroke follow the pen.
            if (i < _history.Count - 1)
            {
                double falling = older.RawPressure - _history[i + 1].RawPressure;
                if (falling > 0)
                {
                    falling *= 40.0 * smoothing.TailAggressiveness * (1.0 - older.RawPressure);
                    distance += falling * 3.0 * sigma;
                }
            }

            travelled += distance;
            double weight = peak * Math.Exp(-travelled * travelled / twoSigmaSquared);

            if (i == _history.Count - 1) newestWeight = weight;
            else if (weight <= 0 || newestWeight / weight > WeightCutoff) break;

            weightSum += weight;
            x += weight * older.Position.X;
            y += weight * older.Position.Y;
            if (smoothing.SmoothPressure) pressure += weight * older.RawPressure;
        }

        // Krita additionally refuses a result where either coordinate came out zero. That guard is
        // not reproduced: it rejects a legitimate smoothed position anywhere on the top or left
        // edge of the document, which here is inside the page rather than off-canvas. The
        // condition it was reaching for is this one.
        if (weightSum <= 0) return new Filtered(sample.Position, sample.RawPressure);

        var smoothed = new Point(x / weightSum, y / weightSum);
        double smoothedPressure = smoothing.SmoothPressure
            ? pressure / weightSum
            : sample.RawPressure;

        // The smoothed position goes into the history, not the raw one, so the next sample is
        // averaged over outputs. This is what makes the filter settle.
        _history[^1] = sample with { Position = smoothed };

        return new Filtered(smoothed, smoothedPressure);
    }

    private static double Distance(Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
