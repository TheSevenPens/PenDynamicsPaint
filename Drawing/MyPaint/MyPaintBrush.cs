using System.Text.Json;

namespace PenDynamicsPaint.Drawing.MyPaint;

/// <summary>
/// The settings a MyPaint brush can carry that this application acts on.
/// </summary>
/// <remarks>
/// <para>
/// libmypaint defines sixty-four. These are the ones that reach the mark here, plus the handful
/// that decide how an input is computed. A <c>.myb</c> file naming any of the others loads with
/// that setting kept but unused -- see <see cref="MyPaintBrush.Ignored"/>, which is how a brush
/// says what it wanted and did not get.
/// </para>
/// <para>
/// The names are the ones in the file, so loading is a lookup rather than a translation table.
/// </para>
/// </remarks>
public enum MyPaintSetting
{
    /// <summary>Dab radius, as a natural log. The base radius of the brush.</summary>
    RadiusLogarithmic,

    /// <summary>Dab alpha before <see cref="OpaqueMultiply"/>.</summary>
    Opaque,

    /// <summary>A second alpha, multiplied in. Usually where pressure is attached.</summary>
    OpaqueMultiply,

    /// <summary>
    /// How much to thin each dab so that a pile of them reaches the opacity asked for.
    /// </summary>
    /// <remarks>
    /// 0 applies no correction and 1 applies it in full; the default is 0.9, so this is in force
    /// for nearly every brush file whether or not it mentions it.
    /// </remarks>
    OpaqueLinearize,

    /// <summary>Where the dab stops being solid and starts fading, as a fraction of its radius.</summary>
    Hardness,

    /// <summary>Dabs per radius of travel, measured against the brush's base radius.</summary>
    DabsPerBasicRadius,

    /// <summary>Dabs per radius of travel, measured against the dab's current radius.</summary>
    DabsPerActualRadius,

    /// <summary>How far a dab may be thrown off the path, in radii.</summary>
    OffsetByRandom,

    /// <summary>How much a dab's radius may vary at random.</summary>
    RadiusByRandom,

    /// <summary>Timescale the first speed input is smoothed over.</summary>
    Speed1Slowness,

    /// <inheritdoc cref="Speed1Slowness"/>
    Speed2Slowness,

    /// <summary>Shapes the first speed input's logarithmic response.</summary>
    Speed1Gamma,

    /// <inheritdoc cref="Speed1Gamma"/>
    Speed2Gamma,

    /// <summary>How much travel the stroke input takes to run from 0 to 1, as a negative log.</summary>
    StrokeDurationLogarithmic,

    /// <summary>How long the stroke input sits at 1 before wrapping back to 0.</summary>
    StrokeHoldtime,

    /// <summary>Applied to the pen's pressure before anything else reads it, as a log gain.</summary>
    PressureGainLog,
}

/// <summary>
/// One setting: a base value, plus a curve for each input that bends it.
/// </summary>
public sealed class DynamicSetting
{
    private readonly InputMapping?[] _mappings = new InputMapping?[BrushInputs.Count];

    public DynamicSetting(float baseValue) => BaseValue = baseValue;

    /// <summary>What the setting is worth before any input has a say.</summary>
    public float BaseValue { get; }

    /// <summary>Attach a curve, replacing any this input already had.</summary>
    public void Bend(BrushInput input, InputMapping mapping) => _mappings[(int)input] = mapping;

    /// <summary>True when no input bends this, so its value is its base value and nothing else.</summary>
    public bool IsConstant => _mappings.All(m => m is null or { IsEmpty: true });

    /// <summary>Which inputs have a curve attached.</summary>
    public IEnumerable<BrushInput> BentBy =>
        Enum.GetValues<BrushInput>().Where(i => _mappings[(int)i] is { IsEmpty: false });

    /// <summary>The setting's value for one sample's inputs.</summary>
    /// <remarks>
    /// Every curve is summed onto the base value, which is libmypaint's rule and the reason a
    /// setting can be pulled two ways at once.
    /// </remarks>
    public float ValueFor(in BrushInputs inputs)
    {
        float value = BaseValue;

        for (int i = 0; i < _mappings.Length; i++)
            if (_mappings[i] is { IsEmpty: false } mapping)
                value += mapping.Apply(inputs[(BrushInput)i]);

        return value;
    }
}

