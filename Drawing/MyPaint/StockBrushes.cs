namespace PenDynamicsPaint.Drawing.MyPaint;

/// <summary>
/// A couple of MyPaint brushes written here, so the model has something to show without a file.
/// </summary>
/// <remarks>
/// <para>
/// Written rather than shipped. MyPaint's own brush collection is someone else's work under its
/// own licence, and vendoring it to make a demo would be borrowing without asking. These are in
/// the same format -- they go through <see cref="MyPaintBrush.Parse"/> like any other file, so the
/// loader is exercised by the application starting up.
/// </para>
/// <para>
/// Between them they drive the radius from tilt, from speed and from the stroke position, which
/// are the three that are new here. A brush responding to pressure alone would have shown nothing
/// the existing engines could not already do.
/// </para>
/// </remarks>
public static class StockBrushes
{
    /// <summary>
    /// An inking pen that thins as it is pushed faster, the way a real nib runs dry.
    /// </summary>
    public const string SpeedPen = """
        {
          "version": 3,
          "comment": "Written for PenDynamicsPaint. Radius falls as speed1 rises.",
          "settings": {
            "radius_logarithmic":      { "base_value": 1.6,
                                         "inputs": { "pressure": [[0.0, -0.9], [1.0, 0.25]],
                                                     "speed1":   [[0.0, 0.25], [1.0, -0.55]] } },
            "opaque":                  { "base_value": 1.0 },
            "opaque_multiply":         { "base_value": 0.0,
                                         "inputs": { "pressure": [[0.0, 0.0], [0.35, 0.85], [1.0, 1.0]] } },
            "hardness":                { "base_value": 0.92 },
            "dabs_per_actual_radius":  { "base_value": 6.0 }
          }
        }
        """;

    /// <summary>
    /// A finger for pushing wet paint around: it lays almost no ink of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>smudge</c> at 1 means the dab is made entirely of what was already on the canvas, so
    /// this moves paint rather than adding any. Pressure drives the length -- press lightly and it
    /// barely picks anything up, press hard and it drags a colour a long way.
    /// </para>
    /// <para>
    /// It does nothing on a blank canvas, and that is not a fault: there is nothing to pick up, so
    /// every dab comes out fully transparent and none is drawn. Paint something first.
    /// </para>
    /// </remarks>
    public const string Smudge = """
        {
          "version": 3,
          "comment": "Written for PenDynamicsPaint. Moves paint instead of laying any.",
          "settings": {
            "radius_logarithmic":      { "base_value": 2.7 },
            "opaque":                  { "base_value": 1.0 },
            "opaque_multiply":         { "base_value": 1.0 },
            "hardness":                { "base_value": 0.7 },
            "dabs_per_actual_radius":  { "base_value": 6.0 },
            "smudge":                  { "base_value": 1.0 },
            "smudge_length":           { "base_value": 0.4,
                                         "inputs": { "pressure": [[0.0, 0.0], [1.0, 0.5]] } },
            "smudge_radius_log":       { "base_value": 0.2 }
          }
        }
        """;

    /// <summary>
    /// A diagnostic: a flat even line whose <b>hue</b> is the direction the pen is leaning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For looking at how steady tilt is, and for nothing else. Width is the obvious readout and a
    /// poor one: it is entangled with pressure and with whatever texture the brush has, so on a
    /// charcoal that already breaks up you cannot tell a wobble in the tilt from the brush doing
    /// its job. Hue has neither problem -- the eye picks out a band or a flicker in it immediately,
    /// and nothing else here touches it.
    /// </para>
    /// <para>
    /// Nothing varies but the colour: the radius is fixed, the ink is opaque and the dabs are hard.
    /// So any change along a stroke is the pen's orientation and cannot be anything else.
    /// </para>
    /// <para>
    /// <c>tilt_ascension</c> rather than declination, because the bearing is the noisy one: near
    /// vertical it is the pole of a spherical coordinate and swings wildly for no real movement.
    /// The full turn is mapped onto the full wheel, so rolling the pen around walks the hue around
    /// once and comes back.
    /// </para>
    /// </remarks>
    public const string TiltTesting = """
        {
          "version": 3,
          "comment": "Written for PenDynamicsPaint. Hue follows tilt_ascension; nothing else moves.",
          "settings": {
            "radius_logarithmic":      { "base_value": 2.4 },
            "opaque":                  { "base_value": 1.0 },
            "opaque_multiply":         { "base_value": 1.0 },
            "opaque_linearize":        { "base_value": 0.0 },
            "hardness":                { "base_value": 1.0 },
            "dabs_per_actual_radius":  { "base_value": 6.0 },
            "change_color_h":          { "base_value": 0.0,
                                         "inputs": { "tilt_ascension": [[-180.0, -0.5], [180.0, 0.5]] } },
            "change_color_hsv_s":      { "base_value": 5.0 },
            "change_color_v":          { "base_value": 0.75 }
          }
        }
        """;

    /// <summary>
    /// A soft charcoal that broadens as the pen is laid over, and scatters as it does.
    /// </summary>
    /// <remarks>
    /// The one to try tilt with. Held upright it is a narrow soft line; leaned over it spreads and
    /// breaks up, because declination drives the radius and the random offset together.
    /// </remarks>
    public const string TiltCharcoal = """
        {
          "version": 3,
          "comment": "Written for PenDynamicsPaint. Radius and scatter follow tilt declination.",
          "settings": {
            "radius_logarithmic":      { "base_value": 1.9,
                                         "inputs": { "pressure":         [[0.0, -0.6], [1.0, 0.2]],
                                                     "tilt_declination": [[0.0, 0.0], [90.0, 1.1]] } },
            "opaque":                  { "base_value": 1.0 },
            "opaque_multiply":         { "base_value": 0.0,
                                         "inputs": { "pressure": [[0.0, 0.0], [1.0, 0.75]] } },
            "hardness":                { "base_value": 0.22 },
            "dabs_per_actual_radius":  { "base_value": 3.5 },
            "offset_by_random":        { "base_value": 0.0,
                                         "inputs": { "tilt_declination": [[0.0, 0.0], [90.0, 0.55]] } },
            "radius_by_random":        { "base_value": 0.18 }
          }
        }
        """;
}
