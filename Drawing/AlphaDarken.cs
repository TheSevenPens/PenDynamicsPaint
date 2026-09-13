using SkiaSharp;

namespace PenDynamicsPaint.Drawing;

/// <summary>
/// Compositing where overlapping marks take the <b>greater</b> alpha rather than accumulating.
/// </summary>
/// <remarks>
/// <para>
/// This is what stops a stroke darkening itself. Painted with ordinary source-over, a translucent
/// mark composites again at every overlap, and a real stroke puts about a hundred overlapping
/// segments over each pixel: a stroke asked for 15% alpha comes out at 99%. Measured, in
/// TheSevenPens/PenDynamicsLab#68.
/// </para>
/// <para>
/// Krita solves it the same way and has a name for it: dabs paint into a temporary device with
/// <c>COMPOSITE_ALPHA_DARKEN</c>, and that device merges onto the layer once at the brush's
/// opacity. Per-dab pressure-driven opacity survives intact, because taking the maximum preserves
/// whatever each mark asked for instead of summing them.
/// </para>
/// <para>
/// Skia has no such blend mode, so it is written as a runtime blender.
/// <c>AlphaDarkenBlenderTests</c> pins that this works on the CPU raster backend, including the
/// case that matters: a hundred marks at alpha 38 stay at 38.
/// </para>
/// </remarks>
public static class AlphaDarken
{
    /// <summary>
    /// Alpha-darken over premultiplied colours.
    /// </summary>
    /// <remarks>
    /// The alpha is the maximum of the two. The colour is whichever source has coverage,
    /// unpremultiplied and re-premultiplied at the new alpha -- which for a stroke drawn in one
    /// colour is that colour, and the reason this stays so simple here.
    /// </remarks>
    private const string Sksl = """
        half4 main(half4 src, half4 dst) {
            half a = max(src.a, dst.a);
            half3 c = src.a > 0.0 ? src.rgb / src.a
                    : (dst.a > 0.0 ? dst.rgb / dst.a : half3(0.0));
            return half4(c * a, a);
        }
        """;

    private static SKBlender? _blender;

    /// <summary>
    /// The shared blender, or null if this build of Skia will not compile it.
    /// </summary>
    /// <remarks>
    /// Built once and kept: it is immutable and compiling SkSL per stroke would be wasteful. Null
    /// rather than throwing so that a Skia that cannot do this degrades to ordinary compositing
    /// with a visible artifact rather than an application that will not draw at all.
    /// </remarks>
    public static SKBlender? Blender
    {
        get
        {
            if (_blender is not null) return _blender;

            using var effect = SKRuntimeEffect.CreateBlender(Sksl, out string? errors);
            if (effect is null || !string.IsNullOrEmpty(errors)) return null;

            _blender = effect.ToBlender();
            return _blender;
        }
    }
}
