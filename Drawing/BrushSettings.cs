namespace PenDynamicsPaint.Drawing;

/// <summary>
/// What the brush is configured to do: size, colour mode, which property pressure drives,
/// and whether a zero-pressure sample still marks.
/// </summary>
/// <remarks>
/// <para>
/// Brush state used to be read on demand off <c>BrushRibbon</c>'s controls, which is convenient
/// for exactly one ribbon and wrong for anything else — a second tool, a stamp engine, a
/// headless replay, or a tilt-shaped nib all need these numbers without asking a
/// <c>UserControl</c> for them. The ribbon is now a view over this record rather than the place
/// the values live.
/// </para>
/// <para>
/// <b>Declarative only.</b> The <em>resolved</em> colour of the stroke in progress is not here:
/// it changes mid-gesture, and an immutable settings record is the wrong home for something
/// that does. <see cref="ColorMode.Random"/> makes that concrete — the colour a stroke actually
/// got cannot be recovered from these settings afterwards, so whatever records strokes has to
/// capture it at stroke start. That state belongs to the drawing session.
/// </para>
/// <para>
/// Deliberately not persisted with curve presets. Presets are the pressure pipeline; the ribbon
/// is view state until there is a reason to save it. A preset that silently changed your brush
/// size would be a genuinely bad surprise — the same reasoning that keeps <c>SmoothingOrder</c>
/// on <c>UiSettings</c>.
/// </para>
/// </remarks>
public sealed record BrushSettings
{
    /// <summary>Smallest usable brush size, matching the ribbon slider's minimum.</summary>
    public const double MinSize = 1;

    /// <summary>Largest brush size, matching the ribbon slider's maximum.</summary>
    public const double MaxSize = 200;

    /// <summary>The ribbon slider's starting value.</summary>
    public const double DefaultSize = 40;

    /// <summary>
    /// The thinnest mark a stroke can taper to, in DIPs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be 1 DIP, on the reasoning that a zero-width stroke draws nothing so a light
    /// touch would silently skip. True of zero, but 1 DIP is far more than zero: on a 168 dpi
    /// display it is 1.75 physical pixels, so the entry and exit of every stroke stopped dead at
    /// a visible width instead of fading out. Krita tapers to a genuine sub-pixel hairline, which
    /// is most of why its stroke ends look like ink and ours looked like a marker.
    /// </para>
    /// <para>
    /// A quarter of a DIP is still safely above zero - the geometry stays valid and the faintest
    /// contact leaves a trace - while being thin enough that antialiasing renders it as a fading
    /// hairline rather than a line with a width. Lower it further for a fuller fade-out; the
    /// floor exists to keep a mark, not to set a look.
    /// </para>
    /// </remarks>
    public const double MinStrokeWidth = 0.25;

    private readonly double _size = DefaultSize;

    /// <summary>
    /// Brush size in DIPs, clamped to [<see cref="MinSize"/>, <see cref="MaxSize"/>].
    /// </summary>
    /// <remarks>
    /// The range used to be enforced only by the slider's <c>Minimum</c>/<c>Maximum</c>. Once the
    /// record is the source of truth the control is no longer what guards it, and a preset or a
    /// headless caller could hand over a zero or a negative — so the clamp lives here. Written as
    /// an <c>init</c> accessor rather than a constructor check so that <c>with</c> expressions are
    /// guarded too, which is how most callers will build one.
    /// </remarks>
    public double Size
    {
        get => _size;
        init => _size = double.IsNaN(value) ? DefaultSize : Math.Clamp(value, MinSize, MaxSize);
    }

    /// <summary>How each new stroke picks its colour.</summary>
    public ColorMode ColorMode { get; init; } = ColorMode.Black;

    /// <summary>Which property of the mark pressure drives.</summary>
    public PressureControl PressureDrives { get; init; } = PressureControl.Size;

    /// <summary>Whether a sample with no pressure still puts something down.</summary>
    public bool DrawAtZeroPressure { get; init; }

    public static BrushSettings Default { get; } = new();

    /// <summary>
    /// Stroke width in DIPs for a pipeline output value.
    /// </summary>
    /// <remarks>
    /// Lives on the record rather than on the window so that anything holding these settings can
    /// work out the mark — which is the point of having a record at all. Floored at
    /// <see cref="MinStrokeWidth"/> rather than at zero, so the faintest contact still marks.
    /// </remarks>
    public float StrokeWidthFor(double pressure) => PressureDrives == PressureControl.Opacity
        ? (float)Size
        : (float)Math.Max(MinStrokeWidth, pressure * Size);

    /// <summary>
    /// Stroke opacity for a pipeline output value.
    /// </summary>
    /// <remarks>
    /// Floored at 0.02 rather than 0 for the same reason: fully transparent is indistinguishable
    /// from not drawing, and the faintest contact should still leave a trace.
    /// </remarks>
    public float OpacityFor(double pressure) => PressureDrives == PressureControl.Opacity
        ? (float)Math.Max(0.02, pressure)
        : 1f;
}
