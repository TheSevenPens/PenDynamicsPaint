using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Paint;
using WinPenKit;
using WinPenKit.Diagnostics;
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

    /// <summary>Click a button the way releasing the pointer over it would.</summary>
    /// <remarks>
    /// Not the same as <see cref="Press"/>. A button raises Click when the pointer is released
    /// over it, not when it goes down, so pressing one does nothing at all -- which is how the
    /// first version of the preset test below came to report that Soft and Default gave the same
    /// answer. The swatches are different: they handle the press itself.
    /// </remarks>
    private static void ClickButton(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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
    public void The_options_dialog_opens_and_offers_the_backends_it_was_given()
    {
        // It crashed on being opened. The window defined its own InitializeComponent, which shadows
        // the one Avalonia generates -- and the generated one is what assigns the fields behind
        // x:Name, so every control in the dialog was null and the first line to touch one threw.
        //
        // MainWindow does not define its own, which is why only the new window broke and why
        // nothing else in the application showed it. A window that throws on construction is the
        // cheapest possible thing to test and there was no test that built one.
        OnTheUiThread.Run(() =>
        {
            var backends = new[] { InputApi.AvaloniaPointer, InputApi.WintabDigitizer };

            var dialog = new OptionsWindow(backends, InputApi.WintabDigitizer);

            var combo = dialog.GetControl<ComboBox>("ApiCombo");
            var offered = Assert.IsAssignableFrom<IEnumerable<string>>(combo.ItemsSource).ToList();

            Assert.Equal(backends.Select(a => a.Label()), offered);

            // Opened on what is already in use, so that closing it without touching anything
            // cannot change the backend.
            Assert.Equal(1, combo.SelectedIndex);

            // And nothing is chosen until it is: the caller acts on this, so a dialog that
            // answered before being answered would restart the pen session on every open.
            Assert.Null(dialog.Chosen);
        });
    }

    [Fact]
    public void Every_window_in_the_application_can_be_built()
    {
        // The general form of the fault above. Constructing a window runs its XAML, wires its
        // controls and runs whatever the constructor does with them, and any of that can throw --
        // which reaches the user as the application vanishing rather than as a message.
        OnTheUiThread.Run(() =>
        {
            Assert.NotNull(new MainWindow());
            Assert.NotNull(new OptionsWindow());
        });
    }

    [Fact]
    public void Hovering_or_pressing_a_slider_does_not_move_the_panel()
    {
        // Reported: clicking a size or opacity slider nudged it, and everything under it, down the
        // panel. The thumb grew on hover, and the rows here are as tall as their content, so the
        // row asked for two more pixels and took the rest of the column with it -- the control
        // moved under the pointer that was reaching for it.
        //
        // Measured rather than looked at, because two pixels is exactly the size of fault that
        // survives a screenshot. Any state a control can be in has to leave it asking for the same
        // room as every other state.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            window.Measure(new Size(1400, 900));
            window.Arrange(new Rect(0, 0, 1400, 900));

            // Measured as the reported symptom: does something below the sliders move. Measuring a
            // slider on its own with a made-up constraint says nothing, because the template's
            // height does not follow the thumb -- the first version of this did that and passed
            // against the very fault it was written for.
            var below = window.GetControl<Button>("SmoothingSection");
            double restingTop = below.Bounds.Y;

            Assert.True(restingTop > 0, "the panel has not been laid out");

            // The thumbs on the panel. The ones in the fly-outs are not built until a fly-out
            // opens, so there is no thumb to reach -- but they are the same control under the same
            // style, and it is the style that does this.
            foreach (var name in new[] { "SizeSlider", "BrushOpacitySlider" })
            {
                var slider = window.GetControl<Slider>(name);

                // The thumb inside the template, not the slider: the style that caused this selects
                // Thumb:pointerover, which is the thumb's own state.
                var thumb = slider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
                Assert.True(thumb is not null, $"{name} has no thumb to hover");

                foreach (var state in new[] { ":pointerover", ":pressed" })
                {
                    ((IPseudoClasses)thumb!.Classes).Set(state, true);

                    window.Measure(new Size(1400, 900));
                    window.Arrange(new Rect(0, 0, 1400, 900));

                    double now = below.Bounds.Y;

                    ((IPseudoClasses)thumb.Classes).Set(state, false);

                    window.Measure(new Size(1400, 900));
                    window.Arrange(new Rect(0, 0, 1400, 900));

                    Assert.True(Math.Abs(now - restingTop) < 0.01,
                        $"{state} on {name} moved the panel below it from {restingTop} to {now}");
                }
            }
        });
    }

    [Fact]
    public void The_new_document_dialog_offers_sizes_and_starts_on_2K()
    {
        OnTheUiThread.Run(() =>
        {
            var dialog = new NewDocumentWindow();

            var list = dialog.GetControl<ListBox>("SizeList");
            var offered = Assert.IsAssignableFrom<IEnumerable<string>>(list.ItemsSource).ToList();

            Assert.Equal(NewDocumentWindow.Sizes.Select(s => s.Name), offered);

            // 2K first and selected: the commonest screen, and a new document four times the area
            // of the screen it will be looked at on is a surprise rather than a convenience.
            Assert.Equal(0, list.SelectedIndex);
            Assert.Equal((1920, 1080),
                         (NewDocumentWindow.Sizes[0].Width, NewDocumentWindow.Sizes[0].Height));

            // Nothing chosen until it is, so closing it changes no document.
            Assert.Null(dialog.Chosen);
        });
    }

    /// <summary>Open the pressure curve flyout and hand back the plot inside it.</summary>
    /// <remarks>
    /// The only test here that needs a shown window, and it needs one for a specific reason: a
    /// pointer event carries its position relative to a root visual, and translating that into the
    /// coordinates of a control -- which is what the handler under test does -- gives nothing at
    /// all for a control that has never been attached to a root. Every other test raises events on
    /// controls whose position does not matter and leaves the window unshown.
    /// </remarks>
    private static CurvePlotUnderTest OpenCurvePlot(MainWindow window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var section = window.GetControl<Button>("CurveSection");
        section.Flyout!.ShowAt(section);
        Dispatcher.UIThread.RunJobs();

        var plot = window.GetControl<Canvas>("CurvePlot");
        var root = (Visual?)TopLevel.GetTopLevel(plot);

        Assert.True(root is not null, "The flyout did not open, so the plot has no root.");
        Assert.True(plot.Bounds.Width > 1 && plot.Bounds.Height > 1,
                    $"The plot was not laid out: {plot.Bounds}");

        return new CurvePlotUnderTest(plot, root!);
    }

    private sealed record CurvePlotUnderTest(Canvas Plot, Visual Root)
    {
        public double Width => Plot.Bounds.Width;

        public double Height => Plot.Bounds.Height;

        /// <summary>Press at one point in the plot and drag to another.</summary>
        public void Drag(Point from, Point to)
        {
            var pointer = new Pointer(1, PointerType.Mouse, isPrimary: true);
            var held = new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                                                  PointerUpdateKind.LeftButtonPressed);

            // Events carry a position in the root's coordinates, so the points are stated in the
            // plot's and converted here. Stating them the other way round would mean every test
            // knowing where the flyout happened to open.
            Point InRoot(Point p) => Plot.TranslatePoint(p, Root) ?? p;

            Plot.RaiseEvent(new PointerPressedEventArgs(
                Plot, pointer, Root, InRoot(from), 0, held, KeyModifiers.None));

            Plot.RaiseEvent(new PointerEventArgs(
                InputElement.PointerMovedEvent, Plot, pointer, Root, InRoot(to), 0, held,
                KeyModifiers.None));

            Plot.RaiseEvent(new PointerReleasedEventArgs(
                Plot, pointer, Root, InRoot(to), 0,
                new PointerPointProperties(RawInputModifiers.None,
                                           PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));
        }

        /// <summary>
        /// The corner where the pen reads nothing and the brush does nothing, which is where the
        /// Start node sits on a curve whose range has not been narrowed.
        /// </summary>
        /// <remarks>
        /// 6 is the margin the plot leaves around the unit square, so a node at an extreme is not
        /// half outside the border. Aiming is all it is for: a node is caught from 22px away, so
        /// being a pixel or two out still finds it.
        /// </remarks>
        public Point BottomLeft => new(6, Height - 6);

        public Point TopRight => new(Width - 6, 6);

        /// <summary>The middle: pressure 0.5 and output 0.5, whatever the margin is.</summary>
        /// <remarks>
        /// Deliberately the one point that can be named without repeating the mapping under test.
        /// A test that worked out where 0.4 lands would agree with a broken mapping that put it in
        /// the same wrong place; the centre of a symmetric mapping is the centre however it scales.
        /// </remarks>
        public Point Centre => new(Width / 2, Height / 2);

        /// <summary>Half way across the plot, and <paramref name="fraction"/> of the way up it.</summary>
        /// <remarks>
        /// Horizontally the centre, because that is the one position that can be named without
        /// repeating the mapping. Vertically wherever is asked for, and for the two nodes that
        /// read the horizontal that matters: dragged to the exact centre, a node reading the
        /// wrong axis lands on the same answer as one reading the right one, and a test that only
        /// ever aims there cannot tell them apart.
        /// </remarks>
        public Point Above(double fraction) => new(Width / 2, Height * (1 - fraction));
    }

    /// <summary>Type a value into a text box and commit it the way Enter does.</summary>
    private static void TypeInto(TextBox box, string text)
    {
        box.Text = text;
        box.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = box,
            Key = Key.Enter,
        });
    }

    [Fact]
    public void Dragging_the_start_node_moves_where_the_response_begins()
    {
        // Three sliders were three numbers to solve for. The nodes sit on the curve itself, so the
        // thing dragged and the thing changed are the same object -- but only if the press finds
        // the node and the drag is read on the right axis. Nothing else in the application would
        // notice if it read the wrong one: the plot would still draw a curve, just not the one
        // that was asked for.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            ClickButton(window.GetControl<Button>("CurveDefaultButton"));

            var plot = OpenCurvePlot(window);
            var was = window.CurrentBrush.Curve;

            plot.Drag(plot.BottomLeft, plot.Above(0.75));

            var now = window.CurrentBrush.Curve;

            // Half way across, and three quarters of the way up. Start is where along the pen's
            // range the response begins, so only the first of those two numbers is an answer to
            // it: a node reading the height would land on 0.75.
            Assert.Equal(0.5, now.Start, 2);

            // And only Start. The node is dragged to the middle of the plot, which is half way up
            // as well as half way across: an End that read the same drag would have moved too.
            Assert.Equal(was.End, now.End, 6);
            Assert.Equal(was.Exponent, now.Exponent, 6);
        });
    }

    [Fact]
    public void Dragging_the_end_node_moves_where_the_response_tops_out()
    {
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            ClickButton(window.GetControl<Button>("CurveDefaultButton"));

            var plot = OpenCurvePlot(window);
            var was = window.CurrentBrush.Curve;

            plot.Drag(plot.TopRight, plot.Above(0.25));

            var now = window.CurrentBrush.Curve;

            // Half way across, a quarter of the way up. A node reading the height gives 0.25.
            Assert.Equal(0.5, now.End, 2);
            Assert.Equal(was.Start, now.Start, 6);
            Assert.Equal(was.Exponent, now.Exponent, 6);
        });
    }

    [Fact]
    public void Dragging_the_middle_node_shapes_the_response()
    {
        // The one that earns the plot. An exponent is a number nobody can picture; the height of
        // the curve at half pressure is the thing being chosen, and dragging it is saying it
        // directly.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            ClickButton(window.GetControl<Button>("CurveDefaultButton"));

            var plot = OpenCurvePlot(window);

            // Pulled up: the brush reaches most of its width before the pen is half pressed.
            plot.Drag(plot.Centre, plot.Above(0.75));
            double soft = window.CurrentBrush.Curve.Apply(0.5);

            // Pushed down: it holds off instead. Grabbed from three quarters up, which is where
            // the drag above left it -- a node that does not move to where it was dragged is
            // grabbed once and then lost, and this is the drag that would find that.
            plot.Drag(plot.Above(0.75), plot.Above(0.25));
            double hard = window.CurrentBrush.Curve.Apply(0.5);

            Assert.True(soft > 0.5, $"Dragging up gave {soft:F2} at half pressure");
            Assert.True(hard < 0.5, $"Dragging down gave {hard:F2} at half pressure");

            // Back to the middle is back to a straight line, and the node has to be findable at
            // its new height to get there -- a node drawn in the wrong place is grabbed once and
            // then lost.
            plot.Drag(plot.Above(0.25), plot.Centre);

            Assert.Equal(1.0, window.CurrentBrush.Curve.Exponent, 2);
        });
    }

    [Fact]
    public void The_two_ends_of_the_range_do_not_cross()
    {
        // Start past End is a curve the model does define -- a threshold, nothing below the point
        // and full strength at it -- but it is not something to arrive at by dragging one node
        // through another, which leaves a plot whose line runs backwards.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            ClickButton(window.GetControl<Button>("CurveDefaultButton"));

            var plot = OpenCurvePlot(window);

            plot.Drag(plot.BottomLeft, plot.Above(0.75));
            Assert.Equal(0.5, window.CurrentBrush.Curve.Start, 2);

            // End dragged hard to the left, well past where Start now is.
            plot.Drag(plot.TopRight, new Point(0, 0));

            var curve = window.CurrentBrush.Curve;
            Assert.True(curve.End >= curve.Start,
                        $"End {curve.End:F2} ended up below Start {curve.Start:F2}");
        });
    }

    [Fact]
    public void A_press_on_empty_plot_moves_nothing()
    {
        // A plot that jumps when it is clicked is worse than one that waits to be aimed at: there
        // is no reading of a press in the middle of nowhere that is not a guess at which of three
        // nodes was wanted.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            ClickButton(window.GetControl<Button>("CurveDefaultButton"));

            var plot = OpenCurvePlot(window);
            var was = window.CurrentBrush.Curve;

            // The top left corner, which on a default curve is nowhere near any of the three.
            // The drag then goes somewhere that would change the curve if a node had been taken:
            // aimed at the centre it would not, because the centre is where the bend already is.
            var empty = new Point(10, 10);
            plot.Drag(empty, plot.Above(0.25));

            Assert.Equal(was, window.CurrentBrush.Curve);

            // And the plot is something a pointer can land on at all. Every test here raises
            // events on the canvas directly, which skips hit-testing: a Canvas with no brush is
            // not hit-tested, and on one of those a node could only be caught by hitting the 13px
            // ellipse itself. Nothing else in this file would notice.
            //
            // Asserted on the brush rather than through InputHitTest, which is what this was
            // first. That walked a visual tree inside a flyout inside a shown window, and the
            // answer turned out to depend on which other tests had run first: it passed alone and
            // returned null once three more tests had each shown a window of their own. The brush
            // is the whole of what decides it.
            Assert.NotNull(plot.Plot.Background);
        });
    }

    [Fact]
    public void The_numbers_under_the_plot_can_be_typed_into()
    {
        // Dragging is for finding a shape; typing is for setting an exact one, or copying a value
        // from somewhere else. Both have to reach the same brush, and the boxes have to end up
        // showing what the brush made of what was typed rather than what was typed.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            var exponent = window.GetControl<TextBox>("CurveExponentBox");
            var start = window.GetControl<TextBox>("CurveStartBox");

            TypeInto(exponent, "2.5");
            Assert.Equal(2.5, window.CurrentBrush.Curve.Exponent, 6);

            TypeInto(start, "0.3");
            Assert.Equal(0.3, window.CurrentBrush.Curve.Start, 6);

            // Out of range is clamped by the brush, and the box then says what the brush says --
            // not what was asked for. A box left showing 40 for a curve of 8 is a lie about the
            // brush that will draw the next stroke.
            TypeInto(exponent, "40");
            Assert.Equal(8.0, window.CurrentBrush.Curve.Exponent, 6);
            Assert.Equal("8.00", exponent.Text);

            // Nonsense is not an edit.
            TypeInto(start, "wide");
            Assert.Equal(0.3, window.CurrentBrush.Curve.Start, 6);
            Assert.Equal("0.30", start.Text);
        });
    }

    [Fact]
    public void The_curve_presets_change_the_brush_they_are_pressed_for()
    {
        // Soft has to reach full width earlier than default, and hard later. Stated as an
        // ordering rather than as three numbers, because the numbers are a judgement and the
        // ordering is the thing that would be a bug if it were wrong -- a Soft button that made
        // the brush harder is worse than one tuned to the wrong value.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();

            double WidthAtHalfPressure(string button)
            {
                ClickButton(window.GetControl<Button>(button));
                return window.CurrentBrush.Curve.Apply(0.5);
            }

            double soft = WidthAtHalfPressure("CurveSoftButton");
            double normal = WidthAtHalfPressure("CurveDefaultButton");
            double hard = WidthAtHalfPressure("CurveHardButton");

            Assert.True(soft > normal, $"Soft gave {soft:F2} at half pressure against {normal:F2}");
            Assert.True(hard < normal, $"Hard gave {hard:F2} at half pressure against {normal:F2}");

            // Default is then the straight line, which it can only be because a preset sets the
            // range too: half pressure, half the width.
            Assert.Equal(0.5, normal, 6);

            // A preset is a whole curve and somewhere you can get back to. Moving only the
            // exponent, as the first version did, leaves the button a modifier: pressing Soft
            // gives a different curve depending on what the brush was set to, and pressing it
            // twice from different starting points gives two different brushes.
            foreach (var button in new[] { "CurveSoftButton", "CurveDefaultButton", "CurveHardButton" })
            {
                ClickButton(window.GetControl<Button>(button));

                Assert.Equal(0.0, window.CurrentBrush.Curve.Start, 6);
                Assert.Equal(1.0, window.CurrentBrush.Curve.End, 6);
            }

            // The same button from two different starting points has to land in the same place,
            // which is the property "preset" actually means.
            ClickButton(window.GetControl<Button>("CurveHardButton"));
            ClickButton(window.GetControl<Button>("CurveSoftButton"));
            var fromHard = window.CurrentBrush.Curve;

            ClickButton(window.GetControl<Button>("CurveDefaultButton"));
            ClickButton(window.GetControl<Button>("CurveSoftButton"));

            Assert.Equal(fromHard, window.CurrentBrush.Curve);
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

            // Each entry says what kind of brush it is, because that is what decides which
            // settings the brush even has.
            Assert.Equal(BrushLibrary.Defaults.Count, offered.Count);

            foreach (var (brush, shown) in BrushLibrary.Defaults.Zip(offered))
            {
                Assert.StartsWith(brush.Name, shown);
                Assert.Contains(brush.Engine switch
                {
                    BrushEngineKind.Dabs => "Dabs",
                    BrushEngineKind.MyPaint => "MyPaint",
                    _ => "Taper",
                }, shown);
            }
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
    public void A_brushs_engine_cannot_be_changed_out_from_under_it()
    {
        // The engine decides which settings a brush has, so offering it as one of those settings
        // read as though any brush could be switched to any engine. It could, and the result was
        // misleading in both directions: a taper switched to MyPaint quietly became a default round
        // dab with nothing of the original in it, and a MyPaint brush switched to taper drew from
        // settings its file had never set.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();

            Assert.Null(window.FindControl<ComboBox>("EngineCombo"));
        });
    }

    [Fact]
    public void A_new_brush_is_added_rather_than_replacing_the_one_in_use()
    {
        // Making a brush used to mean turning the selected one into something else -- loading a
        // .myb overwrote whichever brush was in front of the pen, name and all. A new brush is a
        // new entry, and the one that was there is still there.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            var combo = window.GetControl<ComboBox>("BrushCombo");

            combo.SelectedIndex = 0;
            var wasFirst = window.CurrentBrush;
            int before = BrushLibrary.Defaults.Count;

            window.NewBrushForTest(BrushEngineKind.Dabs);

            var offered = Assert.IsAssignableFrom<IEnumerable<string>>(combo.ItemsSource).ToList();

            Assert.Equal(before + 1, offered.Count);
            Assert.Equal(BrushEngineKind.Dabs, window.CurrentBrush.Engine);

            // And the brush that was selected is untouched, under its own name.
            combo.SelectedIndex = 0;
            Assert.Equal(wasFirst.Name, window.CurrentBrush.Name);
            Assert.Equal(wasFirst.Engine, window.CurrentBrush.Engine);
        });
    }

    [Fact]
    public void The_button_beside_the_brush_picker_makes_brushes()
    {
        // The three ways to make a brush live in the Brush menu, which is where someone looks after
        // failing to find them. The button next to the picker is where they look first, and it is a
        // flyout wired in XAML: nothing else in the application would notice if the handler were
        // dropped from one of these items, and the button would open, offer the choice, and do
        // nothing at all.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            var button = window.GetControl<Button>("AddBrushButton");

            var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
            var items = flyout.Items.OfType<MenuItem>().ToList();

            Assert.Equal(3, items.Count);
            Assert.Contains(items, i => (i.Header as string)?.Contains("taper") == true);
            Assert.Contains(items, i => (i.Header as string)?.Contains("dabs") == true);
            Assert.Contains(items, i => (i.Header as string)?.Contains("MyPaint") == true);

            int before = BrushLibrary.Defaults.Count;

            // Choosing one adds a brush of that kind and puts it in front of the pen. Raised as a
            // click on the item because the flyout itself needs a popup to open into, which a
            // headless window does not have.
            var taper = items.First(i => (i.Header as string)!.Contains("taper"));
            taper.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            var offered = Assert.IsAssignableFrom<IEnumerable<string>>(
                window.GetControl<ComboBox>("BrushCombo").ItemsSource).ToList();

            Assert.Equal(before + 1, offered.Count);
            Assert.Equal(BrushEngineKind.Taper, window.CurrentBrush.Engine);

            var dabs = items.First(i => (i.Header as string)!.Contains("dabs"));
            dabs.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal(BrushEngineKind.Dabs, window.CurrentBrush.Engine);
        });
    }

    [Fact]
    public void A_MyPaint_brush_hides_the_settings_it_does_not_have()
    {
        // A MyPaint brush brings its own size, opacity and pressure response, all varying per dab.
        // Those rows used to be disabled, which reads as something broken or as a setting that
        // would work if only the right thing were selected. Absent reads as not applicable.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            var combo = window.GetControl<ComboBox>("BrushCombo");

            int native = BrushLibrary.Defaults
                .Select((b, i) => (b, i)).First(x => x.b.Engine == BrushEngineKind.Taper).i;
            int mypaint = BrushLibrary.Defaults
                .Select((b, i) => (b, i)).First(x => x.b.Engine == BrushEngineKind.MyPaint).i;

            combo.SelectedIndex = native;
            Assert.True(window.GetControl<Border>("SizeRow").IsVisible);
            Assert.False(window.GetControl<Border>("MyPaintRow").IsVisible);

            combo.SelectedIndex = mypaint;
            Assert.False(window.GetControl<Border>("SizeRow").IsVisible);
            Assert.False(window.GetControl<Border>("OpacityRow").IsVisible);
            Assert.False(window.GetControl<Border>("DrivesRow").IsVisible);
            Assert.False(window.GetControl<Button>("CurveSection").IsVisible);

            // And it says which file it is, in their place.
            Assert.True(window.GetControl<Border>("MyPaintRow").IsVisible);
        });
    }

    [Fact]
    public void A_tablet_that_will_not_open_still_leaves_the_canvas_drawn()
    {
        // The fault this test exists for, found by running it. The frame loop presents the
        // document as well as draining the pen, and it used to start on the last line of
        // StartSession, after two early returns. So a Wintab context that would not open did not
        // leave the application penless -- it left the canvas blank, the document never drawn,
        // and a driver's complaint in the status line of an empty window.
        //
        // Nothing else here would have noticed. Every other window test builds a window without
        // showing it, so no pen session is ever opened and no frame is ever presented.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow
            {
                PenOutcomeForTest = MainWindow.PenForTest.Refused,
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsPresenting,
                        "a pen session that would not open stopped the canvas being drawn");

            // And it says what can be done about it. The driver's own words are not actionable:
            // what Wintab says is that a fallback context failed to open, which names no cause.
            //
            // This held on a machine with a tablet driver and not on a build machine, because
            // the refusal used to read whichever driver had been chosen -- and with no tablet
            // installed that is Avalonia Pointer, whose refusal says nothing about Options,
            // since that is where it would be sending somebody who is already there. The forced
            // refusal names a tablet driver now, so the path under test is the same everywhere.
            string? said = window.GetControl<TextBlock>("StatusLabel").Text;
            Assert.Contains("Options", said);
        });
    }

    [Fact]
    public void No_tablet_driver_at_all_still_leaves_the_canvas_drawn()
    {
        // The other way out of the same method, and the same guarantee: a machine with no tablet
        // driver installed is a machine this should still open a document on.
        OnTheUiThread.Run(() =>
        {
            var asked = new List<string>();
            var window = new MainWindow
            {
                PenOutcomeForTest = MainWindow.PenForTest.NoDriver,
            };

            window.AskAboutPen = (_, error) =>
            {
                asked.Add(error);
                return Task.FromResult(PenProblemChoice.Dismiss);
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsPresenting,
                        "no driver stopped the canvas being drawn");

            // And it is said out loud, for the same reason a refused context is: a canvas that
            // will not take a stroke looks the same either way.
            Assert.Single(asked);

            window.Close();
        });
    }

    /// <summary>A window whose pen will not open, with the dialog replaced by a recorder.</summary>
    /// <remarks>
    /// <para>
    /// Shown, because the fault this is all about only happens to a window that has opened, and
    /// answered without a dialog, because a test must not open a window that waits to be
    /// dismissed.
    /// </para>
    /// <para>
    /// <b>Close it when the test is done with it.</b> The headless session is one platform shared
    /// by every test in the file, and a window left showing stays in it: the first version of
    /// these three left three, and the plot test -- which opens a flyout and hit-tests it -- began
    /// failing when it ran after them and passing when it ran alone.
    /// </para>
    /// </remarks>
    private static (MainWindow Window, List<string> Asked) RefusedPen(
        Func<int, PenProblemChoice> answer)
    {
        var asked = new List<string>();
        var window = new MainWindow { PenOutcomeForTest = MainWindow.PenForTest.Refused };

        window.AskAboutPen = (api, error) =>
        {
            asked.Add($"{api}: {error}");
            return Task.FromResult(answer(asked.Count));
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (window, asked);
    }

    [Fact]
    public void A_tablet_that_will_not_open_says_so_in_front_of_the_document()
    {
        // Twice now, a pen session that failed to open has been read as the drawing being broken.
        // It looks exactly like that: the pen moves, the canvas stays empty, and the only thing
        // that says otherwise is a line at the bottom of the window, which is not where anyone
        // looks when the thing in the middle appears not to work.
        OnTheUiThread.Run(() =>
        {
            var (window, asked) = RefusedPen(_ => PenProblemChoice.Dismiss);

            Assert.Single(asked);

            // Carrying the driver's own words, which are what anyone searching for the problem
            // will have to go on.
            Assert.Contains("refused", asked[0]);

            // Dismissed, the application is still usable for everything that is not the pen --
            // and, since this is the bug underneath, the canvas is still being drawn.
            Assert.True(window.IsPresenting);

            window.Close();
        });
    }

    [Fact]
    public void The_way_out_offered_by_the_dialog_is_taken()
    {
        // The dialog is worth having only because it is actionable. The usual cause is another
        // application holding the tablet, so one answer is to read it through Windows instead --
        // and that answer has to actually change which driver is read.
        OnTheUiThread.Run(() =>
        {
            // Once, and then let it go: the second session is refused as well, so an answer that
            // never changed would ask again forever.
            var (window, asked) = RefusedPen(
                n => n == 1 ? PenProblemChoice.UseFallback : PenProblemChoice.Dismiss);

            Dispatcher.UIThread.RunJobs();

            Assert.Equal(InputApi.AvaloniaPointer, window.ApiForTest);

            // And it asked again when that failed too, rather than going quiet on a window whose
            // pen still does not work.
            Assert.Equal(2, asked.Count);

            window.Close();
        });
    }

    [Fact]
    public void Trying_again_opens_the_session_again()
    {
        // The other answer, and the one someone reaches for after closing whatever was holding
        // the tablet. It has to restart the session rather than only close the dialog.
        OnTheUiThread.Run(() =>
        {
            var (window, asked) = RefusedPen(
                n => n <= 2 ? PenProblemChoice.Retry : PenProblemChoice.Dismiss);

            Dispatcher.UIThread.RunJobs();

            Assert.Equal(3, asked.Count);
            Assert.True(window.IsPresenting);

            window.Close();
        });
    }

    [Fact]
    public void The_dialog_does_not_offer_a_way_out_it_has_already_tried()
    {
        // Windows pen input is where the dialog sends someone whose Wintab context was taken. It
        // is also a session that can fail on its own -- an unplugged tablet does it -- and
        // offering it as the way out of itself would be a button that reopens what just failed.
        OnTheUiThread.Run(() =>
        {
            var wintab = new PenProblemWindow(InputApi.WintabDigitizer, "held", true, null);
            Assert.True(wintab.GetControl<Button>("FallbackButton").IsVisible);

            var fallback = new PenProblemWindow(InputApi.AvaloniaPointer, "nothing", true, null);
            Assert.False(fallback.GetControl<Button>("FallbackButton").IsVisible);

            // Nor one this machine has not got.
            var alone = new PenProblemWindow(InputApi.WintabDigitizer, "held", false, null);
            Assert.False(alone.GetControl<Button>("FallbackButton").IsVisible);

            // Whichever is showing, one button that is showing is the one Enter presses. The
            // visibility matters: the fallback carries IsDefault from the XAML and keeps it after
            // being hidden, so a test that only asked which button was default would be satisfied
            // by a dialog where Enter does nothing at all.
            Assert.Contains(new[] { "RetryButton", "FallbackButton" },
                            name => alone.GetControl<Button>(name)
                                is { IsDefault: true, IsVisible: true });
        });
    }

    [Fact]
    public void The_dialog_offers_the_causes_without_picking_one()
    {
        // This has guessed wrong twice. First it said another application was holding the tablet,
        // when the driver was refusing every kind of context to everyone. Then it said the driver
        // had run out, because 253 were open against a stated maximum of 32 -- which turned out
        // to be no limit at all, since opening past it worked all the way to 334. What is known
        // is that a service restart cures it, so that is what it says.
        OnTheUiThread.Run(() =>
        {
            var odd = new PenProblemWindow(InputApi.WintabDigitizer, "no", true,
                                           new WintabContextTable(253, 32));
            string? said = odd.GetControl<TextBlock>("CauseText").Text;

            // Both causes offered, neither claimed.
            Assert.Contains("Wacom Tablet Service", said);
            Assert.Contains("Clip Studio", said);

            // The count is reported, and explicitly not blamed.
            Assert.Contains("253", said);
            Assert.Contains("not why", said);

            // An ordinary count is not worth a sentence.
            var ordinary = new PenProblemWindow(InputApi.WintabDigitizer, "no", true,
                                                new WintabContextTable(4, 32));
            said = ordinary.GetControl<TextBlock>("CauseText").Text;

            Assert.DoesNotContain("killed", said);
            Assert.Contains("Wacom Tablet Service", said);

            // And a driver that would not say keeps the rest of the sentence.
            var silent = new PenProblemWindow(InputApi.WintabDigitizer, "no", true, null);
            Assert.Contains("Wacom Tablet Service",
                            silent.GetControl<TextBlock>("CauseText").Text);
        });
    }

    [Fact]
    public void The_status_line_says_when_the_pen_has_gone_and_when_it_returns()
    {
        // A context can be taken away underneath a running application -- restarting the tablet
        // service does it -- and until this existed nothing showed. The window stayed up with its
        // status line still naming the driver while the pen did nothing, which reads as a broken
        // canvas. The session gets itself back within a few seconds, but a few seconds of a dead
        // pen is long enough to go looking in the wrong place.
        OnTheUiThread.Run(() =>
        {
            var window = new MainWindow();
            var status = window.GetControl<TextBlock>("StatusLabel");

            window.ShowPenState(false);
            Assert.Contains("took the pen away", status.Text);

            window.ShowPenState(true);
            Assert.Contains("working again", status.Text);

            // Written only when the answer changes. This is asked once a frame, and the line is
            // shared with everything else the application says, so a repeat would be the last
            // word anybody ever saw.
            status.Text = "Document saved";
            window.ShowPenState(true);
            Assert.Equal("Document saved", status.Text);

            // But a real change still gets through.
            window.ShowPenState(false);
            Assert.Contains("took the pen away", status.Text);
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
