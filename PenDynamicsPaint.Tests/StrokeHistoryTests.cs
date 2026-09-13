using PenDynamicsPaint.Drawing;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// Pins the stroke record and the input-authoritative / output-cached decision behind it.
/// </summary>
/// <remarks>
/// <see cref="DrawingSession"/> itself needs Avalonia controls and cannot be tested here, but the
/// model it records into has no UI in it — which is deliberate, and is what makes the decision
/// checkable rather than merely written down.
/// </remarks>
public class StrokeHistoryTests
{
    private static readonly SKColor Blue = new(0x00, 0x00, 0xFF);

    private static StrokeHistory WithOneStroke(out Stroke recorded, int sampleCount = 3)
    {
        var h = new StrokeHistory();
        h.BeginStroke(BrushSettings.Default, Blue);
        for (int i = 0; i < sampleCount; i++)
            h.AddSample(new DocumentPoint(i, i), rawPressure: 0.5, PenOrientation.None, processedPressure: 0.25);
        h.EndStroke();
        recorded = h.Strokes[^1];
        return h;
    }

    [Fact]
    public void AStrokeRemembersTheStateItWasDrawnUnder()
    {
        var brush = BrushSettings.Default with { Size = 77 };
        var h = new StrokeHistory();
        h.BeginStroke(brush, Blue);
        h.AddSample(new DocumentPoint(1, 1), 0.5, PenOrientation.None, 0.25);
        h.EndStroke();

        var s = h.Strokes[0];
        // Snapshots, not references to live state: a later brush change must not rewrite history.
        Assert.Equal(77, s.Brush.Size);
        Assert.Equal(Blue, s.Color);
    }

    [Fact]
    public void OrientationIsRecordedEvenThoughNothingRendersIt()
    {
        // Recorded from day one so the first tilt feature is not also a format migration.
        var h = new StrokeHistory();
        h.BeginStroke(BrushSettings.Default, Blue);
        h.AddSample(new DocumentPoint(0, 0), 0.5, new PenOrientation(10, 20, 30, -5, 5), 0.25);
        h.EndStroke();

        var o = h.Strokes[0].Samples[0].Orientation;
        Assert.Equal(30, o.Twist);
        Assert.Equal(-5, o.TiltX);
    }

    [Fact]
    public void AnEmptyStrokeIsNotKept()
    {
        // Otherwise undo would appear to do nothing while it removed an invisible entry.
        var h = new StrokeHistory();
        h.BeginStroke(BrushSettings.Default, Blue);
        h.EndStroke();

        Assert.Empty(h.Strokes);
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void EndingTwiceDoesNotDuplicate()
    {
        // ResetStroke and the zero-pressure path can both end the same stroke.
        var h = WithOneStroke(out _);
        h.EndStroke();
        Assert.Single(h.Strokes);
    }

    // ── Undo ─────────────────────────────────────────────────────

    [Fact]
    public void RemoveLastTakesTheMostRecentStroke()
    {
        var h = new StrokeHistory();
        foreach (var size in (double[])[10, 20, 30])
        {
            h.BeginStroke(BrushSettings.Default with { Size = size }, Blue);
            h.AddSample(new DocumentPoint(0, 0), 0.5, PenOrientation.None, 0.25);
            h.EndStroke();
        }

        Assert.True(h.RemoveLast());
        Assert.Equal([10, 20], h.Strokes.Select(s => s.Brush.Size));
    }

    [Fact]
    public void RemoveLastOnAnEmptyHistorySaysSo()
    {
        Assert.False(new StrokeHistory().RemoveLast());
    }

    [Fact]
    public void ClearForgetsCompletedAndInProgressStrokesAlike()
    {
        var h = WithOneStroke(out _);
        h.BeginStroke(BrushSettings.Default, Blue);
        h.AddSample(new DocumentPoint(9, 9), 0.5, PenOrientation.None, 0.25);

        h.Clear();
        h.EndStroke();   // the in-progress stroke must not resurface

        Assert.Empty(h.Strokes);
    }
}
