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
