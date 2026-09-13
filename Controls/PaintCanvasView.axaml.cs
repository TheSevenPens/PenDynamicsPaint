using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PenDynamicsPaint.Paint;
using SkiaSharp;

namespace PenDynamicsPaint.Controls;

/// <summary>
/// A window onto a <see cref="PaintSession"/>'s document, with zoom and pan.
/// </summary>
/// <remarks>
/// <para>
/// The stroke tabs present a bitmap that <i>is</i> the canvas, sized to this control. Here the
/// document has a size of its own and this shows a region of it, so there are two bitmaps rather
/// than one: the document, which the session owns, and the viewport's, which this owns and redraws
/// whenever the document or the view changes.
/// </para>
/// <para>
/// The zoom controls sit on the viewport rather than in the left settings pane because they are
/// view state, not document state. Panning and zooming change nothing about what has been drawn.
/// </para>
/// </remarks>
public partial class PaintCanvasView : UserControl
{
    /// <summary>What surrounds the document. Distinct from the paper so its edge is visible.</summary>
    private static readonly SKColor Backdrop = new(0x3A, 0x38, 0x40);

    /// <summary>A hairline around the document, so its bounds read even against white paper.</summary>
    private static readonly SKColor DocumentEdge = new(0x20, 0x1E, 0x26);

    private const double WheelZoomStep = 1.12;

    private WriteableBitmap? _avBitmap;
    private SKBitmap? _skBitmap;
    private SKCanvas? _skCanvas;
    private int _pixelWidth, _pixelHeight;
    private double _scale = 1;

    private bool _panning;
    private Point _panFrom;

    /// <summary>Where the document is and how big, in this viewport.</summary>
    public PaintViewport Viewport { get; } = new();

    /// <summary>The document being shown. Set by the window once the session exists.</summary>
    public PaintSession? Session { get; set; }

    /// <summary>Raised when the user asks to undo, from the context menu.</summary>
    public event EventHandler? UndoRequested;

    /// <summary>Raised when the user asks to clear the document.</summary>
    public event EventHandler? ClearRequested;

    public PaintCanvasView()
    {
        InitializeComponent();

        // A resize changes what the viewport shows without touching the document or the view
        // state, so neither dirty flag would catch it and the stale bitmap would keep being
        // presented at the old size -- visible as the document not filling a window that just grew.
        ViewportHost.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Bounds") Invalidate();
        };

