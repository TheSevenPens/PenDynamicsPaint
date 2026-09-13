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
- one brush engine: an antialiased taper between two round ends
- stroke history with undo

Brush size is in **document units**, so a 40 unit brush covers 80 screen pixels at 200%.

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
