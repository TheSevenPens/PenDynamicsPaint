using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Paint;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// What the window does with the controls on it.
/// </summary>
/// <remarks>
/// <para>
/// Everything else here builds a <c>PaintSession</c> directly and sets whatever brush, colour and
/// compositing it likes. That leaves the whole of <c>MainWindow</c> untested, and anything wrong
/// <b>between</b> the window and the session invisible however many tests pass -- a control wired
/// to nothing, a setting the window never applies, a list that does not hold what it claims.
/// </para>
/// <para>
/// It is not a hypothetical gap. The smudge brush shipped looking exactly like an ordinary brush,
/// because <c>SetStrokeColor</c> was never called anywhere in the application and every mark ever
/// made was the same dark navy: a brush that picks colour up off the canvas and lays it down again
/// can only produce the colour everything else was drawn in. The engine was right and the feature
/// was unusable by the person it was built for. See TheSevenPens/PenDynamicsPaint#16.
/// </para>
/// <para>
/// <b>The window is built and never shown.</b> Showing it fires <c>Opened</c>, which starts a pen
/// session against a real tablet stack -- the one part of this application that genuinely needs
/// hardware, and nothing these tests are about.
/// </para>
/// </remarks>
public class WindowTests
{
    /// <summary>Press a control the way a pointer would, without needing a laid-out window.</summary>
    /// <remarks>
    /// Raised rather than clicked at a coordinate, because an unshown window has never had a layout
    /// pass and every control in it is a zero-sized box at the origin. The handler under test is the
    /// same one either way: the question is whether the control is wired to anything, not whether it
    /// is in the right place.
    /// </remarks>
    private static void Press(Control control)
    {
        var pointer = new Pointer(1, PointerType.Mouse, isPrimary: true);

        control.RaiseEvent(new PointerPressedEventArgs(
            control, pointer, control, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                                       PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
    }

    [Fact]
    public void The_window_starts_with_an_ink_chosen()
    {
        // The fault this file exists for. Nothing ever called SetStrokeColor, so the session kept
        // its own default for the life of the application and every brush drew the same colour.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();

            Assert.Equal(MainWindow.InkChoices[0].Colour, window.Session.StrokeColor);

            // And one swatch is marked as the chosen one. The colour alone does not settle it: the
            // first ink is the same dark navy the session already defaults to, so a window that
            // chose nothing at all still reports that colour. Only the mark says a choice was made.
            var chosen = window.Swatches
                .Count(s => s.BorderBrush is SolidColorBrush { Color.A: 255 });

            Assert.Equal(1, chosen);
        });
    }

