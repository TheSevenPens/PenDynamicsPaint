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
- **curve fitting**: a cubic through the samples, so the ink between them follows an arc rather
  than a chord
- **MyPaint brushes**: `.myb` files, with settings decided per dab from nine inputs including tilt,
  speed and direction
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

### Curve fitting

A tablet reports every few document units, so joining the samples with chords draws a polygon, and
the corners show on anything drawn quickly. Krita's Bezier interpolation, ported from `KDE/krita`
at `75315b18`, fits a cubic through each pair of samples with tangents taken from their neighbours.

This is a **second** kind of smoothing and a more visible one than filtering the samples: the filter
decides where the samples are, this decides the path between them. They are independent, and either
can run without the other.

The fitted curve is flattened into short straight pieces before it reaches a brush engine, so
`IBrushEngine` still takes two samples at a time and neither engine had to change. The dab engine
is indifferent to the subdivision because its spacing rule carries an accumulator across calls and
does not care how the path was cut up -- a property pinned when that engine was written, and this
is what it buys.

Two limits, both Krita's and both kept:

- **It lags one sample.** The tangent at a sample is a central difference through its neighbours,
  so the segment ending at a sample cannot be drawn until the next one arrives. The stroke's last
  segment is flushed when the pen lifts.
- **The first and last segments are only approximated.** They have no neighbour outside the stroke
  to take a tangent from, so they use the single chord they have, which points a half-angle off the
  true tangent. Measured on a twelve-point circle: the interior strays 0.08 units from the arc
  against the polygon's 4.05, while the two end segments stray 2.5.

One adaptation. Krita divides each tangent by the time elapsed across it, making its magnitude a
speed; here they are divided by the number of sample intervals instead, making them a distance per
sample. Not every backend supplies a usable clock, and a divisor that silently collapsed to one
would make the first tangent count double.

### MyPaint brushes

The brush model from [libmypaint](https://github.com/mypaint/libmypaint), ported to C# at `v1.6.1`,
driving the existing dab engine. `.myb` files load, and a brush's settings are decided **per dab**
from nine inputs: pressure, two smoothed speeds, a random value, how far into the stroke the pen is,
direction of travel, tilt declination and ascension, and barrel rotation.

Two rules carry most of the behaviour and neither is the obvious one:

- an input's curve **adds** to a setting's base value rather than scaling it, so several inputs can
  pull one setting at once and cancel
- beyond its outermost control points a curve **extrapolates** along its terminal segment rather
  than clamping, so a pressure curve drawn over 0..1 still has an opinion about a tablet that
  reports 1.4

Dabs fade by libmypaint's own profile: two straight lines in the *square* of the normalised radius,
which is a curve in the radius and so is sampled into a gradient rather than handed over as two
stops.

**What reaches the mark:** radius, opacity, hardness, spacing, the pile-up correction, and the two
random offsets. **What does not:** elliptical dabs, smudge, colour dynamics, tracking, the custom
input, and the eraser.

The pile-up correction is `opaque_linearize`, and it is worth calling out because its default is
0.9 rather than 0 -- so it applies to nearly every brush file whether or not the file mentions it.
What the opacity settings state is the opacity the *stroke* should reach, not the opacity of one
dab, and a brush lays several dabs over every pixel; without the correction the stroke overshoots
by that pile. The airbrush asks for 52% and lays 11.5 dabs per pixel, so it arrived as a solid
black slab with no pressure response until this was put in. A brush file that
leans on any of those still loads, and the panel says how many settings went unused rather than
letting the shortfall pass unnoticed.

**Not a binding.** libmypaint is ISC licensed and binding it is a real option, but on Windows it
needs a C toolchain, vcpkg, and builds of glib and json-c before the library itself — and the engine
then becomes a black box, which is the opposite of what this pair of repositories is for. The model
is small enough to port and the port is what can be instrumented.

**Time is the weak point.** Speed is distance per second and the pen's own clock is what makes that
meaningful; where a backend reports none, a nominal interval stands in, which makes speed a function
of the report rate rather than of the hand.

### Framework independence

`PenDynamicsPaint.Drawing` and `PenDynamicsPaint.Paint` name no UI framework type. Positions are
`DocumentPoint`, a pair of doubles, rather than `Avalonia.Point`; the conversion to Skia's float
`SKPoint` happens where a mark is actually drawn, which is once, at the end.

Doubles rather than floats because a position arrives from the viewport's mapping already computed
in double and then goes through the filter, the fitter and the spacing walk before it becomes a
pixel. Narrowing at the front of that narrows before the arithmetic rather than after it.

Skia is not excluded and the difference is worth stating: it is the raster backend, the thing that
turns geometry into pixels, and an engine that could not name a canvas would have nothing to draw
on. A UI framework is a way of getting a window, which none of this needs.

The rule is checked rather than written down. `FrameworkIndependenceTests` walks every type in those
namespaces -- base classes, interfaces, fields, properties, parameters, returns and generic
arguments -- and fails if any of them mentions one. A convention nothing checks lasts until the next
hurry, and one `using` is all it would take.

The stronger version is a project boundary: move those namespaces into an assembly that does not
reference Avalonia and let the compiler refuse. That is a restructure rather than a test, and worth
doing; this holds the line until then.

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

**No arc-length parameterisation.** The pen's readings are spread along a fitted segment linearly in
the curve parameter rather than by distance. The two differ only where the control handles are very
uneven, and by less than the pen's own resolution.

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
