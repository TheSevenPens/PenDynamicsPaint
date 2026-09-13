using Avalonia.Styling;

namespace PenDynamicsPaint.Theming;

/// <summary>
/// The app's own theme variants, beyond Avalonia's built-in Light and Dark.
/// </summary>
/// <remarks>
/// <para>
/// Each one inherits from <see cref="ThemeVariant.Light"/>. That is not decoration — it is what
/// makes them work at all. Fluent's own control templates are keyed on Light and Dark only, so a
/// variant with no inherit would leave every ComboBox, Slider and CheckBox with no template to
/// resolve. Inheriting Light means stock controls render as they do in light mode, and only the
/// <c>Pdl.*</c> brushes the app defines are re-pointed.
/// </para>
/// <para>
/// It also means a variant only has to override what it wants to change — though in practice the
/// Sakura dictionary defines the full set, so a missing key is a visible mistake rather than a
/// silent fallback to blue.
/// </para>
/// </remarks>
public static class AppThemeVariants
{
    /// <summary>A light theme whose chrome is pale cherry-blossom pink.</summary>
    public static readonly ThemeVariant Sakura = new("Sakura", ThemeVariant.Light);
}