/// <summary>
/// A MyPaint brush: every setting it carries, and what it wanted that this application ignores.
/// </summary>
/// <remarks>
/// <para>
/// Loaded from a <c>.myb</c> file, which is JSON from version 3 onward. The older text format is
/// not read, and a file that is not JSON is refused rather than half understood.
/// </para>
/// <para>
/// <b>A brush is not rejected for asking too much.</b> Settings and inputs this application does
/// not have are recorded in <see cref="Ignored"/> and otherwise skipped, because most of a real
/// brush's behaviour survives losing one of them and a file that half loads is more use than one
/// that does not load at all. What matters is that the loss is visible rather than silent.
/// </para>
/// </remarks>
public sealed class MyPaintBrush
{
    /// <summary>libmypaint's own defaults, from <c>brushsettings.json</c> at <c>v1.6.1</c>.</summary>
    private static readonly IReadOnlyDictionary<MyPaintSetting, float> Defaults =
        new Dictionary<MyPaintSetting, float>
        {
            [MyPaintSetting.RadiusLogarithmic] = 2.0f,
            [MyPaintSetting.Opaque] = 1.0f,
            [MyPaintSetting.OpaqueMultiply] = 0.0f,
            [MyPaintSetting.OpaqueLinearize] = 0.9f,
            [MyPaintSetting.Hardness] = 0.8f,
            [MyPaintSetting.DabsPerBasicRadius] = 0.0f,
            [MyPaintSetting.DabsPerActualRadius] = 2.0f,
            [MyPaintSetting.OffsetByRandom] = 0.0f,
            [MyPaintSetting.RadiusByRandom] = 0.0f,
            [MyPaintSetting.Speed1Slowness] = 0.04f,
            [MyPaintSetting.Speed2Slowness] = 0.8f,
            [MyPaintSetting.Speed1Gamma] = 4.0f,
            [MyPaintSetting.Speed2Gamma] = 4.0f,
            [MyPaintSetting.StrokeDurationLogarithmic] = 4.0f,
            [MyPaintSetting.StrokeHoldtime] = 0.0f,
            [MyPaintSetting.PressureGainLog] = 0.0f,
        };

