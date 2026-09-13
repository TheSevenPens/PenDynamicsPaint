using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Paint;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// The stack itself, and what undo does once there is more than one surface to undo on.
/// </summary>
/// <remarks>
/// <para>
/// Undo is chronological across the document -- the last stroke drawn goes, wherever it was drawn
/// -- and repaints only the layer that stroke belonged to. Both halves matter: repainting the
/// active layer instead would erase work on a layer the user had merely switched away from.
/// </para>
/// <para>
/// The cases that carry real risk are the ones where pixels and history can drift apart: a layer
/// removed while its strokes stay behind, and a merge that puts pixels on a layer no stroke
/// accounts for. Either one turns undo from limited into destructive.
/// </para>
/// </remarks>
public class LayerStackTests
{
    private static readonly SKColor Red = new(0xFF, 0x00, 0x00);
    private static readonly SKColor Blue = new(0x00, 0x00, 0xFF);

    private static void Stroke(PaintSession s, SKColor color, double y, double size = 30)
    {
        s.SetStrokeColor(color);
        var brush = BrushSettings.Default with { Size = size, PressureDrives = PressureControl.Size };

        for (double x = 20; x <= 220; x += 2) s.AddSample(x, y, 1.0, brush);
        s.EndStroke();
    }

    [Fact]
    public void A_new_document_has_one_layer_and_it_is_active()
    {
        using var session = new PaintSession(240, 200);

        Assert.Single(session.Layers);
        Assert.Equal(0, session.ActiveLayerIndex);
        Assert.Same(session.Layers[0], session.ActiveLayer);
    }

    [Fact]
    public void The_last_layer_cannot_be_removed()
    {
        // Every other operation here assumes there is somewhere for marks to go. An empty stack
        // would make ActiveLayer throw on the next pen sample.
        using var session = new PaintSession(240, 200);

        Assert.False(session.RemoveLayer(0));
        Assert.Single(session.Layers);
    }

    [Fact]
    public void A_new_layer_goes_directly_above_the_active_one_and_becomes_active()
    {
        using var session = new PaintSession(240, 200);
        var second = session.AddLayer();
        var third = session.AddLayer();

        Assert.Equal([session.Layers[0], second, third], session.Layers);

        // Back to the bottom, then add: the new one goes at index 1, not on top of the stack.
        Assert.True(session.SetActiveLayer(0));
        var inserted = session.AddLayer();

        Assert.Equal(1, session.ActiveLayerIndex);
        Assert.Same(inserted, session.Layers[1]);
        Assert.Same(third, session.Layers[3]);
    }

    [Fact]
    public void Layer_ids_are_not_reused_when_a_layer_is_removed()
    {
        // A stroke records the id of the layer it went to. Reusing one would hand a finished
        // stroke to a different surface the next time anything replayed it.
        using var session = new PaintSession(240, 200);

        var second = session.AddLayer();
        int usedId = second.Id;
        Assert.True(session.RemoveLayer(1));

        Assert.NotEqual(usedId, session.AddLayer().Id);
    }

    [Fact]
    public void A_stroke_records_the_layer_it_was_drawn_on()
    {
        using var session = new PaintSession(240, 200);
        int bottom = session.Layers[0].Id;

        Stroke(session, Red, 60);
        var top = session.AddLayer();
        Stroke(session, Blue, 140);

        Assert.Equal([bottom, top.Id], session.History.Strokes.Select(s => s.LayerId));
    }

