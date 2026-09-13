namespace PenDynamicsPaint.Paint;

/// <summary>
/// Where the document is, and how big, as seen through the Paint tab's viewport.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole difference between the Paint surface and the stroke tabs. There, the control
/// <i>is</i> the canvas: <c>DrawSurface</c> sizes its bitmap to the host's bounds, and a position
/// in the control is a position in the drawing. Here a document has a size of its own and the
/// viewport shows some region of it at some magnification, so a position in the control means
/// nothing until it has been through this.
/// </para>
/// <para>
/// <b>Deliberately free of Avalonia and of display scaling.</b> Zoom is document units per DIP,
/// and pan is in document units; turning DIPs into physical pixels is the presenter's job and
/// belongs with the bitmap, not here. Keeping this to doubles is what makes the mapping testable
/// without a window, and what lets it travel if the Paint surface ever moves to another host --
/// see issues 72 and 74.
/// </para>
/// <para>
/// No rotation. That is a decision rather than an omission: rotation would make the mapping a
/// matrix and the inverse a matrix inverse, and nothing here needs it yet.
/// </para>
/// </remarks>
public sealed class PaintViewport
{
    /// <summary>Smallest and largest magnification. Beyond these the mapping is still correct and
    /// the result is useless, so they are a usability bound rather than a numerical one.</summary>
    public const double MinZoom = 0.05;

    /// <inheritdoc cref="MinZoom"/>
    public const double MaxZoom = 32.0;

    private double _zoom = 1.0;

    /// <summary>DIPs per document unit. 1.0 shows the document at its own size.</summary>
    public double Zoom
    {
        get => _zoom;
        set => _zoom = Math.Clamp(double.IsFinite(value) ? value : 1.0, MinZoom, MaxZoom);
    }

    /// <summary>The document coordinate shown at the viewport's top-left corner.</summary>
    public double PanX { get; set; }

    /// <inheritdoc cref="PanX"/>
    public double PanY { get; set; }

    /// <summary>A position in the viewport, in DIPs from its top-left, as a document position.</summary>
    public (double X, double Y) ToDocument(double viewportX, double viewportY)
        => (viewportX / _zoom + PanX, viewportY / _zoom + PanY);

    /// <summary>A document position as a position in the viewport, in DIPs from its top-left.</summary>
    public (double X, double Y) ToViewport(double documentX, double documentY)
        => ((documentX - PanX) * _zoom, (documentY - PanY) * _zoom);

    /// <summary>
    /// Magnify by <paramref name="factor"/>, keeping whatever document position is currently under
    /// (<paramref name="viewportX"/>, <paramref name="viewportY"/>) under it afterwards.
    /// </summary>
    /// <remarks>
    /// Zooming about the pointer rather than about a corner is what makes a wheel feel like it is
    /// moving the paper rather than resizing it. The anchor is why this is a method and not a
    /// setter: the pan that keeps the document still has to be derived from the zoom that moved
    /// it, and doing that at the call site is how the two get out of step.
    /// </remarks>
    public void ZoomAt(double factor, double viewportX, double viewportY)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;

        var (docX, docY) = ToDocument(viewportX, viewportY);

        double before = _zoom;
        Zoom = _zoom * factor;
        if (_zoom == before) return;   // clamped; leaving the pan alone keeps the anchor honest

        PanX = docX - viewportX / _zoom;
        PanY = docY - viewportY / _zoom;
    }

    /// <summary>Scroll by a viewport-space delta in DIPs, as a drag would.</summary>
    /// <remarks>
    /// Dragging the paper left moves the view right, so a positive delta here is the pointer's
    /// movement and the pan goes the other way.
    /// </remarks>
    public void PanByViewport(double deltaX, double deltaY)
    {
        PanX -= deltaX / _zoom;
        PanY -= deltaY / _zoom;
    }

    /// <summary>
    /// Fit a document of the given size inside a viewport of the given size, and centre it.
    /// </summary>
    /// <remarks>
    /// Margin is a fraction of the viewport left clear on the tighter axis, so the document does
    /// not sit edge to edge with the window.
    /// </remarks>
    public void FitToViewport(double documentWidth, double documentHeight,
                              double viewportWidth, double viewportHeight, double margin = 0.04)
    {
        if (documentWidth <= 0 || documentHeight <= 0) return;
        if (viewportWidth <= 0 || viewportHeight <= 0) return;

        double usable = Math.Clamp(1.0 - margin * 2, 0.1, 1.0);
        Zoom = Math.Min(viewportWidth / documentWidth, viewportHeight / documentHeight) * usable;

        CentreOn(documentWidth / 2, documentHeight / 2, viewportWidth, viewportHeight);
    }

    /// <summary>Put a document position at the centre of the viewport.</summary>
    public void CentreOn(double documentX, double documentY, double viewportWidth, double viewportHeight)
    {
        PanX = documentX - viewportWidth / 2 / _zoom;
        PanY = documentY - viewportHeight / 2 / _zoom;
    }
}
