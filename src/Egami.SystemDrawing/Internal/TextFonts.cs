using EgamiFonts = Egami.Fonts;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// Finds the faces System.Drawing names: the installed fonts (or a private collection's), then metric-compatible open fonts for
/// the Windows core fonts, so text keeps its line lengths on Linux and macOS.
/// </summary>
internal static class TextFonts
{
    /// <summary>Metric-compatible open fonts for the Windows and Office core fonts, best first.</summary>
    private static readonly Dictionary<string, string[]> s_substitutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] = ["Liberation Sans", "Arimo"],
        ["Helvetica"] = ["Liberation Sans", "Arimo", "Arial"],
        ["Times New Roman"] = ["Liberation Serif", "Tinos"],
        ["Times"] = ["Liberation Serif", "Tinos", "Times New Roman"],
        ["Courier New"] = ["Liberation Mono", "Cousine"],
        ["Courier"] = ["Liberation Mono", "Cousine", "Courier New"],
        ["Calibri"] = ["Carlito"],
        ["Cambria"] = ["Caladea"],
        ["Arial Narrow"] = ["Liberation Sans Narrow"],
        ["Microsoft Sans Serif"] = ["Liberation Sans", "Arimo", "DejaVu Sans"],
        ["MS Sans Serif"] = ["Microsoft Sans Serif", "Liberation Sans", "Arimo", "DejaVu Sans"],
        ["Segoe UI"] = ["Selawik", "Open Sans", "Noto Sans", "DejaVu Sans"],
        ["Tahoma"] = ["DejaVu Sans", "Liberation Sans"],
        ["Verdana"] = ["DejaVu Sans", "Liberation Sans"],
    };

    /// <summary>The faces the generic families name, as on Windows.</summary>
    public static readonly string SansSerif = "Microsoft Sans Serif", Serif = "Times New Roman", Monospace = "Courier New";

    /// <summary>Finds a family by name in a collection, or a metric-compatible substitute in it; null when neither is there.</summary>
    public static EgamiFonts.FontFamily? Find(EgamiFonts.FontCollection fonts, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        if (fonts.TryGetFamily(name, out EgamiFonts.FontFamily family))
        {
            return family;
        }

        if (s_substitutes.TryGetValue(name, out string[]? candidates))
        {
            foreach (string candidate in candidates)
            {
                if (fonts.TryGetFamily(candidate, out family))
                {
                    return family;
                }
            }
        }

        return null;
    }

    /// <summary>Finds a generic family's face, falling back to any sans-serif face the collection has.</summary>
    public static EgamiFonts.FontFamily? Generic(string name, EgamiFonts.GenericFamily generic)
    {
        EgamiFonts.FontCollection fonts = EgamiFonts.FontCollection.System;
        if (Find(fonts, name) is { } family)
        {
            return family;
        }

        if (fonts.Match(generic, default) is EgamiFonts.FontMatch m && fonts.TryGetFamily(m.Typeface.FamilyName, out family))
        {
            return family;
        }

        return fonts.Families.Count > 0 ? fonts.Families[0] : null;
    }

    /// <summary>The Egami style for a System.Drawing style: bold is weight 700, italic the italic slant.</summary>
    public static EgamiFonts.FontStyle Style(FontStyle style) =>
        new((style & FontStyle.Bold) != 0 ? 700 : 400, 5, (style & FontStyle.Italic) != 0 ? EgamiFonts.FontSlant.Italic : EgamiFonts.FontSlant.Upright);
}
