using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PenDynamicsPaint;

/// <summary>
/// Asks how big a new document should be.
/// </summary>
/// <remarks>
/// File &gt; New used to make a document the same size as the one already open, which is only ever
/// right by accident. Named sizes rather than two number boxes: a document size is picked once and
/// almost always from the handful of shapes a screen comes in.
/// </remarks>
public partial class NewDocumentWindow : Window
{
    /// <summary>The sizes on offer, in the order they are shown.</summary>
    /// <remarks>
    /// Named the way a display is sold rather than by pixel count, because that is what someone
    /// choosing already has a feel for. 2K is the default: it is the commonest screen, and a new
    /// document that is four times the area of the screen it will be looked at on is a surprise
    /// rather than a convenience.
    /// </remarks>
    public static readonly (string Name, int Width, int Height)[] Sizes =
    [
        ("2K  (1920 x 1080)",   1920, 1080),
        ("2.5K  (2560 x 1440)", 2560, 1440),
        ("4K  (3840 x 2160)",   3840, 2160),
        ("Square  (2048)",      2048, 2048),
        ("A4 at 300 dpi",       2480, 3508),
    ];

    /// <summary>Which size was chosen, or null if the dialog was cancelled.</summary>
    public (int Width, int Height)? Chosen { get; private set; }

    public NewDocumentWindow()
    {
        InitializeComponent();

        SizeList.ItemsSource = Sizes.Select(s => s.Name).ToList();
        SizeList.SelectedIndex = 0;

        SizeList.SelectionChanged += (_, _) => ShowDetail();

        // Double-clicking a size is the same as choosing it and pressing Create, which is what a
        // list of things to pick from ought to do.
        SizeList.DoubleTapped += (_, e) => { e.Handled = true; Accept(); };

        ShowDetail();
    }

    private void ShowDetail()
    {
        if (SizeList.SelectedIndex < 0 || SizeList.SelectedIndex >= Sizes.Length) return;

        var (_, width, height) = Sizes[SizeList.SelectedIndex];
        double megapixels = width * (double)height / 1_000_000;

        SizeDetail.Text = $"{width} x {height} document units, {megapixels:F1} megapixels";
    }

    private void Accept()
    {
        if (SizeList.SelectedIndex >= 0 && SizeList.SelectedIndex < Sizes.Length)
        {
            var (_, width, height) = Sizes[SizeList.SelectedIndex];
            Chosen = (width, height);
        }

        Close();
    }

    private void Create_Click(object? sender, RoutedEventArgs e) => Accept();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
