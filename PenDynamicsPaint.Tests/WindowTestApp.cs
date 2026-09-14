using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

using WinPenKit.Diagnostics;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// The Avalonia application the window tests run inside, with no desktop behind it.
/// </summary>
/// <remarks>
/// <para>
/// Headless because the tests are about wiring rather than pixels: whether a click on a swatch
/// reaches the session, whether the picker holds the brushes the library defines. None of that
/// needs a window on a screen, and needing one would mean these could not run on a build machine.
/// </para>
/// <para>
/// <b>The Fluent theme is loaded even though nothing is looked at.</b> Constructing a window builds
/// its controls, and a ComboBox with no template throws rather than quietly coming up bare -- so a
/// test that seems to be about a colour fails somewhere far away with a missing style.
/// </para>
/// <para>
/// This hosts the application under test rather than being it. The real <c>App</c> wires up a
/// desktop lifetime and a main window, which is the opposite of what a test wants: each test makes
/// its own window and drops it.
/// </para>
/// </remarks>
public sealed class WindowTestApp : Application
{
    public WindowTestApp()
    {
        Styles.Add(new FluentTheme());
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<WindowTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// Runs a test body on Avalonia's own thread, with a headless platform under it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Avalonia.Headless.XUnit is deliberately not used</b>, though it exists and would give an
/// <c>[AvaloniaFact]</c> attribute. At this version it brings xunit v3 with it, which cannot share
/// an assembly with the xunit 2 the other two hundred tests are written against -- every one of
/// them stops compiling on an ambiguous <c>FactAttribute</c>. Six window tests are not worth
/// migrating the suite, so this uses the session machinery underneath that adapter directly.
/// </para>
/// <para>
/// One session for the assembly, because it owns a dispatcher thread and starting a second would
/// mean two of them.
/// </para>
/// </remarks>
public static class OnTheUiThread
{
    private static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.StartNew(typeof(WindowTestApp));

    /// <summary>Run <paramref name="test"/> where Avalonia will let controls be built.</summary>
    /// <remarks>
    /// Every window the test made is closed afterwards, whether the test passed or threw. See
    /// <see cref="TestWindows"/> for why that matters more here than tidiness usually does.
    /// </remarks>
    public static void Run(Action test) => Session.Dispatch(() =>
    {
        try
        {
            test();
        }
        finally
        {
            TestWindows.CloseAll();
        }
    }, CancellationToken.None).GetAwaiter().GetResult();
}

/// <summary>
/// The windows a test made, so that they can be closed when it ends.
/// </summary>
/// <remarks>
/// <para>
/// A dropped window is not free. <c>MainWindow</c> opens a Wintab context when it is shown and
/// gives it back when it closes, and a Wintab context that is never closed is never returned by
/// the driver: the count climbs and stays climbed until the tablet service is restarted. A run of
/// this suite showed thirteen windows and closed four of them, so every run cost the machine
/// twenty-six of the driver's context units, and a morning of runs took it past a thousand. Past
/// some point the driver stops handing out contexts at all and no application on the machine can
/// see the pen, which looks like a broken tablet rather than like a test suite.
/// </para>
/// <para>
/// So this is not about tidiness. It is the difference between a suite that can be run all day and
/// one that quietly breaks the pen on the machine running it.
/// </para>
/// <para>
/// <b>It does not fix the leak.</b> The driver still keeps every context that is not closed
/// properly, and a killed process still leaks whatever it was holding. This only stops the tests
/// being the thing that does it.
/// </para>
/// </remarks>
internal static class TestWindows
{
    private static readonly List<Window> Open = [];

    /// <summary>Hand back the window, and remember to close it.</summary>
    /// <param name="withPenSession">
    /// True for the few tests that are actually about opening a pen session. Everything else gets
    /// a window that opens none, which is the difference between a suite that costs the driver
    /// thirteen contexts a run and one that costs it none even when the run is killed.
    /// </param>
    /// <remarks>
    /// A window built with an outcome already chosen keeps it. The tests that force a refusal or
    /// an absent driver are saying exactly what they want, and this is not entitled to overrule
    /// them.
    /// </remarks>
    internal static T Track<T>(T window, bool withPenSession = false) where T : Window
    {
        if (!withPenSession
            && window is MainWindow main
            && main.PenOutcomeForTest == MainWindow.PenForTest.Real)
        {
            main.PenOutcomeForTest = MainWindow.PenForTest.NoSession;
        }

        Open.Add(window);

        return window;
    }

    internal static void CloseAll()
    {
        foreach (var window in Open)
        {
            // A test that has already failed should not be reported as failing here instead, and
            // a window that will not close is not worth losing the real result over.
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[tests] Could not close a window: {ex.Message}");
            }
        }

        Open.Clear();
    }
}

/// <summary>
/// Reads the driver's context count before and after the window tests, and fails if it grew.
/// </summary>
/// <remarks>
/// <para>
/// The backstop for everything else in this file. Closing windows and not opening sessions are
/// both things a future edit can undo by accident, and the symptom of undoing them is not a red
/// test: it is a tablet that stops working hours later, on a machine where nothing obvious
/// changed. That is a bad trade, so the suite watches the number itself.
/// </para>
/// <para>
/// <b>Silent where there is no Wintab.</b> A build machine has no driver, so there is no count to
/// read and nothing to check. This is for the developer machine with the tablet attached, which
/// is the only one that can be hurt.
/// </para>
/// <para>
/// <b>It can be wrong.</b> The count belongs to the whole machine, so a painting application
/// opened while the suite runs will raise it and this will report a leak that is not ours. That is
/// rare, and the alternative -- an allowance for a few units -- would hide exactly the two-unit
/// leak worth catching. The message says so rather than pretending the number is ours alone.
/// </para>
/// </remarks>
public sealed class WintabWatch : IDisposable
{
    private readonly uint? _before = Read();

    public void Dispose()
    {
        if (_before is not { } before || Read() is not { } after || after <= before)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The window tests left the Wintab driver holding {after - before} more context "
            + $"units than they found ({before} to {after}). Something opened a pen session and "
            + "did not close it, and the driver does not take those back: see "
            + "Docs/WINTAB-CONTEXT-LEAK.md in WinPenKit. If a painting application was opened "
            + "while the tests ran, that is the other explanation.");
    }

    private static uint? Read() => WintabDiagnostics.ContextTable()?.Open;
}
