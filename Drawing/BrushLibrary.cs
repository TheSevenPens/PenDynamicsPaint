using PenDynamicsPaint.Drawing.MyPaint;

namespace PenDynamicsPaint.Drawing;

/// <summary>
/// The brushes the application opens with.
/// </summary>
/// <remarks>
/// <para>
/// Not a file format and not a preset manager. It is a starting set, chosen so that every setting
/// that moved onto <see cref="BrushSettings"/> has at least one brush where it is doing something
/// -- a library where all four entries differ only in size would prove nothing about whether the
/// settings actually reach the mark. That includes the two smoothing reaches, which between them
/// cover path only, pressure only, both, and neither, and both ways of joining the samples up.
/// </para>
/// <para>
/// Editing a brush in the panel changes the copy held there for the session. Nothing is written to
/// disk: what a saved brush file should contain is a decision that has not been made, and guessing
/// at one now would mean a format to migrate later.
/// </para>
/// </remarks>
public static class BrushLibrary
{
    /// <summary>The opening set, in the order the picker shows them.</summary>
    public static IReadOnlyList<BrushSettings> Defaults { get; } =
    [
        // A hard nib. Pressure moves width and nothing else, and the curve leaves the low end of
        // the range alone so the taper starts as soon as the pen touches.
        new BrushSettings
        {
            Name = "Ink pen",
            Engine = BrushEngineKind.Taper,
            Size = 18,
            PressureDrives = PressureControl.Size,
            Curve = new PressureCurve(0.0, 0.85, 1.4),

            // Inking is where a steady line is worth a little lag. The pressure is left
            // unfiltered: this brush wants its line straightened, not its weight evened out.
            Smoothing = new StrokeSmoothing { Position = 55 },

            // And where the corners of a polygon show most. A fitted path costs one more sample
            // of lag on top of the filter's, which is the trade an inking brush is built to make.
            Interpolation = StrokeInterpolation.Curved,
        },

        // Translucent and a constant width, which is the case Wash exists for: a stroke asked for
        // 35% ink gets 35% ink however many overlapping segments it is made of.
        new BrushSettings
        {
            Name = "Marker",
            Engine = BrushEngineKind.Taper,
            Size = 42,
            Opacity = 0.35,
            PressureDrives = PressureControl.Opacity,
            Curve = new PressureCurve(0.05, 0.7, 1.0),

            // The opposite case, and the reason the two reaches are separate: pressure drives
            // opacity here, so a jumpy sensor shows as a blotchy stroke. Steady the pressure and
            // leave the path alone.
            Smoothing = new StrokeSmoothing { Pressure = 70 },
        },

        // Stamped marks, close enough together to read as a continuous stroke. Pressure drives
        // both, so it changes the width and the spacing with it.
        new BrushSettings
        {
            Name = "Round dabs",
            Engine = BrushEngineKind.Dabs,
            Size = 36,
            Spacing = 0.1,
            PressureDrives = PressureControl.Both,
            Curve = new PressureCurve(0.02, 1.0, 1.0),

            // Pressure drives size and opacity together here, so both halves are worth steadying.
            Smoothing = new StrokeSmoothing { Position = 30, Pressure = 40 },
            Interpolation = StrokeInterpolation.Curved,
        },

        // The same engine with the spacing walked apart, which is what makes distance spacing
        // visible rather than merely different. Worth keeping in the opening set for that reason.
        new BrushSettings
        {
            Name = "Beads",
            Engine = BrushEngineKind.Dabs,
            Size = 28,
            Spacing = 1.0,
            PressureDrives = PressureControl.Size,
            Curve = new PressureCurve(0.0, 1.0, 0.7),

            // Unfiltered, so the opening set has one brush that shows the pen exactly as it
            // reported -- which is the thing this application's neighbour exists to look at.
            Smoothing = StrokeSmoothing.None,
        },

        // The two MyPaint brushes. Their size, opacity, softness and spacing come from the brush
        // file rather than from the sliders, which is why the panel greys those out when one is
        // selected: moving them would say nothing.
        new BrushSettings
        {
            Name = "Speed pen",
            Engine = BrushEngineKind.MyPaint,
            MyPaint = MyPaintBrush.Parse(StockBrushes.SpeedPen, "Speed pen"),
            Interpolation = StrokeInterpolation.Curved,
            Smoothing = new StrokeSmoothing { Position = 40 },
        },

        new BrushSettings
        {
            Name = "Tilt charcoal",
            Engine = BrushEngineKind.MyPaint,
            MyPaint = MyPaintBrush.Parse(StockBrushes.TiltCharcoal, "Tilt charcoal"),

            // The one brush in the set whose width follows the pen's lean, so the one that needs
            // the tilt reach. A longer reach than the path gets: tilt arrives quantised to whole
            // degrees, and each of those steps is a step in the width of the mark.
            Smoothing = new StrokeSmoothing { Position = 25, Tilt = 60 },
        },

        // Moves paint rather than laying any, so it is the one brush here that does nothing at
        // all on an empty canvas.
        new BrushSettings
        {
            Name = "Smudge",
            Engine = BrushEngineKind.MyPaint,
            MyPaint = MyPaintBrush.Parse(StockBrushes.Smudge, "Smudge"),
            Smoothing = new StrokeSmoothing { Position = 30 },
        },

        // A diagnostic rather than a brush to draw with. It is in the opening set because the
        // thing it measures -- how steady the tilt reaching a brush is -- is otherwise only
        // visible through a brush whose texture hides it.
        //
        // Its tilt reach starts at zero on purpose: the point is to move that slider and watch the
        // colour settle, so it has to start from the unfiltered case.
        new BrushSettings
        {
            Name = "Tilt testing",
            Engine = BrushEngineKind.MyPaint,
            MyPaint = MyPaintBrush.Parse(StockBrushes.TiltTesting, "Tilt testing"),
            Smoothing = new StrokeSmoothing { Position = 25 },
        },
    ];
}
