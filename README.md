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

**What reaches the mark:** radius, opacity, hardness, spacing, the pile-up correction, elliptical
dabs, the HSV colour shifts, smudge, and the two random offsets. **What does not:** the HSL colour
pair, tracking, and the eraser.

The colour shifts are worked out per dab and driven by inputs like any other setting, so a brush can
colour a stroke by what the pen is doing rather than by what was picked. Hue is added and **wraps**,
so a brush driving it from something that goes round comes back to where it started; value is added
and clamps. Saturation is the odd one -- libmypaint scales its shift by the saturation already
there, so **grey ink cannot be given a colour this way** however hard the setting is driven.

Elliptical dabs are what make a chisel nib: `elliptical_dab_ratio` keeps the dab's radius on its
long axis and divides the short one by the ratio, so raising it narrows the nib rather than
enlarging it, and `elliptical_dab_angle` turns it. The calligraphy brush is a 5.46:1 nib at 46
degrees, and draws a hairline along that diagonal and a broad stroke across it.

The part worth knowing is that **an elliptical dab measures spacing in its own metric**. libmypaint
stretches the step by the aspect ratio across the narrow axis before counting dabs into it, so a
nib dragged sideways lays them closer together than the same nib drawn along its length. That is
the difference between a nib and an oval stamp, and getting it wrong leaves gaps in exactly the
strokes a calligraphy brush is for.

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

### Smudging

The one thing in the engine that **reads** the canvas rather than only writing to it. A smudging
dab takes its colour from what is already there: at `smudge` 1 it lays no ink of its own at all and
only moves paint around.

Two colours are kept, not one. What is read off the canvas is blended into a running colour, and it
is that running colour the dab is painted with -- which is what makes a smudge a smear with a length
rather than a copy of the pixel underneath. `smudge_length` sets how long the brush holds on to what
it picked up.

**A stroke that reads the canvas paints straight onto the layer**, skipping the stroke layer Wash
otherwise gives it. In that layer the only thing to find would be the marks it had just made, so it
would drag its own colour along and never touch the painting. libmypaint has no such intermediate
for the same reason.

**It reads the layer as it stood when the stroke began**, not as the stroke is changing it. A brush
reading the live layer picks up the paint the dab before it just laid and tops itself back up, so
the colour never runs out and a smudge carries on to the edge of the canvas at the same strength.
Reading what was already there makes the paint on the brush finite, and the trail then fades because
it is running out.

**The reading is taken from the half-disc behind the dab**, not from a disc around it. A whole disc
reaches as far in front of the brush as behind, so a dab still short of a mark already overlaps it,
picks its colour up and lays it down there -- paint moving backwards, against the stroke. This is a
deliberate departure from libmypaint, which has that bleed; Krita's smudge does not, and paint
dragged the way the brush is moving is what someone using one expects.

Half a disc rather than a whole one shifted back, which is worse: shifted by its own radius the
reading sits entirely on ground the dab has left, so crossing a mark it reads the blank canvas
behind and wipes the mark out instead of spreading it.

Between them these two make `smudge_length` the only thing deciding how far paint travels, so it
runs high -- below about 0.7 nothing goes more than a dab or two.

**A smudge moves paint rather than adding it**, and that needs its own compositing. Painting a dab
over the canvas can only ever put more paint down, so a smudge built on source-over copies its
colour onward for as long as the stroke lasts and the mark it came from never loses anything. The
canvas is pulled *towards* the dab instead: where the brush is carrying less paint than the canvas
holds, the canvas ends up with less. That is what makes a mark spread thinner instead of being
duplicated, and what makes a trail fade as the paint runs out. Skia has no such blend mode, so it is
a runtime blender like `AlphaDarken`, ported from `draw_dab_pixels_BlendMode_Normal_and_Eraser`.

The dab's colour is divided by that target alpha and the blend multiplies it back. Doing only the
division leaves every dab too bright; doing neither leaves it too dark.

