using Avalonia;
using PenDynamicsPaint.Drawing;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// Pins the history cap — the bound on what is otherwise memory that grows for as long as you
/// keep drawing.
/// </summary>
/// <remarks>
/// The caps are deliberately generous: they exist to stop a long session growing without limit,
/// not to ration undo. What matters here is that eviction is correct and observable, because the
/// caller has to bake each evicted stroke into the replay baseline before its samples are gone.
/// </remarks>
public class StrokeHistoryCapTests
{
    private static readonly SKColor Blue = new(0x00, 0x00, 0xFF);

    private static void Record(StrokeHistory h, int samples = 1)
    {
        h.BeginStroke(BrushSettings.Default, Blue);
        for (int i = 0; i < samples; i++)
            h.AddSample(new Point(i, i), 0.5, PenOrientation.None, 0.25);
        h.EndStroke();
    }

    private static int DrainEvictions(StrokeHistory h)
    {
        int n = 0;
        while (h.EvictOldestIfOverCap() is not null) n++;
        return n;
    }

    [Fact]
    public void NothingIsEvictedBelowTheCap()
    {
        var h = new StrokeHistory();
        for (int i = 0; i < 10; i++) Record(h);

        Assert.Null(h.EvictOldestIfOverCap());
        Assert.Equal(10, h.Strokes.Count);
    }

    [Fact]
    public void TheOldestGoesFirst()
    {
        var h = new StrokeHistory();
        for (int i = 0; i < StrokeHistory.MaxStrokes + 1; i++)
        {
            h.BeginStroke(BrushSettings.Default with { Size = i + 1 }, Blue);
            h.AddSample(new Point(i, i), 0.5, PenOrientation.None, 0.25);
            h.EndStroke();
        }

        var evicted = h.EvictOldestIfOverCap();

        Assert.NotNull(evicted);
        Assert.Equal(1, evicted!.Brush.Size);              // the first one recorded
        Assert.Equal(2, h.Strokes[0].Brush.Size);          // now the oldest retained
        Assert.Equal(StrokeHistory.MaxStrokes, h.Strokes.Count);
    }

    [Fact]
    public void TheEvictedStrokeIsReturnedNotDiscarded()
    {
        // The caller has to render it into the baseline. Returning it is what makes capping safe:
        // undo clears and replays, so a stroke that vanished from history without being preserved
        // would vanish from the canvas too.
        var h = new StrokeHistory();
        for (int i = 0; i < StrokeHistory.MaxStrokes + 1; i++) Record(h, samples: 3);

        var evicted = h.EvictOldestIfOverCap();

        Assert.NotNull(evicted);
        Assert.Equal(3, evicted!.Samples.Count);   // still has its samples to render from
    }

    [Fact]
    public void SampleCountCapsEvenWhenStrokeCountDoesNot()
    {
        // One continuous stroke can run for minutes at tablet report rates, so a stroke-count cap
        // alone does not bound anything.
        var h = new StrokeHistory();
        Record(h, samples: StrokeHistory.MaxSamples);
        Record(h, samples: 10);

        Assert.True(h.Strokes.Count <= StrokeHistory.MaxStrokes);
        Assert.Equal(1, DrainEvictions(h));
        Assert.Equal(10, h.TotalSamples);
    }

    [Fact]
    public void EvictionRunsUntilBackUnderTheCap()
    {
        var h = new StrokeHistory();
        for (int i = 0; i < StrokeHistory.MaxStrokes + 25; i++) Record(h);

        Assert.Equal(25, DrainEvictions(h));
        Assert.Equal(StrokeHistory.MaxStrokes, h.Strokes.Count);
    }

    // ── The sample tally has to stay honest ──────────────────────

    [Fact]
    public void UndoReleasesItsSamplesFromTheTally()
    {
        // Otherwise the tally only ever rises and the cap would evict strokes that are not
        // actually costing anything.
        var h = new StrokeHistory();
        Record(h, samples: 7);
        Record(h, samples: 5);
        Assert.Equal(12, h.TotalSamples);

        h.RemoveLast();

        Assert.Equal(7, h.TotalSamples);
    }

    [Fact]
    public void ClearResetsTheTally()
    {
        var h = new StrokeHistory();
        Record(h, samples: 9);
        h.Clear();

        Assert.Equal(0, h.TotalSamples);
        Assert.Null(h.EvictOldestIfOverCap());
    }

    [Fact]
    public void AnEmptyStrokeAddsNothingToTheTally()
    {
        var h = new StrokeHistory();
        h.BeginStroke(BrushSettings.Default, Blue);
        h.EndStroke();

        Assert.Equal(0, h.TotalSamples);
    }

    [Fact]
    public void EvictingFromAnEmptyHistoryIsSafe()
    {
        Assert.Null(new StrokeHistory().EvictOldestIfOverCap());
    }
}
