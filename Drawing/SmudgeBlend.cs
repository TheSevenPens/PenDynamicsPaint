using SkiaSharp;

namespace PenDynamicsPaint.Drawing;

/// <summary>
/// Compositing that moves paint rather than adding it: the canvas is pulled <b>towards</b> the
/// dab's colour and alpha instead of having the dab laid over it.
/// </summary>
/// <remarks>
/// <para>
/// What a smudge needs and source-over cannot do. Painting a dab over the canvas can only ever put
/// more paint down, so a smudge dragged off the edge of a mark copies the colour onwards forever
/// and the mark it came from never loses anything. Pulling the canvas towards the dab instead
/// means that where the dab is carrying less paint than the canvas holds, the canvas ends up with
/// less -- which is a smudge running out, and a mark being spread thinner rather than duplicated.
/// </para>
/// <para>
/// Ported from <c>draw_dab_pixels_BlendMode_Normal_and_Eraser</c> in libmypaint's
/// <c>brushmodes.c</c> at <c>v1.6.1</c>. In its terms, for coverage <c>c</c> and a target alpha
/// <c>t</c>: the new alpha is <c>c*t + (1-c)*dst.a</c>, and the colour is carried the same way.
/// It is a plain interpolation towards the dab, weighted by how much of the dab covers the pixel.
/// </para>
/// <para>
/// <b>The target is a uniform rather than the paint's alpha</b>, because the two are different
/// things and Skia multiplies the paint's alpha into the source before a blender ever sees it. The
/// source's alpha has to stay as the dab's own coverage or there is nothing left to interpolate
/// by. Blenders are cached one per 8-bit target, since a dab's target is a byte by the time it has
/// been through the canvas.
/// </para>
/// </remarks>
public static class SmudgeBlend
{
    private const string Sksl = """
        uniform half target;

        half4 main(half4 src, half4 dst) {
            half c = src.a;
            half3 ink = c > 0.0 ? src.rgb / c : half3(0.0);

            half a = c * target + (1.0 - c) * dst.a;
            half3 p = c * target * ink + (1.0 - c) * dst.rgb;

            return half4(p, a);
        }
        """;

    private static readonly SKBlender?[] Cache = new SKBlender?[256];
    private static SKRuntimeEffect? _effect;
    private static bool _tried;

    /// <summary>
    /// A blender that pulls the canvas towards a dab whose alpha target is
    /// <paramref name="target"/>, or null if this build of Skia will not compile it.
    /// </summary>
    /// <remarks>
    /// Null rather than throwing, the way <see cref="AlphaDarken"/> does it: a Skia that cannot
    /// run this should degrade to ordinary painting with a visible artifact rather than refuse to
    /// draw. The artifact in that case is this very fault -- a smudge that copies instead of
    /// moving.
    /// </remarks>
    public static SKBlender? For(double target)
    {
        int index = (int)Math.Clamp(Math.Round(target * 255), 0, 255);
        if (Cache[index] is { } cached) return cached;

        var effect = Effect();
        if (effect is null) return null;

        var uniforms = new SKRuntimeEffectUniforms(effect) { ["target"] = index / 255f };

        var blender = effect.ToBlender(uniforms);
        Cache[index] = blender;
        return blender;
    }

    private static SKRuntimeEffect? Effect()
    {
        if (_tried) return _effect;
        _tried = true;

        var effect = SKRuntimeEffect.CreateBlender(Sksl, out string? errors);
        if (effect is null || !string.IsNullOrEmpty(errors)) return null;

        _effect = effect;
        return _effect;
    }
}
