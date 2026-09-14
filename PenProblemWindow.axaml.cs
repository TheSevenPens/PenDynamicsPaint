using Avalonia.Controls;
using Avalonia.Interactivity;
using WinPenKit;

namespace PenDynamicsPaint;

/// <summary>What the user chose to do about a tablet that would not open.</summary>
public enum PenProblemChoice
{
    /// <summary>Carry on with no pen.</summary>
    Dismiss,

    /// <summary>Open the same driver again, presumably after closing whatever held it.</summary>
    Retry,

    /// <summary>Read the tablet through Windows instead, which needs no driver of its own.</summary>
    UseFallback,
}

/// <summary>
/// Says that the tablet could not be opened, and offers the two ways out.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the status line did not work. A pen session that fails to open leaves a
/// canvas that will not take a stroke, and that is indistinguishable from a broken renderer or a
/// broken brush -- it was read as one both times it happened. A line at the bottom of the window
/// is not where anyone looks when the thing in the middle appears not to work.
/// </para>
/// <para>
/// Actionable rather than only informative. The usual cause is another application holding a
/// Wintab context, so the two answers are "I have closed it, try again" and "read the tablet
/// through Windows instead", and both are one click.
/// </para>
/// </remarks>
public partial class PenProblemWindow : Window
{
    /// <summary>What was chosen. <see cref="PenProblemChoice.Dismiss"/> if the window was closed.</summary>
    public PenProblemChoice Choice { get; private set; } = PenProblemChoice.Dismiss;

    public PenProblemWindow() : this(InputApi.WintabDigitizer, "", true) { }

    public PenProblemWindow(InputApi api, string driverMessage, bool fallbackAvailable)
    {
        InitializeComponent();

        CauseText.Text = Cause(api);
        DriverText.Text = driverMessage.Length > 0
            ? $"{api.Label()} said: {driverMessage}"
            : $"{api.Label()} could not be started.";

        // Offering the fallback as a way out of the fallback would be a loop, and offering one
        // this machine does not have would be a button that does nothing.
        FallbackButton.IsVisible = fallbackAvailable && api != InputApi.AvaloniaPointer;
        if (!FallbackButton.IsVisible) RetryButton.IsDefault = true;
    }

    /// <summary>The likely reason, in terms of something the user can act on.</summary>
    /// <remarks>
    /// Wintab contexts are commonly held by whichever application opened one first: Clip Studio
    /// and Photoshop both take one and keep it for as long as they are running. Named, because
    /// "another application" sends someone hunting and the two that do it are worth saying.
    /// </remarks>
    private static string Cause(InputApi api) => api == InputApi.AvaloniaPointer
        ? "Windows is not passing pen input to this window. A tablet that is unplugged or a "
          + "driver that is not running will both do this."
        : "Another application is probably holding the tablet. Wintab hands its context to one "
          + "application at a time, and Clip Studio Paint and Photoshop both take one and keep it "
          + "for as long as they are open.";

    private void Choose(PenProblemChoice choice)
    {
        Choice = choice;
        Close();
    }

    private void Dismiss_Click(object? sender, RoutedEventArgs e) => Choose(PenProblemChoice.Dismiss);

    private void Retry_Click(object? sender, RoutedEventArgs e) => Choose(PenProblemChoice.Retry);

    private void Fallback_Click(object? sender, RoutedEventArgs e) =>
        Choose(PenProblemChoice.UseFallback);
}
