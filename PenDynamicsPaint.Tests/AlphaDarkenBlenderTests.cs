using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// Establishes that Skia can do what Krita's Wash mode is built on: overlapping marks taking the
/// <b>maximum</b> alpha rather than accumulating.
/// </summary>
/// <remarks>
/// <para>
/// Krita paints a stroke's dabs into a temporary device with <c>COMPOSITE_ALPHA_DARKEN</c> and
/// merges that device onto the layer once, at the brush's opacity. Alpha-darken is what stops a
/// stroke darkening itself: where two dabs overlap the result is the greater of the two alphas,
/// not their sum, so a light stroke stays light however many samples it is made of.
/// </para>
/// <para>
/// Skia has no such blend mode. It does have runtime blenders, and this is the check that one can
/// express alpha-darken and that it behaves on the CPU raster backend — which is the only backend
/// this application uses. Written before anything depends on it, because "Skia can probably do
/// this" is not a thing to build a stroke renderer on.
/// </para>
/// <para>
/// <b>Nothing calls this yet.</b> Wash has not been built. These tests are the evidence behind the
/// decision that it can be built the way Krita builds it — they are what turned "per-sample opacity
/// capped at its own value" from a hope into a settled design — and they will fail loudly if a
/// SkiaSharp upgrade takes the capability away before anything uses it.
/// </para>
/// <para>
/// The artifact that makes Wash worth having was measured next door in PenDynamicsLab, whose naive
/// renderer keeps it on purpose as reference behaviour: a stroke drawn at 15% pressure renders at
/// 99%, because a hundred overlapping segments per pixel accumulate under source-over. See
/// TheSevenPens/PenDynamicsLab#68.
/// </para>
/// </remarks>
public class AlphaDarkenBlenderTests
{
    /// <summary>
    /// Alpha-darken, over premultiplied colours.
    /// </summary>
    /// <remarks>
    /// The alpha is the maximum of the two. The colour is whichever source actually has coverage,
    /// unpremultiplied and re-premultiplied at the new alpha — which for a stroke drawn in one
    /// colour is that colour, and the reason this works so simply here.
    /// </remarks>
    private const string AlphaDarkenSksl = """
        half4 main(half4 src, half4 dst) {
            half a = max(src.a, dst.a);
            half3 c = src.a > 0.0 ? src.rgb / src.a
                    : (dst.a > 0.0 ? dst.rgb / dst.a : half3(0.0));
            return half4(c * a, a);
        }
        """;

    private static SKBlender MakeBlender()
    {
        using var effect = SKRuntimeEffect.CreateBlender(AlphaDarkenSksl, out string? errors);
        Assert.True(string.IsNullOrEmpty(errors), $"SkSL failed to compile: {errors}");
        Assert.NotNull(effect);
        return effect!.ToBlender();
    }

    /// <summary>Alpha of the pixel at the centre of a bitmap.</summary>
    private static byte AlphaAt(SKBitmap bmp, int x, int y) => bmp.GetPixel(x, y).Alpha;

    [Fact]
    public void The_blender_compiles()
    {
        using var blender = MakeBlender();
        Assert.NotNull(blender);
    }

    [Fact]
    public void Two_overlapping_marks_take_the_greater_alpha_not_the_sum()
    {
        // Two rectangles at 50% alpha, overlapping in the middle. Ordinary source-over would make
        // the overlap 75%; alpha-darken leaves it at 50%.
        using var bmp = new SKBitmap(60, 20, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        using var blender = MakeBlender();
        using var paint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 128),
            IsAntialias = false,
            Blender = blender,
        };

        canvas.DrawRect(new SKRect(0, 0, 40, 20), paint);
        canvas.DrawRect(new SKRect(20, 0, 60, 20), paint);

        Assert.Equal(128, AlphaAt(bmp, 10, 10));   // first only
        Assert.Equal(128, AlphaAt(bmp, 30, 10));   // the overlap — the whole point
        Assert.Equal(128, AlphaAt(bmp, 50, 10));   // second only
    }

    [Fact]
    public void Source_over_really_would_have_accumulated()
    {
        // The control. Without this, the test above could pass because nothing was drawn twice.
        using var bmp = new SKBitmap(60, 20, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint { Color = new SKColor(0, 0, 0, 128), IsAntialias = false };
        canvas.DrawRect(new SKRect(0, 0, 40, 20), paint);
        canvas.DrawRect(new SKRect(20, 0, 60, 20), paint);

        Assert.Equal(128, AlphaAt(bmp, 10, 10));
        Assert.Equal(192, AlphaAt(bmp, 30, 10));   // 128 + 128*(1-0.5) — the artifact, measured
    }

    [Fact]
    public void A_stronger_mark_wins_and_a_weaker_one_does_not_erase()
    {
        // Order must not matter, which is what "maximum" means and what "last writer wins" would
        // fail. Drawing the weak mark second must leave the strong one alone.
        using var bmp = new SKBitmap(40, 20, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        using var blender = MakeBlender();
        using var strong = new SKPaint { Color = new SKColor(0, 0, 0, 200), IsAntialias = false, Blender = blender };
        using var weak = new SKPaint { Color = new SKColor(0, 0, 0, 60), IsAntialias = false, Blender = blender };

        canvas.DrawRect(new SKRect(0, 0, 40, 20), strong);
        canvas.DrawRect(new SKRect(0, 0, 40, 20), weak);

        Assert.Equal(200, AlphaAt(bmp, 20, 10));
    }

    [Fact]
    public void Many_overlapping_marks_still_do_not_darken()
    {
        // The case that matters: a real stroke puts about a hundred overlapping segments over each
        // pixel. Under source-over that saturates to opaque; it must not here.
        using var bmp = new SKBitmap(40, 20, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        using var blender = MakeBlender();
        using var paint = new SKPaint { Color = new SKColor(0, 0, 0, 38), IsAntialias = false, Blender = blender };

        for (int i = 0; i < 100; i++)
            canvas.DrawRect(new SKRect(0, 0, 40, 20), paint);

        Assert.Equal(38, AlphaAt(bmp, 20, 10));
    }
}
