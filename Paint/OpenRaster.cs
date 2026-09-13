using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using SkiaSharp;

namespace PenDynamicsPaint.Paint;

/// <summary>
/// Reads and writes the document as an OpenRaster (<c>.ora</c>) file.
/// </summary>
/// <remarks>
/// <para>
/// A real format rather than one invented here. OpenRaster is a zip holding one PNG per layer and
/// a <c>stack.xml</c> describing the stack, and it is what MyPaint saves; Krita and GIMP read it
/// too. So a document written here opens in the applications whose brushes it is being driven by,
/// which is worth more than any convenience a private format could buy.
/// </para>
/// <para>
/// Written to version <c>0.0.3</c> of the specification. Three parts of that are easy to leave out
/// and none of them are optional: <c>mimetype</c> must be the <b>first</b> entry and <b>stored
/// uncompressed</b>, so a reader can identify the file from its leading bytes without unzipping it;
/// <c>mergedimage.png</c> must be a full-size flattening of the stack, which is what lets a viewer
/// show the image without understanding layers at all; and a thumbnail no larger than 256 square
/// goes in <c>Thumbnails/</c>.
/// </para>
/// <para>
/// <b>The stack is listed top first.</b> The opposite of <see cref="PaintSession.Layers"/>, where
/// index 0 is the bottom because that is the order they are drawn in. Getting this backwards
/// produces a file that opens with the layers inverted, and nothing about it looks wrong until
/// something is hidden behind what it used to be in front of.
/// </para>
/// <para>
/// <b>What is not saved: the undo history.</b> A loaded layer's pixels are baked as the replay
/// baseline, so an undo after opening a file steps back through this session's own strokes and
/// stops, rather than erasing work that came from disk. Saving the history would mean saving the
/// brushes that drew it, which is a format decision of its own.
/// </para>
/// </remarks>
public static class OpenRaster
{
    private const string MimeType = "image/openraster";
    private const string StackFile = "stack.xml";
    private const string MergedFile = "mergedimage.png";
    private const string ThumbnailFile = "Thumbnails/thumbnail.png";

    /// <summary>The largest a thumbnail may be on either side, from the specification.</summary>
    private const int ThumbnailMax = 256;

    /// <summary>Write the session's layer stack to <paramref name="destination"/>.</summary>
    public static void Save(PaintSession session, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(destination);

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        // First, and stored rather than deflated. Both are required: a reader identifies the file
        // by finding this string at a fixed offset from the start of the archive.
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(mime.Open()))
        {
            writer.Write(MimeType);
        }

        var stack = new XElement("stack");

        // Top first, so the session's layers are walked backwards.
        for (int i = session.Layers.Count - 1; i >= 0; i--)
        {
            var layer = session.Layers[i];
            string path = $"data/layer{i}.png";

            stack.Add(new XElement("layer",
                new XAttribute("name", layer.Name),
                new XAttribute("src", path),
                new XAttribute("x", 0),
                new XAttribute("y", 0),
                new XAttribute("opacity",
                    layer.Opacity.ToString("0.######", CultureInfo.InvariantCulture)),
                new XAttribute("visibility", layer.IsVisible ? "visible" : "hidden"),
                new XAttribute("composite-op", "svg:src-over")));

            WritePng(zip, path, layer.Bitmap);
        }

        var image = new XElement("image",
            new XAttribute("version", "0.0.3"),
            new XAttribute("w", session.Width),
            new XAttribute("h", session.Height),
            stack);

        var entry = zip.CreateEntry(StackFile, CompressionLevel.Optimal);
        using (var stream = entry.Open())
        {
            new XDocument(new XDeclaration("1.0", "UTF-8", null), image).Save(stream);
        }

        var merged = session.Bitmap;

        WritePng(zip, MergedFile, merged);

