using Avalonia;
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
            h.AddSample(new Point(i, i), rawPressure: 0.5, PenOrientation.None, processedPressure: 0.25);
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
        h.AddSample(new Point(1, 1), 0.5, PenOrientation.None, 0.25);
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
        h.AddSample(new Point(0, 0), 0.5, new PenOrientation(10, 20, 30, -5, 5), 0.25);
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

    // ── The cache decision ───────────────────────────────────────

    [Fact]
    public void AStrokeIsTaggedWithTheGenerationThatProducedIt()
    {
        var h = new StrokeHistory();
        h.NoteParamsChanged();
        h.NoteParamsChanged();
        h.BeginStroke(BrushSettings.Default, Blue);
        h.AddSample(new Point(0, 0), 0.5, PenOrientation.None, 0.25);
        h.EndStroke();

        Assert.Equal(h.ParamsVersion, h.Strokes[0].ParamsVersion);
    }

    [Fact]
    public void ChangingParamsLeavesExistingStrokesStale()
    {
        var h = WithOneStroke(out var stroke);
        Assert.Equal(h.ParamsVersion, stroke.ParamsVersion);

        h.NoteParamsChanged();

        // Staleness is a comparison, not a pass over the history — changing a curve a hundred
        // times costs a hundred increments, not a hundred walks.
        Assert.NotEqual(h.ParamsVersion, stroke.ParamsVersion);
    }

    [Fact]
    public void RecachingReplacesOutputsAndClearsStaleness()
    {
        var h = WithOneStroke(out var stroke);
        h.NoteParamsChanged();

        stroke.RecacheOutputs([0.9, 0.8, 0.7], h.ParamsVersion);

        Assert.Equal(h.ParamsVersion, stroke.ParamsVersion);
        Assert.Equal([0.9, 0.8, 0.7], stroke.Samples.Select(s => s.ProcessedPressure));
    }

    [Fact]
    public void RecachingLeavesTheAuthoritativeInputAlone()
    {
        // The whole point of the split: re-curving changes what the mark looks like, never what
        // the pen did.
        var h = WithOneStroke(out var stroke);
        stroke.RecacheOutputs([0.1, 0.2, 0.3], h.ParamsVersion);

        Assert.All(stroke.Samples, s => Assert.Equal(0.5, s.RawPressure));
        Assert.Equal(new Point(2, 2), stroke.Samples[2].Position);
    }

    [Fact]
    public void RecachingRejectsAMismatchedCount()
    {
        // A silent mismatch would misalign every sample after the gap, which draws a plausible
        // but wrong stroke — far worse than a throw.
        var h = WithOneStroke(out var stroke);
        Assert.Throws<ArgumentException>(() => stroke.RecacheOutputs([0.1], h.ParamsVersion));
    }

    // ── Undo ─────────────────────────────────────────────────────

    [Fact]
    public void RemoveLastTakesTheMostRecentStroke()
    {
        var h = new StrokeHistory();
        foreach (var size in (double[])[10, 20, 30])
        {
            h.BeginStroke(BrushSettings.Default with { Size = size }, Blue);
            h.AddSample(new Point(0, 0), 0.5, PenOrientation.None, 0.25);
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
        h.AddSample(new Point(9, 9), 0.5, PenOrientation.None, 0.25);

        h.Clear();
        h.EndStroke();   // the in-progress stroke must not resurface

        Assert.Empty(h.Strokes);
    }
}
