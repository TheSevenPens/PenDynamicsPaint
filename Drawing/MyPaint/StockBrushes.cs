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
