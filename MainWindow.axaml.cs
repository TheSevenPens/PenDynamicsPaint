using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Paint;
using WinPenKit;
using WinPenKit.Avalonia;

namespace PenDynamicsPaint;

/// <summary>
/// The whole application: a document, a viewport onto it, and a pen.
/// </summary>
/// <remarks>
/// <para>
/// Split out of PenDynamicsLab, which exists to establish how the pen pipeline behaves and keeps
/// its canvases deliberately primitive for that reason. A painting application wants the opposite
/// -- a document with a size of its own, layers, per-brush settings -- and growing that inside a
/// diagnostic tool would have spent what made the tool useful.
/// </para>
/// <para>
/// What came across: the viewport mapping, the document session, the brush engine layer and the
/// stroke model. What did not: the raw-versus-processed comparison, the global pressure pipeline
/// and its settings pane, the recorder and the self tests. Those are the Lab's job and are better
/// done there. TheSevenPens/PenDynamicsLab#72 records the reasoning in full.
/// </para>
/// <para>
/// Dynamics are absent on purpose rather than missing. Pressure goes straight from the pen to the
/// brush. In a paint application a pressure curve belongs to a brush rather than to the
/// application, so inheriting the Lab's global pipeline would have meant importing the model this
/// split exists to leave behind.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    private IPenSession? _penSession;
    private IReadOnlyList<InputApi> _apis = [];
    private PaintSession _paint = null!;
    private BrushSettings _brush = BrushSettings.Default with { Size = 24 };
    private bool _fitted;

    public MainWindow()
    {
        InitializeComponent();

        // 1500 x 1000 is a working area rather than a statement about what a document is. It is
        // the first thing a document-properties dialog takes over.
        _paint = new PaintSession(1500, 1000);
        PaintView.Session = _paint;
        PaintView.UndoRequested += (_, _) => { _paint.Undo(); PaintView.Invalidate(); };
        PaintView.ClearRequested += (_, _) => { _paint.Clear(); PaintView.Invalidate(); };

        // Fitted on the first real layout pass. Fitting to a zero-sized viewport would leave the
        // document off-screen at whatever the zoom clamp allowed.
        PaintView.Host.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name != "Bounds" || _fitted || PaintView.Host.Bounds.Width <= 0) return;
            _fitted = true;
            PaintView.Fit();
        };

        SizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name != "Value") return;
            _brush = _brush with { Size = SizeSlider.Value };
            SizeLabel.Text = $"{SizeSlider.Value:F0} px";
        };
        SizeLabel.Text = $"{SizeSlider.Value:F0} px";

        DrivesCombo.ItemsSource = new[] { "Size", "Opacity" };
        DrivesCombo.SelectedIndex = 0;
        DrivesCombo.SelectionChanged += (_, _) =>
            _brush = _brush with
            {
                PressureDrives = DrivesCombo.SelectedIndex == 1
                    ? PressureControl.Opacity
                    : PressureControl.Size,
            };

        CompositingCombo.ItemsSource = new[] { "Wash", "Direct" };
        CompositingCombo.SelectedIndex = 0;
        CompositingCombo.SelectionChanged += (_, _) =>
            _paint.Compositing = CompositingCombo.SelectedIndex == 1
                ? StrokeCompositing.Direct
                : StrokeCompositing.Wash;

        _renderTimer.Tick += RenderTimer_Tick;

        Opened += (_, _) =>
        {
            PopulateApis();
            StartSession();
        };

        Closing += (_, _) =>
        {
            _renderTimer.Stop();
            _penSession?.Stop();
            _penSession?.Dispose();
            _paint.Dispose();
        };
    }

    private void PopulateApis()
    {
        _apis = AvaloniaPenApis.GetAvailable();
        ApiCombo.ItemsSource = _apis.Select(a => a.Label()).ToList();

        // Wintab's digitizer context where it exists: it is the finest of the available clocks
        // and the one a tablet actually reports through. Measured in WinPenKit -- one timestamp
        // per point at 1 ms, where the framework paths vary by four orders of magnitude.
        int preferred = _apis.ToList().FindIndex(a => a == InputApi.WintabDigitizer);
        ApiCombo.SelectedIndex = preferred >= 0 ? preferred : (_apis.Count > 0 ? 0 : -1);
        ApiCombo.SelectionChanged += (_, _) => StartSession();
    }

    private void StartSession()
    {
        if (_apis.Count == 0 || ApiCombo.SelectedIndex < 0) return;

        _renderTimer.Stop();
        _penSession?.Stop();
        _penSession?.Dispose();
        _paint.EndStroke();

        var api = _apis[ApiCombo.SelectedIndex];
        _penSession = api == InputApi.AvaloniaPointer
            ? new AvaloniaPointerSession(PaintView.Host)
            : PenSessionFactory.Create(api);

        IntPtr hwnd = TryGetPlatformHandle() is { } handle ? handle.Handle : IntPtr.Zero;

        if (_penSession.Start(hwnd) is { } error)
        {
            StatusLabel.Text = error;
            _penSession.Dispose();
            _penSession = null;
            return;
        }

        StatusLabel.Text = api.Label();
        _renderTimer.Start();
    }

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        // Before the early return. The viewport repaints for reasons that have nothing to do with
        // the pen -- a zoom, a pan, the first layout pass -- and this method gives up as soon as
        // there is nothing to drain. It costs a comparison when nothing has changed.
        PaintView.PresentIfNeeded();

        if (_penSession is null) return;

        var points = _penSession.DrainPoints();
        if (points.Length == 0) return;

        int maxPressure = _penSession.MaxPressure;
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;

        foreach (var pt in points)
        {
            // Converted by hand rather than through PointToClient, which takes a PixelPoint and so
            // forces the position onto the whole-pixel grid on the way in. Measured in the Lab:
            // quantising a hi-res stroke to whole pixels takes the median turn between segments
            // from 1.5 degrees to 11.3, because at the ~2px steps a tablet reports there are only
            // a handful of directions a segment on an integer grid can point in.
            Point clientPt;
            try
            {
                var origin = topLevel.PointToScreen(new Point(0, 0));
                double scale = topLevel.RenderScaling;
                clientPt = new Point((pt.DesktopX - origin.X) / scale,
                                     (pt.DesktopY - origin.Y) / scale);
            }
            catch
            {
                _paint.EndStroke();
                continue;
            }

            if (PaintView.Host.TranslatePoint(new Point(0, 0), topLevel) is not { } viewportOrigin)
            {
                _paint.EndStroke();
                continue;
            }

            var inViewport = new Point(clientPt.X - viewportOrigin.X, clientPt.Y - viewportOrigin.Y);
            if (!PaintView.HitTest(inViewport))
            {
                _paint.EndStroke();
                continue;
            }

            // Mapped here, at the edge, and read live rather than cached. Everything below works
            // in document coordinates and knows nothing about zoom or pan, which is what makes a
            // stroke the same set of document positions whatever the view was doing at the time.
            var (docX, docY) = PaintView.ToDocument(inViewport);

            double pressure = maxPressure > 0 ? (double)pt.Pressure / maxPressure : 0;

            // Raw and processed are the same value here. There is no pipeline between the pen and
            // the brush, and no raw comparison to make -- see the remarks on this class.
            _paint.AddSample(docX, docY, pressure, pressure, _brush,
                new PenOrientation(pt.Azimuth, pt.Altitude, pt.Twist, pt.TiltX, pt.TiltY),
                pt.TimestampMicroseconds);
        }

        PaintView.PresentIfNeeded();
    }
}
