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

        for (double x = 20; x <= 220; x += 2) s.AddSample(x, y, 1.0, 1.0, brush);
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
    public void Switching_layer_ends_the_stroke_in_progress()
    {
        // Half a stroke on each layer would leave history describing neither.
        using var session = new PaintSession(240, 200);
        session.AddLayer();

        var brush = BrushSettings.Default with { Size = 30, PressureDrives = PressureControl.Size };
        session.SetStrokeColor(Red);
        for (double x = 20; x <= 120; x += 2) session.AddSample(x, 100, 1.0, 1.0, brush);

        Assert.True(session.SetActiveLayer(0));
        Assert.Single(session.History.Strokes);

        // The rest of the gesture is a new stroke, on the layer it started on.
        for (double x = 122; x <= 220; x += 2) session.AddSample(x, 100, 1.0, 1.0, brush);
        session.EndStroke();

        Assert.Equal(2, session.History.Strokes.Count);
        Assert.Equal(session.Layers[1].Id, session.History.Strokes[0].LayerId);
        Assert.Equal(session.Layers[0].Id, session.History.Strokes[1].LayerId);
    }
}
