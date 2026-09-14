namespace PenDynamicsPaint.Drawing;

/// <summary>
/// Everything that decides what a brush's marks look like: which engine draws them, how big, how
/// opaque, how far apart, and how the brush reads the pen.
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
/// <b>The whole record is what a brush is</b>, and a stroke keeps a copy of the one that drew it.
/// That is what makes an undo faithful: replay uses the engine, size, spacing and curve the stroke
/// was actually made with rather than whatever is selected now. Before <see cref="Engine"/> and
/// <see cref="Spacing"/> moved here they were application state, and switching brush and then
/// undoing redrew the older strokes in the new brush's style.
/// </para>
/// <para>
/// Not persisted to disk. The library of brushes is built in code and lives for the session;
/// saving and loading them is a file format decision that has not been made.
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

    /// <summary>The default spacing, as a fraction of a dab's diameter.</summary>
    /// <remarks>
    /// Ten marks across each dab's width, which reads as a continuous stroke rather than as
    /// stamps. Ignored by <see cref="BrushEngineKind.Taper"/>, which has no marks to space.
    /// </remarks>
    public const double DefaultSpacing = 0.1;

    private readonly double _size = DefaultSize;
    private readonly double _spacing = DefaultSpacing;
    private readonly double _opacity = 1.0;

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

    /// <summary>What this brush is called in the picker. Nothing but the UI reads it.</summary>
    public string Name { get; init; } = "Brush";

    /// <summary>Which engine lays the marks down.</summary>
    public BrushEngineKind Engine { get; init; } = BrushEngineKind.Taper;

    /// <summary>
    /// Distance between dabs, as a fraction of the dab's own diameter.
    /// </summary>
    /// <remarks>
    /// A fraction rather than a distance, which is what keeps a stroke reading as one stroke while
    /// pressure changes its width: pressure drives size, so it drives spacing with it. Raising it
    /// toward 1 walks the marks apart until they bead.
    /// </remarks>
    public double Spacing
    {
        get => _spacing;
        init => _spacing = double.IsNaN(value) ? DefaultSpacing : Math.Clamp(value, 0.01, 4.0);
    }

    /// <summary>
    /// How opaque the brush is at full strength, before pressure has any say.
    /// </summary>
    /// <remarks>
    /// Krita's brush opacity, and it is what Wash exists to make honest: a brush set to 15% should
    /// put down 15% ink however many overlapping marks the stroke is made of.
    /// </remarks>
    public double Opacity
    {
        get => _opacity;
        init => _opacity = double.IsNaN(value) ? 1 : Math.Clamp(value, 0, 1);
    }

    /// <summary>What the pen says about the width of the mark.</summary>
    /// <remarks>
    /// This and <see cref="OpacityDynamics"/> replace a single <c>Curve</c> shared by both and a
    /// <c>PressureDrives</c> enum saying which of them it reached. Shared, the two could never
    /// have different shapes, and the enum could say that pressure drove both but not that it
    /// drove them differently -- which is the ordinary case, since a brush usually wants width to
    /// come on faster than ink.
    /// </remarks>
    /// <remarks>
    /// Pressure by default, because a brush that ignores the pen is the odd one out and because
    /// that is what the enum this replaced defaulted to.
    /// </remarks>
    public Dynamics SizeDynamics { get; init; } = Dynamics.FromPressure;

    /// <summary>What the pen says about how much ink the mark puts down.</summary>
    /// <remarks>
    /// No editor yet: the panel has a checkbox for whether pressure reaches opacity at all, and
    /// the curve is whichever one the brush was built with. It is the same type as
    /// <see cref="SizeDynamics"/>, so giving opacity its own button is a matter of pointing the
    /// flyout at this instead.
    /// </remarks>
    public Dynamics OpacityDynamics { get; init; } = Dynamics.None;

    /// <summary>
    /// The MyPaint brush this uses, when <see cref="Engine"/> is
    /// <see cref="BrushEngineKind.MyPaint"/>.
    /// </summary>
    /// <remarks>
    /// A whole brush rather than a handful of fields, because that is what a <c>.myb</c> file is:
    /// sixty-odd settings, each with its own curve for each input. Null means the engine falls back
    /// to libmypaint's defaults, which is a usable round brush.
    /// </remarks>
    public MyPaint.MyPaintBrush? MyPaint { get; init; }

    /// <summary>What the ink does between two pen samples.</summary>
    /// <remarks>
    /// On the brush for the same reason smoothing is: it changes what the stroke looks like, and a
    /// brush that draws a crisp polygon and one that draws a fitted arc are different brushes.
    /// </remarks>
    public StrokeInterpolation Interpolation { get; init; } = StrokeInterpolation.Straight;

    /// <summary>
    /// How much this brush steadies the pen before drawing with it.
    /// </summary>
    /// <remarks>
    /// Here rather than on the application because a stabilised inking brush and an unfiltered
    /// sketching brush want different answers in the same session: leave it outside the brush and
    /// switching brush means setting it again by hand every time. Krita treats it as a tool option;
    /// libmypaint treats slow tracking as a brush property, and that is the one that survives
    /// contact with using it.
    /// </remarks>
    public StrokeSmoothing Smoothing { get; init; } = StrokeSmoothing.None;

    /// <summary>How the marks within a stroke combine with each other.</summary>
    /// <remarks>
    /// <para>
    /// On the brush, alongside smoothing and interpolation, and it sat on the document first. The
    /// argument for the document was that this decides how a stroke reaches the layer rather than
    /// what the mark looks like, and that is the same question whichever brush drew it. In use it
    /// is not: a marker wants its overlaps flattened and a dry-media brush wants them to build up,
    /// and leaving the choice outside the brush means setting it again every time you switch.
    /// </para>
    /// <para>
    /// It still takes effect at the start of a stroke rather than during one, since a stroke half
    /// composited one way cannot finish the other. A stroke records its brush, so it records this.
    /// </para>
    /// </remarks>
    public StrokeCompositing Compositing { get; init; } = StrokeCompositing.Wash;

    /// <summary>How each new stroke picks its colour.</summary>
    public ColorMode ColorMode { get; init; } = ColorMode.Black;

    /// <summary>Whether a sample with no pressure still puts something down.</summary>
    public bool DrawAtZeroPressure { get; init; }

    public static BrushSettings Default { get; } = new();

    /// <summary>
    /// Stroke width in DIPs for one pen reading.
    /// </summary>
    /// <remarks>
    /// Takes the reading as the pen gave it: the curve that shapes it belongs to
    /// <see cref="SizeDynamics"/> and is applied here, rather than once on the way in for every
    /// property to share. Lives on the record so that anything holding these settings can work out
    /// the mark, which is the point of having a record at all. Floored at
    /// <see cref="MinStrokeWidth"/> rather than at zero, so the faintest contact still marks.
    /// </remarks>
    public float StrokeWidthFor(double pressure) =>
        (float)Math.Max(MinStrokeWidth, Size * SizeDynamics.Scale(pressure));

    /// <summary>
    /// Stroke opacity for one pen reading.
    /// </summary>
    /// <remarks>
    /// <see cref="Opacity"/> sets the ceiling and the dynamics scale it, so a 15% brush never
    /// exceeds 15% however hard it is pressed. Floored at 0.02 of that ceiling rather than at 0:
    /// fully transparent is indistinguishable from not drawing, and the faintest contact should
    /// still leave a trace. A brush with nothing driving its opacity scales by 1 and so is
    /// untouched by either.
    /// </remarks>
    public float OpacityFor(double pressure) =>
        (float)(Opacity * Math.Max(0.02, OpacityDynamics.Scale(pressure)));
}