    [Fact]
    public void Choosing_an_ink_changes_what_the_next_stroke_is_drawn_in()
    {
        // Every colour on offer, not just one. A swatch row is exactly the sort of thing that gets
        // wired up in a loop with the index captured wrongly, and then every swatch picks the last.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();

            for (int i = 0; i < window.Swatches.Count; i++)
            {
                Press(window.Swatches[i]);

                Assert.Equal(MainWindow.InkChoices[i].Colour, window.Session.StrokeColor);
            }
        });
    }

    [Fact]
    public void The_ink_a_swatch_shows_is_the_ink_it_chooses()
    {
        // A swatch is a coloured square and a promise about what pressing it will do. Both come
        // from one table here so this is cheap to keep, and a palette that lies about itself is
        // worse than no palette.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();

            for (int i = 0; i < window.Swatches.Count; i++)
            {
                Press(window.Swatches[i]);

                var shown = Assert.IsType<SolidColorBrush>(window.Swatches[i].Background);
                var used = window.Session.StrokeColor;

                Assert.Equal(shown.Color.R, used.Red);
                Assert.Equal(shown.Color.G, used.Green);
                Assert.Equal(shown.Color.B, used.Blue);
            }
        });
    }

    [Fact]
    public void The_picker_offers_every_brush_the_library_defines()
    {
        // A brush added to the library and not to the picker is invisible, and the library is where
        // someone adding one would stop.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();

            var combo = window.GetControl<ComboBox>("BrushCombo");
            var offered = Assert.IsAssignableFrom<IEnumerable<string>>(combo.ItemsSource).ToList();

            Assert.Equal(BrushLibrary.Defaults.Select(b => b.Name), offered);
        });
    }

    [Fact]
    public void Choosing_a_brush_changes_the_one_strokes_are_drawn_with()
    {
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            var combo = window.GetControl<ComboBox>("BrushCombo");

            for (int i = 0; i < BrushLibrary.Defaults.Count; i++)
            {
                combo.SelectedIndex = i;

                Assert.Equal(BrushLibrary.Defaults[i].Name, window.CurrentBrush.Name);
                Assert.Equal(BrushLibrary.Defaults[i].Engine, window.CurrentBrush.Engine);
            }
        });
    }

    [Fact]
    public void The_compositing_choice_reaches_the_brush()
    {
        // A brush setting now rather than a document one. It sat on the document because it
        // decides how a stroke reaches the layer rather than what the mark looks like, which is
        // the same question whichever brush drew it -- but in use a marker wants its overlaps
        // flattened and a dry-media brush wants them to build up.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            var combo = window.GetControl<ComboBox>("CompositingCombo");

            combo.SelectedIndex = 1;
            Assert.Equal(StrokeCompositing.Direct, window.CurrentBrush.Compositing);

            combo.SelectedIndex = 0;
            Assert.Equal(StrokeCompositing.Wash, window.CurrentBrush.Compositing);
        });
    }

    [Fact]
    public void The_compositing_box_follows_the_brush_that_is_chosen()
    {
        // The other direction, and the half that breaks when a setting moves onto the brush: the
        // control has to be re-read every time the brush changes, or it goes on showing whatever
        // the last brush wanted and the next stroke quietly disagrees with the panel.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            var brushes = window.GetControl<ComboBox>("BrushCombo");
            var combo = window.GetControl<ComboBox>("CompositingCombo");

            for (int i = 0; i < BrushLibrary.Defaults.Count; i++)
            {
                brushes.SelectedIndex = i;

                var expected = BrushLibrary.Defaults[i].Compositing == StrokeCompositing.Direct ? 1 : 0;
                Assert.Equal(expected, combo.SelectedIndex);
            }
        });
    }

    [Fact]
    public void Opening_a_document_keeps_the_ink_that_was_chosen()
    {
        // Opening a file replaces the session outright, so everything the window had told the old
        // one has to be said again to the new one. The ink belongs to the application rather than
        // to the file, and a document opened onto a fresh session starts on that session's own
        // default -- which is the same fault as never setting it, one layer along.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();

            int green = MainWindow.InkChoices
                .Select((ink, index) => (ink, index))
                .First(x => x.ink.Name == "Green").index;

            Press(window.Swatches[green]);

            window.AdoptDocument(new PaintSession(400, 300), from: null);

            Assert.Equal(MainWindow.InkChoices[green].Colour, window.Session.StrokeColor);
        });
    }

    [Fact]
    public void A_stroke_drawn_through_the_window_comes_out_in_the_ink_it_is_showing()
    {
        // The two settings together, ending in pixels. Each check above says a control reached the
        // session; this says the session then paints what the window is showing, which is the claim
        // the person drawing actually cares about.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();

            var brushes = window.GetControl<ComboBox>("BrushCombo");
            brushes.SelectedIndex = BrushLibrary.Defaults
                .Select((brush, index) => (brush, index))
                .First(x => x.brush.Name == "Marker").index;

            int red = MainWindow.InkChoices
                .Select((ink, index) => (ink, index))
                .First(x => x.ink.Name == "Red").index;

            Press(window.Swatches[red]);

            var session = window.Session;
            for (int i = 0; i < 40; i++)
                session.AddSample(200 + i * 6, 300, 0.9, window.CurrentBrush);
            session.EndStroke();

            var painted = session.Bitmap.GetPixel(320, 300);

            Assert.True(painted != SKColors.White, "the stroke drew nothing at all");

            // The Marker is translucent, so the mark is red over white paper rather than the ink
            // itself. What matters is that it is made of that colour and not of some other one.
            Assert.True(painted.Red > painted.Blue + 40 && painted.Red > painted.Green + 40,
                $"the stroke came out {painted}, which is not the red the window was showing");
        });
    }
}
