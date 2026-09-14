using Avalonia.Controls;
using Avalonia.Interactivity;
using WinPenKit;
using WinPenKit.Diagnostics;

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

    public PenProblemWindow() : this(InputApi.WintabDigitizer, "", true, null) { }

    public PenProblemWindow(InputApi api, string driverMessage, bool fallbackAvailable,
                            WintabContextTable? contexts)
    {
        InitializeComponent();

        CauseText.Text = Cause(api, contexts);
        DriverText.Text = driverMessage.Length > 0
            ? $"{api.Label()} said: {driverMessage}"
            : $"{api.Label()} could not be started.";

        // Offering the fallback as a way out of the fallback would be a loop, and offering one
        // this machine does not have would be a button that does nothing.
        FallbackButton.IsVisible = fallbackAvailable && api != InputApi.AvaloniaPointer;
        if (!FallbackButton.IsVisible) RetryButton.IsDefault = true;
    }

    /// <summary>What is worth saying about why, which is less than it looks.</summary>
    /// <remarks>
    /// <para>
    /// This has guessed wrong twice, so it now guesses less. First it said another application was
    /// holding the tablet, when the driver was refusing every kind of context to everyone. Then it
    /// said the driver had run out of contexts, because it had 253 open against a stated maximum
    /// of 32 -- and that was measured afterwards to be no limit at all: contexts opened past it
    /// happily, all the way to 334.
    /// </para>
    /// <para>
    /// What is actually known is that the driver can stop handing out contexts, with its own count
    /// frozen, and that restarting the tablet service puts it right within seconds. So that is
    /// what this says, along with the other cause it could be. The counts are shown when they look
    /// implausible, not as an explanation but because they say contexts have been leaked, which is
    /// a true thing about the machine and a thing the reader can act on.
    /// </para>
    /// </remarks>
    private static string Cause(InputApi api, WintabContextTable? contexts)
    {
        if (api == InputApi.AvaloniaPointer)
        {
            return "Windows is not passing pen input to this window. A tablet that is unplugged "
                 + "or a driver that is not running will both do this.";
        }

        string cause =
            "Either the tablet driver has stopped handing out contexts, which happens and is put "
            + "right by restarting the Wacom Tablet Service or the machine, or another "
            + "application is holding the tablet -- Clip Studio Paint and Photoshop both take a "
            + "context and keep it while they are open.";

        // Only when it is obviously implausible. A handful of open contexts is an ordinary
        // machine and saying so would be noise.
        if (contexts is { AboveStatedMaximum: true } odd)
        {
            cause += $" The driver reports {odd}, which means programs have been killed or have "
                   + "crashed rather than closing: each one leaves a context behind and the "
                   + "driver never takes it back. That is not why the pen has stopped, but it "
                   + "does clear when the service is restarted.";
        }

        return cause;
    }

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
