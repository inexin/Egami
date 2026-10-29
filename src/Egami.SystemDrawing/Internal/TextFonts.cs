using System.Collections.Concurrent;

using EgamiFonts = Egami.Fonts;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// Finds the faces System.Drawing names: the installed fonts (or a private collection's), then the stand-ins an application
/// registers in <see cref="SystemDrawingOptions.FontSubstitutes"/>, such as Egami.Fonts.Substitutes' metric-compatible fonts.
/// </summary>
internal static class TextFonts
{
    /// <summary>The faces the generic families name, as on Windows.</summary>
    public static readonly string SansSerif = "Microsoft Sans Serif", Serif = "Times New Roman", Monospace = "Courier New";

    private static readonly EgamiFonts.FontStyle[] s_styles =
    [
        new(400, 5, EgamiFonts.FontSlant.Upright), new(700, 5, EgamiFonts.FontSlant.Upright),
        new(400, 5, EgamiFonts.FontSlant.Italic), new(700, 5, EgamiFonts.FontSlant.Italic),
    ];

    /// <summary>The stand-in families built so far, by the finder that gave them and the requested name.</summary>
    private static readonly ConcurrentDictionary<(object Finder, string Name), EgamiFonts.FontFamily?> s_standIns = new();

    /// <summary>
    /// Finds a family by name in a collection, or the stand-in the registered substitutes give for it; null when neither has it.
    /// <paramref name="substituted"/> tells whether a stand-in was used.
    /// </summary>
    public static EgamiFonts.FontFamily? Find(EgamiFonts.FontCollection fonts, string name, out bool substituted)
    {
        substituted = false;
        name = name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        if (fonts.TryGetFamily(name, out EgamiFonts.FontFamily family))
        {
            return family;
        }

        if (SystemDrawingOptions.FontSubstitutes is not { } finder)
        {
            return null;
        }

        EgamiFonts.FontFamily? standIn = s_standIns.GetOrAdd((finder, name.ToUpperInvariant()), _ => StandIn(finder, name));
        substituted = standIn is not null;
        return standIn;
    }

    /// <summary>Finds a generic family's face, falling back to the installed fonts' own sans-serif, serif or monospace match.</summary>
    public static EgamiFonts.FontFamily? Generic(string name, EgamiFonts.GenericFamily generic, out bool substituted)
    {
        EgamiFonts.FontCollection fonts = EgamiFonts.FontCollection.System;
        if (Find(fonts, name, out substituted) is { } family)
        {
            return family;
        }

        if (fonts.Match(generic, new EgamiFonts.FontStyle()) is EgamiFonts.FontMatch m && fonts.TryGetFamily(m.Typeface.FamilyName, out family))
        {
            return family;
        }

        return fonts.Families.Count > 0 ? fonts.Families[0] : null;
    }

    /// <summary>The Egami style for a System.Drawing style: bold is weight 700, italic the italic slant.</summary>
    public static EgamiFonts.FontStyle Style(FontStyle style) =>
        new((style & FontStyle.Bold) != 0 ? 700 : 400, 5, (style & FontStyle.Italic) != 0 ? EgamiFonts.FontSlant.Italic : EgamiFonts.FontSlant.Upright);

    /// <summary>A family of the faces a finder gives for a name in the four styles, or null when it gives none.</summary>
    private static EgamiFonts.FontFamily? StandIn(Func<string, EgamiFonts.FontStyle, EgamiFonts.Typeface?> finder, string name)
    {
        EgamiFonts.FontCollection faces = new();
        string? familyName = null;
        HashSet<EgamiFonts.Typeface> added = [];
        foreach (EgamiFonts.FontStyle style in s_styles)
        {
            if (finder(name, style) is EgamiFonts.Typeface face && added.Add(face))
            {
                faces.Add(face);
                familyName ??= face.FamilyName;
            }
        }

        return familyName is not null && faces.TryGetFamily(familyName, out EgamiFonts.FontFamily family) ? family : null;
    }
}
