namespace PenDynamicsPaint.Drawing;

/// <summary>
/// The brushes the application opens with.
/// </summary>
/// <remarks>
/// <para>
/// Not a file format and not a preset manager. It is a starting set, chosen so that every setting
/// that moved onto <see cref="BrushSettings"/> has at least one brush where it is doing something
/// -- a library where all four entries differ only in size would prove nothing about whether the
/// settings actually reach the mark.
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
        },
    ];
}
