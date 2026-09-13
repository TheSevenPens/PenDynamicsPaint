using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

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
    public static void Run(Action test) => Session.Dispatch(test, CancellationToken.None).GetAwaiter().GetResult();
}
