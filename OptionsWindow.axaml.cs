using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using WinPenKit;

namespace PenDynamicsPaint;

/// <summary>
/// The settings that are chosen once rather than while drawing.
/// </summary>
/// <remarks>
/// <para>
/// One setting so far, and it is here because of what it is rather than to fill a dialog: the pen
/// backend is which driver the tablet is read through, not a drawing choice, and it sat in the
/// toolbar permanently in view although nobody changes it twice in a session.
/// </para>
/// <para>
/// Answers with a value rather than reaching back into the window. The caller applies it, because
/// restarting the pen session is the window's business and a dialog that did it itself would have
/// to know about hardware.
/// </para>
/// </remarks>
public partial class OptionsWindow : Window
{
    private readonly IReadOnlyList<InputApi> _apis;

    /// <summary>Which backend was chosen, or null if the dialog was cancelled.</summary>
    public InputApi? Chosen { get; private set; }

    public OptionsWindow() : this([], null) { }

    public OptionsWindow(IReadOnlyList<InputApi> apis, InputApi? current)
    {
        InitializeComponent();

        _apis = apis;

        ApiCombo.ItemsSource = apis.Select(a => a.Label()).ToList();
        int at = current is { } api ? apis.ToList().IndexOf(api) : -1;
        ApiCombo.SelectedIndex = Math.Max(0, at);

        // Said out loud rather than left for the user to discover by drawing and getting nothing.
        // A Wintab entry appears whether or not a tablet is plugged in.
        ApiStatus.Text = apis.Count == 0
            ? "No pen backend is available."
            : "Changing this restarts the pen session.";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (ApiCombo.SelectedIndex >= 0 && ApiCombo.SelectedIndex < _apis.Count)
        {
            Chosen = _apis[ApiCombo.SelectedIndex];
        }

        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