    /// <summary>
    /// Every setting's default, by the name a brush file uses, from <c>brushsettings.json</c> at
    /// <c>v1.6.1</c>.
    /// </summary>
    /// <remarks>
    /// All sixty-four, including the ones this application does not act on, because that is what
    /// makes <see cref="Ignored"/> honest: a brush that sets <c>elliptical_dab_ratio</c> to 1 has
    /// asked for a round dab, which is what it would have got anyway, and reporting it as lost
    /// would bury the settings that really were.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, float> FileDefaults =
        new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["anti_aliasing"] = 1f,
            ["change_color_h"] = 0f,
            ["change_color_hsl_s"] = 0f,
            ["change_color_hsv_s"] = 0f,
            ["change_color_l"] = 0f,
            ["change_color_v"] = 0f,
            ["color_h"] = 0f,
            ["color_s"] = 0f,
            ["color_v"] = 0f,
            ["colorize"] = 0f,
            ["custom_input"] = 0f,
            ["custom_input_slowness"] = 0f,
            ["dabs_per_actual_radius"] = 2f,
            ["dabs_per_basic_radius"] = 0f,
            ["dabs_per_second"] = 0f,
            ["direction_filter"] = 2f,
            ["elliptical_dab_angle"] = 90f,
            ["elliptical_dab_ratio"] = 1f,
            ["eraser"] = 0f,
            ["gridmap_scale"] = 0f,
            ["gridmap_scale_x"] = 1f,
            ["gridmap_scale_y"] = 1f,
            ["hardness"] = 0.8f,
            ["lock_alpha"] = 0f,
            ["offset_angle"] = 0f,
            ["offset_angle_2"] = 0f,
            ["offset_angle_2_asc"] = 0f,
            ["offset_angle_2_view"] = 0f,
            ["offset_angle_adj"] = 0f,
            ["offset_angle_asc"] = 0f,
            ["offset_angle_view"] = 0f,
            ["offset_by_random"] = 0f,
            ["offset_by_speed"] = 0f,
            ["offset_by_speed_slowness"] = 1f,
            ["offset_multiplier"] = 0f,
            ["offset_x"] = 0f,
            ["offset_y"] = 0f,
            ["opaque"] = 1f,
            ["opaque_linearize"] = 0.9f,
            ["opaque_multiply"] = 0f,
            ["paint_mode"] = 1f,
            ["posterize"] = 0f,
            ["posterize_num"] = 0.05f,
            ["pressure_gain_log"] = 0f,
            ["radius_by_random"] = 0f,
            ["radius_logarithmic"] = 2f,
            ["restore_color"] = 0f,
            ["slow_tracking"] = 0f,
            ["slow_tracking_per_dab"] = 0f,
            ["smudge"] = 0f,
            ["smudge_bucket"] = 0f,
            ["smudge_length"] = 0.5f,
            ["smudge_length_log"] = 0f,
            ["smudge_radius_log"] = 0f,
            ["smudge_transparency"] = 0f,
            ["snap_to_pixel"] = 0f,
            ["speed1_gamma"] = 4f,
            ["speed1_slowness"] = 0.04f,
            ["speed2_gamma"] = 4f,
            ["speed2_slowness"] = 0.8f,
            ["stroke_duration_logarithmic"] = 4f,
            ["stroke_holdtime"] = 0f,
            ["stroke_threshold"] = 0f,
            ["tracking_noise"] = 0f,
        };

    /// <summary>The file's name for each setting, so loading is a lookup.</summary>
    private static readonly IReadOnlyDictionary<string, MyPaintSetting> SettingNames =
        new Dictionary<string, MyPaintSetting>(StringComparer.Ordinal)
        {
            ["radius_logarithmic"] = MyPaintSetting.RadiusLogarithmic,
            ["opaque"] = MyPaintSetting.Opaque,
            ["opaque_multiply"] = MyPaintSetting.OpaqueMultiply,
            ["opaque_linearize"] = MyPaintSetting.OpaqueLinearize,
            ["hardness"] = MyPaintSetting.Hardness,
            ["dabs_per_basic_radius"] = MyPaintSetting.DabsPerBasicRadius,
            ["dabs_per_actual_radius"] = MyPaintSetting.DabsPerActualRadius,
            ["offset_by_random"] = MyPaintSetting.OffsetByRandom,
            ["radius_by_random"] = MyPaintSetting.RadiusByRandom,
            ["speed1_slowness"] = MyPaintSetting.Speed1Slowness,
            ["speed2_slowness"] = MyPaintSetting.Speed2Slowness,
            ["speed1_gamma"] = MyPaintSetting.Speed1Gamma,
            ["speed2_gamma"] = MyPaintSetting.Speed2Gamma,
            ["stroke_duration_logarithmic"] = MyPaintSetting.StrokeDurationLogarithmic,
            ["stroke_holdtime"] = MyPaintSetting.StrokeHoldtime,
            ["pressure_gain_log"] = MyPaintSetting.PressureGainLog,
        };

    /// <inheritdoc cref="SettingNames"/>
    private static readonly IReadOnlyDictionary<string, BrushInput> InputNames =
        new Dictionary<string, BrushInput>(StringComparer.Ordinal)
        {
            ["pressure"] = BrushInput.Pressure,
            ["speed1"] = BrushInput.Speed1,
            ["speed2"] = BrushInput.Speed2,
            ["random"] = BrushInput.Random,
            ["stroke"] = BrushInput.Stroke,
            ["direction"] = BrushInput.Direction,
            ["tilt_declination"] = BrushInput.TiltDeclination,
            ["tilt_ascension"] = BrushInput.TiltAscension,
            ["barrel_rotation"] = BrushInput.BarrelRotation,
        };

    private readonly DynamicSetting[] _settings;

    private MyPaintBrush(DynamicSetting[] settings, string name, IReadOnlyList<string> ignored)
    {
        _settings = settings;
        Name = name;
        Ignored = ignored;
    }

    /// <summary>What the brush is called. Taken from the file name, since a .myb carries no name.</summary>
    public string Name { get; }

    /// <summary>
    /// Settings and inputs the file asked for that this application does not act on.
    /// </summary>
    /// <remarks>
    /// Kept so the shortfall can be shown rather than guessed at. A brush that relies on smudge or
    /// elliptical dabs will not look right here, and this is what says so.
    /// </remarks>
    public IReadOnlyList<string> Ignored { get; }

    public DynamicSetting this[MyPaintSetting setting] => _settings[(int)setting];

    /// <summary>A brush with every setting at libmypaint's default and nothing bending it.</summary>
    public static MyPaintBrush Default { get; } = new(BuildDefaults(), "Default", []);

    private static DynamicSetting[] BuildDefaults()
    {
        var settings = new DynamicSetting[Defaults.Count];
        foreach (var (setting, value) in Defaults) settings[(int)setting] = new DynamicSetting(value);
        return settings;
    }

    /// <summary>Read a brush from the contents of a <c>.myb</c> file.</summary>
    /// <param name="name">What to call it; a brush file carries no name of its own.</param>
    /// <exception cref="FormatException">The text is not a version 3 or later brush.</exception>
    public static MyPaintBrush Parse(string json, string name)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            // Version 2 brushes are a line-based text format. Saying which it looks like is more
            // use than "invalid JSON", since a v2 file is a perfectly good brush this cannot read.
            throw new FormatException(
                json.TrimStart().StartsWith('{')
                    ? "the brush is not valid JSON"
                    : "the brush looks like the version 2 text format, which is not read here", e);
        }

        using (document)
        {
            var root = document.RootElement;
            var settings = BuildDefaults();
            var ignored = new List<string>();

            if (!root.TryGetProperty("settings", out var block) ||
                block.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("the brush has no settings block");
            }

            foreach (var entry in block.EnumerateObject())
            {
                if (!SettingNames.TryGetValue(entry.Name, out var setting))
                {
                    // Only worth reporting if the brush actually leans on it. A setting sitting at
                    // its default says nothing about what the brush wanted.
                    if (LeansOn(entry.Name, entry.Value)) ignored.Add(entry.Name);
                    continue;
                }

                float baseValue = entry.Value.TryGetProperty("base_value", out var b)
                    ? (float)b.GetDouble()
                    : Defaults[setting];

                var dynamic = new DynamicSetting(baseValue);
                settings[(int)setting] = dynamic;

                if (!entry.Value.TryGetProperty("inputs", out var inputs) ||
                    inputs.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var curve in inputs.EnumerateObject())
                {
                    if (!InputNames.TryGetValue(curve.Name, out var input))
                    {
                        ignored.Add($"{entry.Name} by {curve.Name}");
                        continue;
                    }

                    var points = ReadPoints(curve.Value);
                    if (points.Count >= 2) dynamic.Bend(input, new InputMapping(points));
                }
            }

            return new MyPaintBrush(settings, name, ignored);
        }
    }

    /// <summary>Whether a setting this application ignores was actually asked for.</summary>
    /// <remarks>
    /// Against libmypaint's default rather than against zero. Several settings default to
    /// something else -- a round dab is <c>elliptical_dab_ratio</c> of 1, not 0 -- so testing for
    /// non-zero would report every brush as wanting things it never asked for, and the ones it
    /// did ask for would be lost in the noise.
    /// </remarks>
    private static bool LeansOn(string name, JsonElement setting)
    {
        if (setting.ValueKind != JsonValueKind.Object) return false;

        if (setting.TryGetProperty("inputs", out var inputs) &&
            inputs.ValueKind == JsonValueKind.Object &&
            inputs.EnumerateObject().Any())
        {
            return true;
        }

        if (!setting.TryGetProperty("base_value", out var b) || b.ValueKind != JsonValueKind.Number)
            return false;

        // An unknown setting cannot be compared, so it counts as asked for: better to over-report
        // something the file named than to drop it silently.
        return !FileDefaults.TryGetValue(name, out float fallback) ||
               Math.Abs(b.GetDouble() - fallback) > 1e-6;
    }

    private static List<(float X, float Y)> ReadPoints(JsonElement curve)
    {
        var points = new List<(float, float)>();
        if (curve.ValueKind != JsonValueKind.Array) return points;

        foreach (var point in curve.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2) continue;

            points.Add(((float)point[0].GetDouble(), (float)point[1].GetDouble()));
        }

        return points;
    }
}
