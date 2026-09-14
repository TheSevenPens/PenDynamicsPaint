using System.IO.Compression;
using System.Xml.Linq;
using PenDynamicsPaint.Drawing;
using PenDynamicsPaint.Paint;
using SkiaSharp;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// Saving and opening the document, as OpenRaster.
/// </summary>
/// <remarks>
/// <para>
/// The format is a real one rather than an invention here, so the tests are in two halves. Some
/// check that a document survives the round trip, which is what the user cares about. The rest
/// check the file against the specification -- that the mimetype entry is first and stored, that
/// there is a full-size merged image and a thumbnail -- because those exist for readers that are
/// not this application, and nothing about this application would notice them missing.
/// </para>
/// <para>
/// A round trip alone would also miss the fault most worth catching: writing the stack the wrong
/// way up. It cancels out on the way back in, so it passes every round trip and produces a file
/// that opens inverted in every other application.
/// </para>
/// </remarks>
public class OpenRasterTests
{
    private static readonly SKColor Red = new(0xFF, 0x00, 0x00);
    private static readonly SKColor Blue = new(0x00, 0x00, 0xFF);

    private static BrushSettings Opaque(double size = 30) =>
        BrushSettings.Default with { Size = size };

    /// <summary>A short opaque stroke, drawn and finished.</summary>
    private static void Stroke(PaintSession session, SKColor color,
                               double x0, double y0, double x1, double y1)
    {
        session.SetStrokeColor(color);

        var brush = Opaque();
        double dx = x1 - x0, dy = y1 - y0;
        double length = Math.Sqrt(dx * dx + dy * dy);

        for (double d = 0; d <= length; d += 2.0)
        {
            double t = d / length;
            session.AddSample(x0 + dx * t, y0 + dy * t, 1.0, brush);
        }

        session.EndStroke();
    }

    /// <summary>Two layers, each with a mark, overlapping in the middle.</summary>
    /// <remarks>
    /// The marks cross, which is the point: where they do not overlap the order of the stack
    /// cannot be seen, and a test that only looked there would pass with the stack inverted.
    /// </remarks>
    private static PaintSession TwoMarkedLayers(int width = 200, int height = 160)
    {
        var session = new PaintSession(width, height);

        Stroke(session, Red, 20, height / 2.0, width - 20, height / 2.0);

        session.AddLayer("Top");
        Stroke(session, Blue, width / 2.0, 20, width / 2.0, height - 20);

        return session;
    }