    [Fact]
    public void A_hidden_layer_takes_no_marks()
    {
        // Refused rather than drawn invisibly: ink that lands on a hidden layer is really there
        // and appears the moment the layer is shown, which is a surprise nobody asked for.
        using var session = new PaintSession(240, 200);
        Assert.True(session.SetLayerVisible(0, false));

        Stroke(session, Red, 100);

        Assert.Empty(session.History.Strokes);
        Assert.True(session.SetLayerVisible(0, true));
        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 100));
    }

    [Fact]
    public void Undo_removes_the_last_stroke_wherever_it_was_drawn()
    {
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 60);
        session.AddLayer();
        Stroke(session, Blue, 140);

        // Switch back to the bottom layer first: an undo that repainted the active layer rather
        // than the stroke's own would erase the red line and leave the blue one.
        Assert.True(session.SetActiveLayer(0));
        Assert.True(session.Undo());

        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 140));
        Assert.Equal(Red, session.Bitmap.GetPixel(120, 60));
    }

    [Fact]
    public void Deleting_a_layer_takes_its_strokes_with_it()
    {
        // Otherwise the next undo removes a stroke whose pixels are already gone, and the user
        // presses undo and watches nothing happen.
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 60);
        session.AddLayer();
        Stroke(session, Blue, 140);

        Assert.True(session.RemoveLayer(1));
        Assert.Single(session.History.Strokes);

        Assert.True(session.Undo());
        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 60));
    }

    [Fact]
    public void Merging_down_keeps_the_pixels_and_drops_the_layer()
    {
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 60);
        session.AddLayer();
        Stroke(session, Blue, 140);

        Assert.True(session.MergeDown(1));

        Assert.Single(session.Layers);
        Assert.Equal(0, session.ActiveLayerIndex);
        Assert.Equal(Red, session.Bitmap.GetPixel(120, 60));
        Assert.Equal(Blue, session.Bitmap.GetPixel(120, 140));
    }

    [Fact]
    public void A_stroke_after_a_merge_undoes_without_erasing_the_merge()
    {
        // The case the baseline exists for. Undo clears the layer and replays its strokes, and
        // the merged pixels belong to no stroke: without a baseline to start from they would
        // vanish, so an undo of one later stroke would destroy everything merged earlier.
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 60);
        session.AddLayer();
        Stroke(session, Blue, 140);
        Assert.True(session.MergeDown(1));

        Stroke(session, Red, 100);
        Assert.True(session.Undo());

        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 100));
        Assert.Equal(Red, session.Bitmap.GetPixel(120, 60));
        Assert.Equal(Blue, session.Bitmap.GetPixel(120, 140));
    }

    [Fact]
    public void Clear_empties_the_merged_pixels_too()
    {
        // Clear means clear. Resetting to the baseline instead would leave merged marks behind
        // with nothing left in the document able to remove them.
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 60);
        session.AddLayer();
        Stroke(session, Blue, 140);
        Assert.True(session.MergeDown(1));

        session.Clear();

        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 60));
        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 140));
    }

    [Fact]
    public void The_history_stops_growing_and_keeps_what_it_drops()
    {
        // The caps bound a long session. Until they were wired up they were dead code and the
        // history grew for as long as the application ran.
        using var session = new PaintSession(600, 240);
        var brush = BrushSettings.Default with { Size = 6, PressureDrives = PressureControl.Size };

        // The first stroke sits on its own, away from the rest, so it can be identified later.
        session.AddSample(20, 30, 1.0, brush);
        session.AddSample(60, 30, 1.0, brush);
        session.EndStroke();

        // Enough more to push it past the cap.
        for (int i = 0; i < StrokeHistory.MaxStrokes; i++)
        {
            double x = 20 + i % 500;
            session.AddSample(x, 150, 1.0, brush);
            session.AddSample(x + 4, 150, 1.0, brush);
            session.EndStroke();
        }

        Assert.Equal(StrokeHistory.MaxStrokes, session.History.Strokes.Count);
        Assert.DoesNotContain(session.History.Strokes, s => s.Samples[0].Position.Y == 30);

        // Evicted from the history, still on the canvas.
        Assert.NotEqual(SKColors.White, session.Bitmap.GetPixel(40, 30));

        // And it has to survive an undo, which clears the layer to its baseline and replays what
        // is retained. Dropping an evicted stroke without baking it in would erase it here --
        // work the user can still see, destroyed by a command that steps backwards.
        Assert.True(session.Undo());
        Assert.NotEqual(SKColors.White, session.Bitmap.GetPixel(40, 30));
    }

    [Fact]
    public void An_undo_after_an_eviction_does_not_draw_anything_twice()
    {
        // The baseline must hold the evicted strokes and nothing else. Bake the whole layer into
        // it instead -- or start it from the layer's pixels -- and the strokes still in the history
        // are in there as well, so replay lays them down a second time.
        //
        // The brush is translucent for that reason. Drawing an opaque stroke twice looks exactly
        // like drawing it once, and this fault would sit unnoticed until someone painted in ink
        // that stacks.
        using var session = new PaintSession(600, 260);
        var brush = BrushSettings.Default with
        {
            Size = 30,
            Opacity = 0.4,
            PressureDrives = PressureControl.Size,
        };

        // A band of overlapping strokes, more than the cap holds, so the oldest are evicted.
        for (int i = 0; i <= StrokeHistory.MaxStrokes; i++)
        {
            session.AddSample(260 + i % 40, 100, 1.0, brush);
            session.AddSample(300 + i % 40, 100, 1.0, brush);
            session.EndStroke();
        }

        // One more, well away from the band, so undoing it cannot change the band itself.
        session.AddSample(100, 210, 1.0, brush);
        session.AddSample(200, 210, 1.0, brush);
        session.EndStroke();

        using var before = session.Bitmap.Copy();
        Assert.True(session.Undo());

        int changed = 0;
        for (int y = 60; y < 150; y++)
            for (int x = 0; x < session.Width; x++)
                if (before.GetPixel(x, y) != session.Bitmap.GetPixel(x, y)) changed++;

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Clearing_a_layer_leaves_the_rest_of_the_stack_alone()
    {
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 60);
        session.AddLayer();
        Stroke(session, Blue, 140);

        session.ClearActiveLayer();

        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 140));
        Assert.Equal(Red, session.Bitmap.GetPixel(120, 60));
        Assert.Equal(2, session.Layers.Count);
    }

    [Fact]
    public void Clearing_a_layer_takes_its_strokes_with_it()
    {
        // Otherwise the next undo removes a stroke whose pixels are already gone, and the user
        // presses undo and watches nothing happen -- the same reasoning as deleting a layer, which
        // this is the non-destructive half of.
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 60);
        session.AddLayer();
        Stroke(session, Blue, 140);

        session.ClearActiveLayer();
        Assert.Single(session.History.Strokes);

        Assert.True(session.Undo());
        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 60));
    }

    [Fact]
    public void Clearing_a_layer_drops_its_baseline_too()
    {
        // The lesson Clear had to learn: wiping only the visible pixels leaves the baseline
        // holding whatever a merge baked in, and the next undo resets to it.
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 60);
        session.AddLayer();
        Stroke(session, Blue, 140);
        Assert.True(session.MergeDown(1));

        session.ClearActiveLayer();
        Stroke(session, Red, 100);
        Assert.True(session.Undo());

        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 60));
        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 140));
        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 100));
    }

    [Fact]
    public void Clear_really_drops_the_merged_pixels_rather_than_hiding_them()
    {
        // Clear wipes what is on screen, but the merged pixels also live in the layer's replay
        // baseline. Leave that behind and the next undo resets to it, and work the user cleared
        // comes back on its own.
        using var session = new PaintSession(240, 200);

        Stroke(session, Red, 60);
        session.AddLayer();
        Stroke(session, Blue, 140);
        Assert.True(session.MergeDown(1));

        session.Clear();
        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 60));

        // Draw something new and step back off it. The cleared work must stay gone.
        Stroke(session, Red, 100);
        Assert.True(session.Undo());

        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 60));
        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 140));
        Assert.Equal(SKColors.White, session.Bitmap.GetPixel(120, 100));
    }

    [Fact]
    public void Switching_layer_ends_the_stroke_in_progress()
    {
        // Half a stroke on each layer would leave history describing neither.
        using var session = new PaintSession(240, 200);
        session.AddLayer();

        var brush = BrushSettings.Default with { Size = 30, PressureDrives = PressureControl.Size };
        session.SetStrokeColor(Red);
        for (double x = 20; x <= 120; x += 2) session.AddSample(x, 100, 1.0, brush);

        Assert.True(session.SetActiveLayer(0));
        Assert.Single(session.History.Strokes);

        // The rest of the gesture is a new stroke, on the layer it started on.
        for (double x = 122; x <= 220; x += 2) session.AddSample(x, 100, 1.0, brush);
        session.EndStroke();

        Assert.Equal(2, session.History.Strokes.Count);
        Assert.Equal(session.Layers[1].Id, session.History.Strokes[0].LayerId);
        Assert.Equal(session.Layers[0].Id, session.History.Strokes[1].LayerId);
    }
}
