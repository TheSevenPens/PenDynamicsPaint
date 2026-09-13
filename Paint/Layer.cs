using SkiaSharp;

namespace PenDynamicsPaint.Paint;

/// <summary>
/// One transparent, document-sized surface in the stack, with a name, a visibility and an opacity.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Wash stroke layer is the same type.</b> A stroke in progress needs exactly what a layer
/// is -- a transparent surface the size of the document that composites into the stack at a known
/// position -- and building that twice would have meant two pieces of code that had to keep
/// agreeing about premultiplication, clearing and blending. It differs only in that it is transient
/// and is not in <see cref="PaintSession.Layers"/>.
/// </para>
/// <para>
/// Every layer is document-sized, which is the simple choice rather than the frugal one. Krita's
/// paint devices are unbounded and tiled, allocating only the tiles a mark reaches; that is the
/// right answer at large document sizes and is a substantial piece of machinery. At the sizes this
/// application works at a layer costs width * height * 4 bytes -- 6 MB at 1500 x 1000 -- which is
/// worth paying to keep compositing a blit rather than a traversal.
/// </para>
/// <para>
/// The setters are internal because changing a layer changes the picture, and the composite the
/// viewport draws is a cache of the stack. Going through <see cref="PaintSession"/> is what marks
/// that cache stale; a public setter here would be a property that silently does nothing until
/// something else happens to repaint.
/// </para>
/// </remarks>
public sealed class Layer : IDisposable
{
    /// <summary>Replay starts from these pixels, or from transparent when there are none.</summary>
    /// <remarks>
    /// Allocated only when something is baked into it, which for most layers is never. See
    /// <see cref="BakeAsBaseline"/>.
    /// </remarks>
    private SKBitmap? _baseline;

    // Held rather than built per composite: a translucent layer is drawn through this on every
    // frame of every stroke, and allocating a paint per frame per layer is allocating in the
    // middle of the one loop that has to keep up with the pen.
    private readonly SKPaint _paint = new();

    internal Layer(int id, string name, int width, int height)
    {
        Id = id;
        Name = name;
        Bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Canvas = new SKCanvas(Bitmap);
        Canvas.Clear(SKColors.Transparent);
    }

    /// <summary>
    /// Stable for the life of the layer, and never reused.
    /// </summary>
    /// <remarks>
    /// A stroke records the id rather than the index, because inserting or removing a layer
    /// renumbers every index above it and would silently reassign finished strokes to the wrong
    /// surface. See <c>Stroke.LayerId</c>.
    /// </remarks>
    public int Id { get; }

    /// <summary>What the layer is called in the panel. Carries no meaning to the renderer.</summary>
    public string Name { get; internal set; }

    /// <summary>The layer's own pixels, transparent where nothing has been drawn.</summary>
    public SKBitmap Bitmap { get; }

    /// <summary>Where marks destined for this layer are drawn.</summary>
    internal SKCanvas Canvas { get; }

    /// <summary>Hidden layers take no part in the composite and are not drawn on.</summary>
    public bool IsVisible { get; internal set; } = true;

    /// <summary>How much of the layer reaches the composite, 0 to 1.</summary>
    public double Opacity { get; internal set; } = 1.0;

    /// <summary>
    /// Draw this layer onto <paramref name="canvas"/>, honouring visibility and opacity.
    /// </summary>
    internal void DrawOnto(SKCanvas canvas)
    {
        if (!IsVisible || Opacity <= 0) return;
        DrawContentOnto(canvas, Opacity);
    }

    /// <summary>
    /// Draw the pixels at a given alpha, ignoring <see cref="IsVisible"/> and <see cref="Opacity"/>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DrawOnto"/> for the stroke in progress. A Wash stroke belongs to
    /// the layer it is being drawn on, so the layer's opacity has to apply to <b>the two of them
    /// together</b> rather than to the layer alone -- see <c>PaintSession.Composite</c>. That means
    /// drawing the layer at full alpha inside a group, which this is the entry point for.
    /// </remarks>
    internal void DrawContentOnto(SKCanvas canvas, double alpha = 1.0)
    {
        if (alpha >= 1.0)
        {
            canvas.DrawBitmap(Bitmap, 0, 0);
            return;
        }

        _paint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(alpha * 255, 0, 255));
        canvas.DrawBitmap(Bitmap, 0, 0, _paint);
    }

    /// <summary>Throw away the pixels, keeping the baseline.</summary>
    internal void ResetToBaseline()
    {
        Canvas.Clear(SKColors.Transparent);
        if (_baseline is not null) Canvas.DrawBitmap(_baseline, 0, 0);
    }

    /// <summary>
    /// Make the current pixels the point replay starts from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called when pixels enter the layer that no stroke in the history accounts for -- today only
    /// a merge down, which brings another layer's marks across. Without it, undoing a later stroke
    /// would clear the layer and replay only its own strokes, and everything merged in would
    /// vanish: a destructive result from a command that is supposed to step backwards.
    /// </para>
    /// <para>
    /// This is the mechanism <c>StrokeHistory.EvictOldestIfOverCap</c> describes as baking into
    /// the replay baseline, and it is what that cap will need when it is wired up.
    /// </para>
    /// </remarks>
    internal void BakeAsBaseline()
    {
        _baseline ??= new SKBitmap(Bitmap.Width, Bitmap.Height, SKColorType.Bgra8888,
                                   SKAlphaType.Premul);

        using var canvas = new SKCanvas(_baseline);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(Bitmap, 0, 0);
    }

    public void Dispose()
    {
        _paint.Dispose();
        _baseline?.Dispose();
        Canvas.Dispose();
        Bitmap.Dispose();
    }
}