        using var thumbnail = Thumbnail(merged);
        WritePng(zip, ThumbnailFile, thumbnail);
    }

    /// <summary>Read a document back, and say what of it could not be honoured.</summary>
    public static Loaded Load(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);

        var stackEntry = zip.GetEntry(StackFile)
            ?? throw new FormatException(
                $"not an OpenRaster file: it has no {StackFile}. A .ora is a zip of PNGs " +
                "described by that file.");

        XDocument document;
        using (var stream = stackEntry.Open())
        {
            document = XDocument.Load(stream);
        }

        var image = document.Root ?? throw new FormatException($"{StackFile} is empty.");

        int width = (int?)image.Attribute("w") ?? 0;
        int height = (int?)image.Attribute("h") ?? 0;

        if (width <= 0 || height <= 0)
        {
            throw new FormatException($"{StackFile} gives no usable image size.");
        }

        var ignored = new List<string>();

        // Descendants rather than children: the format allows a nested stack as a group, and this
        // application has none. Flattening keeps every layer's pixels rather than dropping a whole
        // group, and the shortfall is reported instead of passing silently.
        var layers = image.Descendants("layer").ToList();

        // A group is a stack inside the root one. Asking whether the document contains a stack at
        // all is always true -- the root is one -- so that reports a group on every file, including
        // every file this application writes.
        if (image.Element("stack")?.Descendants("stack").Any() == true)
        {
            ignored.Add("layer groups (flattened)");
        }

        var session = new PaintSession(width, height);

        // The file is top first and the session is built from the bottom up.
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            var element = layers[i];
            int index = layers.Count - 1 - i;

            // A session is created with one layer already in it, which the bottom file layer takes
            // over rather than being added alongside.
            var layer = index == 0 ? session.Layers[0] : session.AddLayer();

            if ((string?)element.Attribute("src") is { } path && zip.GetEntry(path) is { } png)
            {
                using var stream = png.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                memory.Position = 0;

                using var bitmap = SKBitmap.Decode(memory);
                if (bitmap is not null)
                {
                    // A layer may be smaller than the canvas and offset into it, which is how
                    // other applications store one that covers only part of the image.
                    float x = (float?)element.Attribute("x") ?? 0;
                    float y = (float?)element.Attribute("y") ?? 0;
                    layer.Canvas.DrawBitmap(bitmap, x, y);
                }
            }

            // No stroke in the history accounts for these pixels, so they become the point replay
            // starts from. Without it the first undo would clear the layer and replay nothing.
            layer.BakeAsBaseline();

            // Looked up rather than assumed to be `index`. It is, as long as AddLayer keeps
            // inserting directly above the active layer and moving the active index with it --
            // which is an invariant of that method, not of this one, and if it ever changed the
            // symptom here would be a name and an opacity landing on the wrong layer.
            int at = IndexOf(session, layer);

            if ((string?)element.Attribute("name") is { } name)
            {
                session.RenameLayer(at, name);
            }

            if ((string?)element.Attribute("visibility") is { } visibility)
            {
                session.SetLayerVisible(at, !visibility.Equals("hidden", StringComparison.Ordinal));
            }

            if ((double?)element.Attribute("opacity") is { } opacity)
            {
                session.SetLayerOpacity(at, opacity);
            }

            if ((string?)element.Attribute("composite-op") is { } op
                && op != "svg:src-over" && !ignored.Contains(op))
            {
                ignored.Add(op);
            }
        }

        session.SetActiveLayer(session.Layers.Count - 1);
        session.InvalidateComposite();

        return new Loaded(session, ignored);
    }

    /// <summary>Write the flattened document as a PNG, for anything that wants a plain image.</summary>
    public static void ExportPng(PaintSession session, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(destination);

        using var image = SKImage.FromBitmap(session.Bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        data.SaveTo(destination);
    }

    /// <summary>A document read from a file, and the parts of it this application cannot honour.</summary>
    /// <remarks>
    /// The same bargain <c>MyPaintBrush.Ignored</c> makes: a file that asks for something
    /// unimplemented still opens, and what went unused is reported rather than passed over.
    /// </remarks>
    public sealed record Loaded(PaintSession Session, IReadOnlyList<string> Ignored);

    private static int IndexOf(PaintSession session, Layer layer)
    {
        for (int i = 0; i < session.Layers.Count; i++)
        {
            if (ReferenceEquals(session.Layers[i], layer)) return i;
        }

        return 0;
    }

    private static void WritePng(ZipArchive zip, string path, SKBitmap bitmap)
    {
        // Stored rather than deflated: the bytes are already PNG, so a second pass over them buys
        // almost nothing and costs the whole image's worth of work on every save.
        var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);

        using var stream = entry.Open();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        data.SaveTo(stream);
    }

    private static SKBitmap Thumbnail(SKBitmap source)
    {
        int width = source.Width, height = source.Height;
        double scale = Math.Min(1.0, (double)ThumbnailMax / Math.Max(width, height));

        int w = Math.Max(1, (int)Math.Round(width * scale));
        int h = Math.Max(1, (int)Math.Round(height * scale));

        var thumbnail = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var canvas = new SKCanvas(thumbnail);
        using var paint = new SKPaint { IsAntialias = true };

        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, new SKRect(0, 0, width, height), new SKRect(0, 0, w, h), paint);

        return thumbnail;
    }
}