Ported from the **legacy** path of `update_smudge_color` and `apply_smudge`. The other path mixes
through libmypaint's spectral pigment model, which is a much larger piece of work and a different
question from whether paint moves at all.

### Filtering tilt

**There is a brush in the opening set for looking at this**, called Tilt testing, and it is a
diagnostic rather than something to draw with. Width is the obvious readout for how steady tilt is
and a poor one: it is tangled up with pressure and with whatever texture the brush has, so on a
charcoal that already breaks up you cannot tell a wobble in the tilt from the brush doing its job.
Tilt testing lays a flat even line and puts the pen's **bearing on the hue** instead, with nothing
else moving -- so any change along a stroke is the orientation and can be nothing else, and the eye
picks out a flicker in colour immediately. Its own tilt reach starts at zero, because the point is
to move that slider and watch the colour settle.


Smoothing has a third reach, for the pen's orientation, independent of the other two -- a brush
driven by tilt wants it steadied whether or not the line needed help. It was passed through
untouched until the MyPaint engine gave tilt somewhere to go, which meant the filter was steadying
the path and the pressure while the third channel wobbled straight through.

Tilt arrives dirtier than position does. Tablets quantise it coarsely, often to whole degrees, so a
brush mapping declination onto radius turns each step into a visible step in the mark.

**The two angles that wrap are not averaged.** Azimuth and twist run 0 to 360, and the mean of 359
and 1 is 180 -- pointing the opposite way. Both are averaged as unit vectors and turned back into
an angle, the same thing the stroke's direction input already does.

**An upright pen's azimuth is not trusted.** Near vertical the azimuth is the pole of a spherical
coordinate: a millimetre of wobble swings it through tens of degrees. Each sample's azimuth is
weighted by the sine of how far the pen is leaning, so readings taken upright count for almost
nothing instead of dragging the answer around. A stroke drawn entirely upright leaves nothing to
take an angle from, and then the pen's own reading is returned rather than a number invented out of
an empty sum.

Not a port: libmypaint does not filter tilt and Krita's smoothing does not reach it, so there is no
upstream behaviour to match -- only the same weighting the other two channels use, applied to a
signal that needs it more.

## Saving

The document is saved as **OpenRaster** (`.ora`), a real format rather than one invented here: a
zip holding one PNG per layer and a `stack.xml` describing the stack. It is what MyPaint saves, and
Krita and GIMP read it, so a document written here opens in the applications whose brushes are
driving it. `Export` writes a flattened PNG instead, for anything that wants a picture rather than
a document.

Written to version 0.0.3 of the specification. Three parts of that exist for readers other than
this one, and nothing here would notice them missing, so they are tested against the file rather
than through a round trip: `mimetype` first in the archive and stored uncompressed, so a reader can
identify the file from its leading bytes; a full-size `mergedimage.png`, which is what lets a viewer
show the image without understanding layers; and a thumbnail no larger than 256 square.

The same goes for the order of the stack. OpenRaster lists layers **top first** and this
application's stack has index 0 at the bottom, so the two are reversed on the way in and out.
Getting that backwards passes every round trip -- it inverts twice and cancels -- while producing a
file that opens upside down everywhere else.

**The undo history is not saved.** A loaded layer's pixels are baked as the replay baseline, so an
undo after opening a file steps back through that session's own strokes and stops rather than
erasing work that came from disk. Saving the history would mean saving the brushes that drew it,
which is a format decision of its own.

A document that asks for something unimplemented -- a layer group, a composite mode other than
normal -- still opens, and what went unused is reported in the status line. The same bargain the
brush loader makes.

## What is deliberately not here

**No general dynamics matrix for the two native engines.** Pressure drives size, opacity or both,
through one curve. The MyPaint engine has the full matrix -- ten inputs, each with its own curve
onto each honoured setting -- so the open question is whether the other two should grow the same
thing or simply be reached through a brush file.

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
