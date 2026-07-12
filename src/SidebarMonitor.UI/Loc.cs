using System.Globalization;

namespace SidebarMonitor.UI;

/// <summary>
/// Lightweight UI localisation. The codebase builds its WPF tree in C# (no XAML/resx), and its
/// strings were written in Spanish, so rather than re-key everything we translate <em>by the Spanish
/// string</em>: <see cref="T"/> returns the Spanish literal as-is when the language is Spanish, or
/// looks up its English translation in <see cref="LocStrings.En"/> otherwise. Wrapping a user-facing
/// literal is therefore just <c>Loc.T("…")</c>, and English lives in one file. A missing translation
/// falls back to the Spanish text, so the app is never blank — only untranslated.
/// </summary>
public static class Loc
{
    public enum Lang { Es, En }

    /// <summary>The active language. Set once at startup by <see cref="Init"/>.</summary>
    public static Lang Current { get; private set; } = Lang.Es;

    /// <summary>Resolves the configured preference ("auto"/"es"/"en") against the OS culture.</summary>
    public static void Init(string? preference)
    {
        Current = preference switch
        {
            "es" => Lang.Es,
            "en" => Lang.En,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                    .Equals("es", StringComparison.OrdinalIgnoreCase) ? Lang.Es : Lang.En,
        };
    }

    /// <summary>Translates a Spanish UI literal to the active language.</summary>
    public static string T(string es)
        => Current == Lang.En ? LocStrings.En.GetValueOrDefault(es, es) : es;

    /// <summary>Convenience for interpolation: <c>Loc.T("{0} discos", n)</c>-style formatting where the
    /// Spanish key contains composite-format placeholders.</summary>
    public static string T(string es, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, T(es), args);
}
