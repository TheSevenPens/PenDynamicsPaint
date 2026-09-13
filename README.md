# PenDynamicsPaint

A pen-first painting application: a document, a viewport onto it, and brush engines that place
marks the way real paint applications do.

Windows, .NET 10, Avalonia, SkiaSharp. Pen input comes from
[WinPenKit](https://github.com/TheSevenPens/WinPenKit) next door.

## Why this is a separate repository

It began as a tab in [PenDynamicsLab](https://github.com/TheSevenPens/PenDynamicsLab) and was split
out before it grew.

The Lab exists to establish how the pen pipeline behaves — what a tablet reports, what each backend
does to it, and where precision is lost. Its canvases are deliberately primitive, and that is the
point: their value comes from having few variables, and its naive renderer is a **reference
implementation** whose crudeness is worth being able to see.

A painting application wants the opposite. A document with a size of its own, zoom and pan, layers,
per-brush settings, dab spacing, textured brushes. Growing all of that inside a diagnostic tool
would have spent the thing that made the tool useful, and most of the work would have ended up
being about the painting rather than about the pen.

So the two are apart. The Lab keeps the pipeline honest; this builds on top of it.

## What is here so far

- a document with a size of its own, behind a viewport with zoom and pan
- pen positions mapped into document coordinates, with the mapping read live so a pan or zoom
  part way through a stroke reaches the very next sample
- **layers**: add, delete, reorder, hide, set opacity, merge down
- **brushes**: a library of them, each carrying its own engine, size, spacing, opacity, pressure
  target, pressure curve and smoothing
- **smoothing**: Krita's distance-weighted filter, with separate reaches for position and
  pressure, applied before any mark is placed
- **two brush engines**: an antialiased taper swept between two round ends, and round dabs stamped
  at a distance interval
- **two ways of compositing a stroke**: Wash, where the stroke composites into a layer of its own
  and merges once, and Direct, where each mark composites as it is drawn
- stroke history with undo

Brush size is in **document units**, so a 40 unit brush covers 80 screen pixels at 200%.

### Dab spacing

Krita's isotropic rule, verified against `KDE/krita` at `1e6586cb`: with `s` the spacing wanted and
`a` the distance carried since the last mark, the next mark falls `max(0.5, s) - a` further on, and
the accumulator carries across pen samples. The property that buys is **segmentation invariance** --
the same path puts marks in the same places however it was cut into segments, so a stroke looks the
same drawn slowly as drawn quickly.

Spacing is a fraction of the mark's own diameter, so pressure drives size and spacing together.

### Brushes

A brush is a record, and a stroke keeps a copy of the one that drew it. That is what makes an undo
faithful: replay uses the engine, size, spacing and curve the stroke was actually made with rather
than whatever is selected now. The engine is named on the brush rather than held as an instance,
because a stroke has to be able to record it.

The pressure curve is three numbers -- a range and an exponent:

```
t = clamp((pressure - Start) / (End - Start), 0, 1)
output = pow(t, Exponent)
```

`Start` removes the dead weight at the bottom of a tablet's range, `End` lets the brush saturate
without bottoming the nib out, and `Exponent` decides whether it comes on early or holds light
until you lean on it. An arbitrary spline is the general case and is not here; it belongs with the
rest of the dynamics work, where nine inputs are mapped this way rather than one.

Editing a brush changes what you draw next, not what is already on the canvas.

The brush panel is on the left. It is a column of one-line rows -- label, control, value -- with
the pressure curve and smoothing folded into sections that show their values on the header, so a
folded section still says what it holds. At rest it is about 290 px tall against 770 for the same
settings laid out with each label above its slider, which matters on a tablet where the panel is
the thing competing with the canvas for height.

Brushes are not saved. The opening library is built in code and lives for the session.

### Smoothing

Krita's weighted smoothing, ported from `KDE/krita` at `75315b18`. Each incoming value is replaced
by a weighted mean of the recent path, the weight of a sample falling off as a Gaussian in the
**distance travelled** back to it:

```
sigma  = reach / 3
weight = (1 / (sqrt(2*pi) * sigma)) * exp(-d^2 / (2 * sigma^2))
```

**Position and pressure have separate reaches**, and either can run without the other. They are
different problems: a shaky hand wants its path steadied and its pressure left alone, while a noisy
sensor wants the opposite. Krita couples them -- one distance, and a switch that turns pressure
filtering on with the same reach -- which cannot express either case.

The window is measured in document units rather than in samples, which is the reason to port this
rather than write an average. A window counted in samples reaches twice as far along the path on a
tablet reporting at 200 Hz as on one reporting at 100, so the same gesture is filtered differently
on different hardware, and a slow stroke is filtered harder than a fast one along the same path.

Two things follow from how the filter is built, both deliberate:

- **It is self-limiting.** The step it measures runs from the last *smoothed* position to the new
  *raw* one, so a large deviation inflates the step, shrinks the window and softens the filtering.
  Hand tremor is flattened; a deliberate flourish is mostly left alone.
- **A stroke ends slightly short of where the pen lifted.** The filter lags and nothing runs it out
  to the last raw position. Running it out would put an unfiltered hook on the end of every stroke,
  which is more visible than the shortfall it fixes.

Smoothing belongs to the brush. It sat on the application at first, on the reasoning that it is
about the hand rather than the mark -- which is Krita's model, where it is a tool option. That is
wrong for the way brushes are actually used: a stabilised inking brush and an unfiltered sketching
brush want different answers in the same session, and leaving the setting outside the brush means
setting it again by hand every time you switch. libmypaint treats slow tracking as a brush property
for this reason.

**The filter is non-destructive.** A stroke records what the pen reported; the filtered values are
worked out on the way to the engine and not kept. Replay runs the filter again, which lands in the
same place because it is deterministic and because the stroke keeps the brush that ran it.

The opening library covers all four combinations: the ink pen filters its path only, the marker its
pressure only, the dab brush both, and the beaded brush neither.

### Layers

The stack composites to a single bitmap, rebuilt only over the region that changed. A stroke in
progress lives in a transient layer of the same type, composited **inside** its own layer's group
so that the layer's opacity applies to the two together and anything above still covers it.

Undo is chronological across the document -- the last stroke drawn goes, wherever it was drawn --
and repaints only the layer that stroke belonged to. It steps through **strokes, not commands**:
adding, deleting, reordering or merging a layer is not undoable, and a merge down is destructive,
since the merged pixels cannot be reproduced by replaying the two histories in the order they were
drawn.

## What is deliberately not here

**No general dynamics matrix.** Pressure drives size, opacity or both, through one curve. Tilt,
speed, direction and randomness as inputs, each with its own curve onto each output, is what
libmypaint brings.

**No stabilizer.** Krita's other smoothing mode -- the one that drags the brush behind the cursor on
a string -- is driven by a timer rather than by samples, emitting points while the pen is still so
the string can catch up. That needs a clock this application's input path does not have, and
approximating it from sample arrivals would make it rate-dependent in exactly the way the weighted
filter is not.

**No curve fitting between samples.** Krita also paints a Bezier through consecutive points rather
than a straight segment, which is a second kind of smoothing and a more visible one. It changes
what a segment is, so it belongs with the brush engines rather than with the filter.

**No raw-versus-processed comparison.** That is the Lab's signature feature and it does not
translate: with per-brush dynamics there is no single processed stream for a raw one to be compared
against.

**No global pressure pipeline.** In a paint application a pressure curve belongs to a brush, not to
the application. Pressure currently goes straight from the pen to the brush; dynamics will arrive
attached to brushes rather than to a settings pane.

**No recorder, no self tests.** Those belong to the Lab, which is where questions about what the
pen reported are answered.

## Running it

```
dotnet run
```

Expects a `WinPenKit` checkout beside this one — the project references it directly, the same way
PenDynamicsLab does.

Pick the pen API from the bar at the top. It opens on Wintab's digitizer context where a tablet
offers one, since that is the finest clock available and the path a tablet actually reports
through. Synthetic pen injection never reaches Wintab, so automated testing needs one of the
framework backends.

Wheel zooms about the pointer, middle-drag pans, and the viewport's own controls cover fit and
actual size. Right-click the canvas for undo and clear.

## Tests

```
dotnet test
```

The viewport mapping is held down by tests rather than by drawing and looking, because a mapping
error of half a pixel is invisible on screen and ruinous in a recording. Two properties in
particular: zoom moves the view and never the ink, and a pan mid-stroke shifts the document under
the pen by exactly the pan.