    private static MemoryStream SavedTo(PaintSession session)
    {
        var stream = new MemoryStream();
        OpenRaster.Save(session, stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void A_document_comes_back_with_its_layers_the_same_way_up()
    {
        using var original = TwoMarkedLayers();
        using var before = original.Bitmap.Copy();

        // Where the two marks cross, the top layer's blue is what shows.
        Assert.Equal(Blue, before.GetPixel(100, 80));

        using var file = SavedTo(original);
        using var reopened = OpenRaster.Load(file).Session;

        Assert.Equal(2, reopened.Layers.Count);
        Assert.Equal(Blue, reopened.Bitmap.GetPixel(100, 80));

        // And the whole image, not just the crossing.
        for (int y = 0; y < before.Height; y++)
            for (int x = 0; x < before.Width; x++)
                if (before.GetPixel(x, y) != reopened.Bitmap.GetPixel(x, y))
                    Assert.Fail($"the reopened document differs at {x},{y}: " +
                                $"{reopened.Bitmap.GetPixel(x, y)} against {before.GetPixel(x, y)}");
    }

    [Fact]
    public void The_stack_is_written_top_first()
    {
        // The half a round trip cannot see. Writing the stack the wrong way up inverts it on the
        // way out and inverts it again on the way back in, so the document that returns here is
        // correct while the file itself opens upside down in MyPaint, Krita and GIMP.
        using var session = TwoMarkedLayers();
        using var file = SavedTo(session);

        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        using var stack = zip.GetEntry("stack.xml")!.Open();

        var names = XDocument.Load(stack).Descendants("layer")
            .Select(l => (string?)l.Attribute("name"))
            .ToList();

        Assert.Equal(["Top", "Layer 1"], names);
    }

    [Fact]
    public void A_layer_keeps_its_name_and_opacity_and_whether_it_was_hidden()
    {
        using var session = TwoMarkedLayers();

        session.RenameLayer(0, "Underpainting");
        session.SetLayerOpacity(0, 0.4);
        session.SetLayerVisible(1, false);

        using var file = SavedTo(session);
        using var reopened = OpenRaster.Load(file).Session;

        Assert.Equal("Underpainting", reopened.Layers[0].Name);
        Assert.Equal(0.4, reopened.Layers[0].Opacity, 3);
        Assert.False(reopened.Layers[1].IsVisible);

        // Visible is the default, so a layer that was not hidden has to come back not hidden --
        // otherwise reading the attribute at all would be untested.
        Assert.True(reopened.Layers[0].IsVisible);
    }

    [Fact]
    public void Opening_a_document_does_not_leave_undo_able_to_erase_it()
    {
        // Loaded pixels are accounted for by no stroke in the history. Without baking them as the
        // replay baseline, the first undo of a stroke drawn afterwards clears the layer and
        // replays only that session's own strokes -- so opening a file and drawing one mark would
        // put the user one Ctrl+Z away from losing everything they opened.
        using var session = TwoMarkedLayers();
        using var file = SavedTo(session);

        using var reopened = OpenRaster.Load(file).Session;
        using var asOpened = reopened.Bitmap.Copy();

        Stroke(reopened, Red, 30, 30, 60, 30);
        Assert.True(reopened.Undo(), "there should be a stroke to undo");

        var diff = FirstDifference(asOpened, reopened.Bitmap);
        Assert.True(diff is null,
            diff is null ? "" :
            $"undo changed the opened document at {diff.Value.X},{diff.Value.Y}: " +
            $"{diff.Value.B} where it was {diff.Value.A}");
    }

    [Fact]
    public void The_mimetype_entry_is_first_and_stored_uncompressed()
    {
        // Both are required by the specification, and for the same reason: a reader identifies the
        // file by looking for this string at a fixed offset from the start, without unzipping it.
        // Deflated or written second, the file is still a valid zip and still opens here, and
        // something else refuses to recognise it.
        using var session = TwoMarkedLayers();
        using var file = SavedTo(session);

        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var first = zip.Entries[0];

        Assert.Equal("mimetype", first.FullName);
        Assert.Equal(first.Length, first.CompressedLength);

        using var reader = new StreamReader(first.Open());
        Assert.Equal("image/openraster", reader.ReadToEnd());
    }

    [Fact]
    public void The_file_carries_a_full_size_flattening_and_a_thumbnail()
    {
        // For readers that do not understand layers. Neither is used when opening the document
        // here, so nothing in this application would notice either one missing or wrong.
        //
        // Larger than the thumbnail limit on both sides, which is the whole point of this one: a
        // document smaller than 256 needs no scaling, so a thumbnail written at full size passes
        // every assertion below. The first version of this test used a 200x160 document and missed
        // exactly that.
        using var session = TwoMarkedLayers(600, 400);
        using var expected = session.Bitmap.Copy();
        using var file = SavedTo(session);

        using var zip = new ZipArchive(file, ZipArchiveMode.Read);

        using var merged = Decode(zip, "mergedimage.png");
        Assert.Equal(session.Width, merged.Width);
        Assert.Equal(session.Height, merged.Height);
        Assert.Null(FirstDifference(expected, merged));

        using var thumbnail = Decode(zip, "Thumbnails/thumbnail.png");

        // Scaled down to the limit on its long side, and still the same shape: a thumbnail that
        // fits by being squashed would satisfy the limit and misrepresent the picture.
        Assert.Equal(256, thumbnail.Width);
        Assert.Equal(171, thumbnail.Height);
    }

    [Fact]
    public void A_layer_offset_into_the_canvas_lands_where_the_file_puts_it()
    {
        // How other applications store a layer covering only part of the image. Ignoring x and y
        // loads those pixels in the corner instead, which looks like a document that opened
        // scrambled rather than like an attribute going unread.
        using var patch = new SKBitmap(20, 20, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(patch)) canvas.Clear(Red);

        using var file = OneLayerAt(patch, x: 60, y: 40, width: 200, height: 160);
        using var loaded = OpenRaster.Load(file).Session;

        Assert.Equal(Red, loaded.Bitmap.GetPixel(65, 45));
        Assert.NotEqual(Red, loaded.Bitmap.GetPixel(5, 5));
    }

    [Fact]
    public void A_document_this_wrote_reports_nothing_as_unhonoured()
    {
        // The check none of the round-trip tests made, and the one that mattered: they all read
        // the session back and never looked at what was said about it. Opening a file written here
        // announced "layer groups (flattened) not honoured" on every document, because asking
        // whether one contains a stack at all is always true -- the root of the format is a stack.
        //
        // Found by opening a file in the application and reading the status line, not by a test.
        using var session = TwoMarkedLayers();
        using var file = SavedTo(session);

        var loaded = OpenRaster.Load(file);
        using var reopened = loaded.Session;

        Assert.True(loaded.Ignored.Count == 0,
            $"nothing should be unhonoured in our own file, but: {string.Join(", ", loaded.Ignored)}");
    }

    [Fact]
    public void A_group_of_layers_is_flattened_and_said_to_have_been()
    {
        // The other half. A detection that never fires is the same as none, so this pins that a
        // genuine group -- a stack inside the root stack -- is both kept and reported.
        using var patch = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(patch)) canvas.Clear(Blue);

        using var file = OneLayerAt(patch, 0, 0, 40, 40, inAGroup: true);
        var loaded = OpenRaster.Load(file);

        using var session = loaded.Session;

        Assert.Contains("layer groups (flattened)", loaded.Ignored);

        // Flattened, not dropped: the layer inside the group still arrives.
        Assert.Single(session.Layers);
        Assert.Equal(Blue, session.Bitmap.GetPixel(5, 5));
    }

    [Fact]
    public void A_composite_mode_this_cannot_do_is_reported_rather_than_passed_over()
    {
        // The same bargain the brush loader makes. The document still opens -- refusing it would
        // be worse -- but the user is told which part of it is not being honoured, instead of
        // looking at a multiply layer rendered as if it were normal.
        using var patch = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(patch)) canvas.Clear(Blue);

        using var file = OneLayerAt(patch, 0, 0, 40, 40, compositeOp: "svg:multiply");
        var loaded = OpenRaster.Load(file);

        using var session = loaded.Session;
        Assert.Contains("svg:multiply", loaded.Ignored);
    }