        ViewportHost.PointerWheelChanged += OnWheel;
        ViewportHost.PointerPressed += OnPointerPressed;
        ViewportHost.PointerMoved += OnPointerMoved;
        ViewportHost.PointerReleased += OnPointerReleased;
    }

    /// <summary>The element pen positions are measured against.</summary>
    public Border Host => ViewportHost;

    /// <summary>
    /// A point in this control's coordinates as a document position.
    /// </summary>
    /// <remarks>
    /// The one place the viewport's mapping meets the pen. Read live rather than cached: a pan or
    /// a zoom part way through a stroke has to reach the very next sample, which is the case a
    /// cached origin gets wrong -- see <c>PaintViewportTests</c>.
    /// </remarks>
    public (double X, double Y) ToDocument(Point inHost) => Viewport.ToDocument(inHost.X, inHost.Y);

    /// <summary>True when the given point in this control's coordinates is over the viewport.</summary>
    public bool HitTest(Point inHost) =>
        inHost.X >= 0 && inHost.Y >= 0 &&
        inHost.X < ViewportHost.Bounds.Width && inHost.Y < ViewportHost.Bounds.Height;

    /// <summary>Fit the document in the viewport, as an opening view.</summary>
    public void Fit()
    {
        if (Session is not { } s) return;
        Viewport.FitToViewport(s.Width, s.Height, ViewportHost.Bounds.Width, ViewportHost.Bounds.Height);
        Invalidate();
    }

    /// <summary>Redraw the viewport from the document.</summary>
    /// <remarks>
    /// Called on every render tick. It returns immediately unless the document has changed or the
    /// view has moved, so an idle Paint tab costs a comparison rather than a full blit.
    /// </remarks>
    public void PresentIfNeeded()
    {
        if (Session is not { } s) return;
        if (!_viewDirty && !s.IsDirty) return;

        Render(s);
        s.ClearDirty();
        _viewDirty = false;
    }

    private bool _viewDirty = true;

    /// <summary>Mark the view as needing a redraw, without touching the document.</summary>
    public void Invalidate()
    {
        _viewDirty = true;
        UpdateLabels();
    }

    private void Render(PaintSession session)
    {
        double scale = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        double dipW = ViewportHost.Bounds.Width, dipH = ViewportHost.Bounds.Height;
        if (dipW <= 0 || dipH <= 0) return;

        EnsureBitmaps(dipW, dipH, scale);
        if (_skCanvas is null || _skBitmap is null || _avBitmap is null) return;

        _skCanvas.Clear(Backdrop);

        // Document units -> physical pixels: the zoom, then the display's own scaling.
        float s = (float)(Viewport.Zoom * scale);
        _skCanvas.Save();
        _skCanvas.Translate((float)(-Viewport.PanX * s), (float)(-Viewport.PanY * s));
        _skCanvas.Scale(s);

        // Nearest above 1:1, so magnifying shows the document's real pixels rather than a smoothed
        // guess at them. This is a lab: a stroke's actual rasterisation is the thing being looked
        // at. Below 1:1 linear, because point-sampling a minified image is just aliasing.
        var sampling = Viewport.Zoom >= 1.0
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        // Through an SKImage because that is the overload that takes sampling options. Only
        // built when a render actually happens, and renders only happen when something changed.
        using (var image = SKImage.FromBitmap(session.Bitmap))
            _skCanvas.DrawImage(image, 0f, 0f, sampling);

        // A washed stroke lives in a layer of its own until it ends, so without this it would
        // appear only when the pen lifted. Drawn with ordinary source-over, which is what the
        // merge will do to it: what is on screen mid-stroke is what will be on the document.
        if (session.ActiveStrokeLayer is { } layer)
        {
            using var inProgress = SKImage.FromBitmap(layer);
            _skCanvas.DrawImage(inProgress, 0f, 0f, sampling);
        }

        using (var edge = new SKPaint
        {
            Color = DocumentEdge,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f / s,
            IsAntialias = true,
        })
        {
            _skCanvas.DrawRect(0, 0, session.Width, session.Height, edge);
        }

        _skCanvas.Restore();

        CopyToAvBitmap();
        ViewportImage.InvalidateVisual();
    }

    private void EnsureBitmaps(double dipW, double dipH, double scale)
    {
        int w = Math.Max(1, (int)Math.Ceiling(dipW * scale));
        int h = Math.Max(1, (int)Math.Ceiling(dipH * scale));

        if (_skBitmap is not null && w == _pixelWidth && h == _pixelHeight && scale == _scale) return;

        _skCanvas?.Dispose();
        _skBitmap?.Dispose();

        _skBitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        _skCanvas = new SKCanvas(_skBitmap);
        _pixelWidth = w;
        _pixelHeight = h;
        _scale = scale;

        var old = _avBitmap;
        _avBitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                        PixelFormat.Bgra8888, AlphaFormat.Premul);

        // Unlike the stroke tabs, this bitmap tracks the control rather than the drawing, so its
        // DIP size is simply pixels / scale and Stretch=Fill is again an identity mapping.
        ViewportImage.Source = _avBitmap;
        ViewportImage.Width = w / scale;
        ViewportImage.Height = h / scale;
        old?.Dispose();
    }

    private void CopyToAvBitmap()
    {
        if (_skBitmap is null || _avBitmap is null) return;
        using var fb = _avBitmap.Lock();

        int srcStride = _skBitmap.RowBytes;
        int dstStride = fb.RowBytes;
        int rowBytes = _pixelWidth * 4;

        // Row by row rather than one block: the two bitmaps are the same size but need not have
        // the same stride, and assuming they do is how a copy ends up sheared.
        unsafe
        {
            byte* src = (byte*)_skBitmap.GetPixels();
            byte* dst = (byte*)fb.Address;
            for (int y = 0; y < _pixelHeight; y++)
                Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride,
                                  dstStride, rowBytes);
        }
    }

    // ── View input ───────────────────────────────────────────────

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var p = e.GetPosition(ViewportHost);
        double factor = e.Delta.Y > 0 ? WheelZoomStep : 1 / WheelZoomStep;
        Viewport.ZoomAt(factor, p.X, p.Y);
        Invalidate();
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(ViewportHost).Properties;

        // Middle drag pans. The pen's buttons are left alone: this surface is for drawing with,
        // and a pen that panned when it was meant to draw would be worse than no panning at all.
        if (!props.IsMiddleButtonPressed) return;

        _panning = true;
        _panFrom = e.GetPosition(ViewportHost);
        e.Pointer.Capture(ViewportHost);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning) return;

        var now = e.GetPosition(ViewportHost);
        Viewport.PanByViewport(now.X - _panFrom.X, now.Y - _panFrom.Y);
        _panFrom = now;
        Invalidate();
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ZoomIn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ZoomAboutCentre(1.25);

    private void ZoomOut_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ZoomAboutCentre(1 / 1.25);

    private void ZoomAboutCentre(double factor)
    {
        Viewport.ZoomAt(factor, ViewportHost.Bounds.Width / 2, ViewportHost.Bounds.Height / 2);
        Invalidate();
    }

    private void ZoomReset_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Session is not { } s) return;

        // Actual size, still centred on whatever was in the middle, so "100%" does not also throw
        // away where you were looking.
        var (cx, cy) = Viewport.ToDocument(ViewportHost.Bounds.Width / 2, ViewportHost.Bounds.Height / 2);
        Viewport.Zoom = 1.0;
        Viewport.CentreOn(cx, cy, ViewportHost.Bounds.Width, ViewportHost.Bounds.Height);
        Invalidate();
    }

    private void Fit_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Fit();

    private void Undo_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => UndoRequested?.Invoke(this, EventArgs.Empty);

    private void Clear_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ClearRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateLabels()
    {
        ZoomLabel.Text = $"{Viewport.Zoom * 100:F0}%";
        DocLabel.Text = Session is { } s ? $"{s.Width} x {s.Height}" : "";
    }
}