    [Fact]
    public void A_file_that_is_not_a_document_says_so_usefully()
    {
        // A zip with no stack.xml is the shape of the mistake: any other zip, renamed.
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("something.txt");
        }

        stream.Position = 0;

        var error = Assert.Throws<FormatException>(() => OpenRaster.Load(stream));
        Assert.Contains("stack.xml", error.Message);
    }

    [Fact]
    public void An_exported_png_is_the_flattened_document()
    {
        using var session = TwoMarkedLayers();
        using var expected = session.Bitmap.Copy();

        using var stream = new MemoryStream();
        OpenRaster.ExportPng(session, stream);
        stream.Position = 0;

        using var exported = SKBitmap.Decode(stream);

        Assert.Equal(session.Width, exported.Width);
        Assert.Equal(session.Height, exported.Height);
        Assert.Null(FirstDifference(expected, exported));
    }

    /// <summary>A minimal one-layer file, for the cases this application never writes itself.</summary>
    private static MemoryStream OneLayerAt(SKBitmap content, int x, int y, int width, int height,
                                           string compositeOp = "svg:src-over",
                                           bool inAGroup = false)
    {
        var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var mime = new StreamWriter(
                       zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open()))
            {
                mime.Write("image/openraster");
            }

            var layer = new XElement("layer",
                new XAttribute("name", "Only"),
                new XAttribute("src", "data/layer0.png"),
                new XAttribute("x", x),
                new XAttribute("y", y),
                new XAttribute("opacity", "1"),
                new XAttribute("visibility", "visible"),
                new XAttribute("composite-op", compositeOp));

            // A group is a stack inside the root stack, which is how other applications store one.
            object content_ = inAGroup ? new XElement("stack", new XAttribute("name", "A group"), layer)
                                       : layer;

            var image = new XElement("image",
                new XAttribute("version", "0.0.3"),
                new XAttribute("w", width),
                new XAttribute("h", height),
                new XElement("stack", content_));

            using (var entry = zip.CreateEntry("stack.xml").Open())
            {
                new XDocument(image).Save(entry);
            }

            using var png = zip.CreateEntry("data/layer0.png").Open();
            using var encoded = SKImage.FromBitmap(content).Encode(SKEncodedImageFormat.Png, 100);
            encoded.SaveTo(png);
        }

        stream.Position = 0;
        return stream;
    }

    private static SKBitmap Decode(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        Assert.True(entry is not null, $"the file has no {path}");

        using var stream = entry!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;

        return SKBitmap.Decode(memory);
    }

    /// <summary>Where the two bitmaps first differ, or null when they match everywhere.</summary>
    private static (int X, int Y, SKColor A, SKColor B)? FirstDifference(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return (0, 0, SKColors.Empty, SKColors.Empty);

        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                var ca = a.GetPixel(x, y);
                var cb = b.GetPixel(x, y);
                if (ca != cb) return (x, y, ca, cb);
            }

        return null;
    }
}
